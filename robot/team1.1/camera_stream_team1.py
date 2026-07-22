#!/usr/bin/env python3
import argparse
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn


frame_buffer = None
buffer_lock = threading.Lock()


def store_frame(jpeg_bytes):
    global frame_buffer
    with buffer_lock:
        frame_buffer = jpeg_bytes


def get_frame():
    with buffer_lock:
        return frame_buffer


class CameraSourceThread(threading.Thread):
    def __init__(self, upstream_url, device_index=0, width=320, height=240):
        super().__init__(daemon=True)
        self.upstream_url = upstream_url
        self.device_index = device_index
        self.width = width
        self.height = height
        self.running = True

    def run(self):
        while self.running:
            if self.upstream_url and self.relay_upstream():
                continue
            self.capture_directly()
            time.sleep(1.0)

    def relay_upstream(self):
        print('Connecting to shared camera stream: %s' % self.upstream_url, flush=True)
        try:
            with urllib.request.urlopen(self.upstream_url, timeout=5) as response:
                buffer = bytearray()
                while self.running:
                    chunk = response.read(4096)
                    if not chunk:
                        raise RuntimeError('shared camera stream ended')
                    buffer.extend(chunk)

                    while True:
                        start = buffer.find(b'\xff\xd8')
                        if start < 0:
                            if len(buffer) > 1048576:
                                del buffer[:-2]
                            break
                        end = buffer.find(b'\xff\xd9', start + 2)
                        if end < 0:
                            if start > 0:
                                del buffer[:start]
                            break

                        store_frame(bytes(buffer[start:end + 2]))
                        del buffer[:end + 2]
            return True
        except Exception as exception:
            print('Shared camera unavailable: %s' % exception, flush=True)
            return False

    def capture_directly(self):
        try:
            import cv2
        except Exception as exception:
            print('OpenCV unavailable for direct capture: %s' % exception, flush=True)
            return

        cap = cv2.VideoCapture(self.device_index)
        if not cap.isOpened():
            print('Camera /dev/video%d is busy or unavailable' % self.device_index, flush=True)
            cap.release()
            return

        cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)
        print('Direct camera capture started: %dx%d' % (self.width, self.height), flush=True)

        try:
            while self.running:
                ok, frame = cap.read()
                if not ok:
                    break
                if frame.shape[1] != self.width or frame.shape[0] != self.height:
                    frame = cv2.resize(frame, (self.width, self.height))
                encoded, jpeg = cv2.imencode(
                    '.jpg', frame, [int(cv2.IMWRITE_JPEG_QUALITY), 80])
                if encoded:
                    store_frame(jpeg.tobytes())
                time.sleep(0.04)
        finally:
            cap.release()


class StreamingHandler(BaseHTTPRequestHandler):
    def log_message(self, format_string, *args):
        return

    def do_GET(self):
        request_path = self.path.split('?', 1)[0]

        if request_path in ('/health', '/healthz'):
            frame = get_frame()
            body = b'ok\n' if frame else b'waiting for frame\n'
            self.send_response(200 if frame else 503)
            self.send_header('Content-Type', 'text/plain')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if request_path == '/frame.jpg':
            frame = get_frame()
            if frame is None:
                self.send_error(503, 'Camera frame is not ready')
                return
            self.send_response(200)
            self.send_header('Content-Type', 'image/jpeg')
            self.send_header('Cache-Control', 'no-store, no-cache, must-revalidate')
            self.send_header('Content-Length', str(len(frame)))
            self.end_headers()
            self.wfile.write(frame)
            return

        if request_path not in ('/', '/stream.mjpg'):
            self.send_error(404)
            return

        self.send_response(200)
        self.send_header('Content-Type', 'multipart/x-mixed-replace; boundary=frame')
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        try:
            while True:
                frame = get_frame()
                if frame is None:
                    time.sleep(0.1)
                    continue
                self.wfile.write(b'--frame\r\n')
                self.wfile.write(b'Content-Type: image/jpeg\r\n')
                self.wfile.write(('Content-Length: %d\r\n\r\n' % len(frame)).encode('ascii'))
                self.wfile.write(frame)
                self.wfile.write(b'\r\n')
                time.sleep(0.05)
        except (BrokenPipeError, ConnectionResetError):
            pass


class ThreadedHTTPServer(ThreadingMixIn, HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=10002)
    parser.add_argument('--upstream', default='http://127.0.0.1:8080/')
    parser.add_argument('--device', type=int, default=0)
    args = parser.parse_args()

    source = CameraSourceThread(args.upstream, args.device)
    source.start()
    server = ThreadedHTTPServer(('', args.port), StreamingHandler)
    print('team1.1 camera service: http://0.0.0.0:%d/frame.jpg' % args.port, flush=True)
    try:
        server.serve_forever()
    finally:
        source.running = False
        server.server_close()


if __name__ == '__main__':
    main()
