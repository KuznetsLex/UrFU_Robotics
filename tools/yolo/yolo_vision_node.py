#!/usr/bin/env python3
"""Run YOLO on the freshest robot-camera frame and publish detections to Unity."""

from __future__ import annotations

import argparse
import ast
import json
import os
import socket
import sys
import threading
import time
import urllib.parse
import urllib.request
from pathlib import Path
from dataclasses import dataclass

import cv2
import numpy as np


# =============================================================================
# EDITABLE SETTINGS
# Command-line arguments override these defaults.
# =============================================================================
DEFAULT_STREAM_URL = "http://192.168.2.158:10002/frame.jpg"
DEFAULT_FALLBACK_STREAM_URL = "http://192.168.2.158:8081/"
PROJECT_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MODEL = PROJECT_ROOT / "data" / "onnx_vino" / "onnx26" / "best_int8.onnx"
DEFAULT_SOURCE_MODE = "auto"  # auto, stream or snapshot
DEFAULT_UDP_HOST = "127.0.0.1"
DEFAULT_UDP_PORT = 5005
DEFAULT_CONFIDENCE = 0.20
DEFAULT_TARGET_CLASSES = (0, 1)  # 0=ball, 1=cube, 2=robot-claw
DEFAULT_IMAGE_SIZE = 512
DEFAULT_SNAPSHOT_FPS = 10.0
DEFAULT_REQUEST_TIMEOUT_SECONDS = 2.0
DEFAULT_FAILURES_BEFORE_FALLBACK = 2
# =============================================================================


@dataclass(frozen=True)
class Detection:
    xyxy: tuple[float, float, float, float]
    confidence: float
    class_id: int


class OnnxYoloDetector:
    """Small ONNX Runtime backend for Ultralytics detect exports."""

    def __init__(self, model_path: Path) -> None:
        try:
            import onnxruntime as ort
        except ImportError as error:
            raise RuntimeError(
                "ONNX Runtime is missing. Run 'make setup' once to install it."
            ) from error

        self.session = ort.InferenceSession(
            str(model_path),
            providers=["CPUExecutionProvider"],
        )
        model_input = self.session.get_inputs()[0]
        self.input_name = model_input.name
        self.input_height = int(model_input.shape[2])
        self.input_width = int(model_input.shape[3])
        output_shape = self.session.get_outputs()[0].shape
        self.end_to_end = len(output_shape) == 3 and output_shape[-1] == 6
        metadata = self.session.get_modelmeta().custom_metadata_map
        try:
            parsed_names = ast.literal_eval(metadata.get("names", "{}"))
            self.names = {int(key): str(value) for key, value in parsed_names.items()}
        except (SyntaxError, ValueError, AttributeError):
            self.names = {}

    @staticmethod
    def _letterbox(
        frame: np.ndarray,
        target_width: int,
        target_height: int,
    ) -> tuple[np.ndarray, float, float, float]:
        height, width = frame.shape[:2]
        scale = min(target_width / width, target_height / height)
        resized_width = int(round(width * scale))
        resized_height = int(round(height * scale))
        resized = cv2.resize(frame, (resized_width, resized_height), interpolation=cv2.INTER_LINEAR)
        pad_x = (target_width - resized_width) / 2.0
        pad_y = (target_height - resized_height) / 2.0
        left = int(round(pad_x - 0.1))
        right = int(round(pad_x + 0.1))
        top = int(round(pad_y - 0.1))
        bottom = int(round(pad_y + 0.1))
        padded = cv2.copyMakeBorder(
            resized,
            top,
            bottom,
            left,
            right,
            cv2.BORDER_CONSTANT,
            value=(114, 114, 114),
        )
        rgb = cv2.cvtColor(padded, cv2.COLOR_BGR2RGB)
        tensor = np.ascontiguousarray(rgb.transpose(2, 0, 1), dtype=np.float32) / 255.0
        return tensor[None, ...], scale, float(left), float(top)

    def predict(
        self,
        frame: np.ndarray,
        confidence: float,
        target_classes: list[int],
    ) -> list[Detection]:
        tensor, scale, pad_x, pad_y = self._letterbox(
            frame,
            self.input_width,
            self.input_height,
        )
        raw = self.session.run(None, {self.input_name: tensor})[0][0]
        if self.end_to_end:
            candidates = self._decode_end_to_end(raw, confidence, target_classes)
        else:
            candidates = self._decode_standard(raw, confidence, target_classes)

        frame_height, frame_width = frame.shape[:2]
        detections: list[Detection] = []
        for candidate in candidates:
            x1, y1, x2, y2 = candidate.xyxy
            mapped_box = (
                float(np.clip((x1 - pad_x) / scale, 0, frame_width)),
                float(np.clip((y1 - pad_y) / scale, 0, frame_height)),
                float(np.clip((x2 - pad_x) / scale, 0, frame_width)),
                float(np.clip((y2 - pad_y) / scale, 0, frame_height)),
            )
            detections.append(Detection(mapped_box, candidate.confidence, candidate.class_id))
        return detections

    @staticmethod
    def _decode_end_to_end(
        output: np.ndarray,
        confidence: float,
        target_classes: list[int],
    ) -> list[Detection]:
        detections = []
        for x1, y1, x2, y2, score, class_id_value in output:
            class_id = int(round(float(class_id_value)))
            if float(score) >= confidence and class_id in target_classes:
                detections.append(
                    Detection(
                        (float(x1), float(y1), float(x2), float(y2)),
                        float(score),
                        class_id,
                    )
                )
        return detections

    @staticmethod
    def _decode_standard(
        output: np.ndarray,
        confidence: float,
        target_classes: list[int],
    ) -> list[Detection]:
        predictions = output.T
        class_scores = predictions[:, 4:]
        class_ids = np.argmax(class_scores, axis=1)
        scores = class_scores[np.arange(len(predictions)), class_ids]
        keep = (scores >= confidence) & np.isin(class_ids, target_classes)
        predictions = predictions[keep]
        scores = scores[keep]
        class_ids = class_ids[keep]
        if len(predictions) == 0:
            return []

        xywh = predictions[:, :4]
        xyxy = np.empty_like(xywh)
        xyxy[:, 0] = xywh[:, 0] - xywh[:, 2] * 0.5
        xyxy[:, 1] = xywh[:, 1] - xywh[:, 3] * 0.5
        xyxy[:, 2] = xywh[:, 0] + xywh[:, 2] * 0.5
        xyxy[:, 3] = xywh[:, 1] + xywh[:, 3] * 0.5
        nms_boxes = [
            [float(x1), float(y1), float(x2 - x1), float(y2 - y1)]
            for x1, y1, x2, y2 in xyxy
        ]
        kept_indices = cv2.dnn.NMSBoxes(nms_boxes, scores.tolist(), confidence, 0.45)
        if len(kept_indices) == 0:
            return []
        return [
            Detection(
                tuple(float(value) for value in xyxy[int(index)]),
                float(scores[int(index)]),
                int(class_ids[int(index)]),
            )
            for index in np.asarray(kept_indices).reshape(-1)
        ]


class UltralyticsDetector:
    """Optional PyTorch backend used when a .pt model is explicitly selected."""

    def __init__(self, model_path: Path, track: bool, image_size: int, device: str | None) -> None:
        try:
            from ultralytics import YOLO
        except ImportError as error:
            raise RuntimeError(
                "A .pt model needs Ultralytics/PyTorch. Install it with "
                ".\\tools\\yolo\\.venv\\Scripts\\python -m pip install ultralytics, "
                "or use the default INT8 ONNX model."
            ) from error
        self.model = YOLO(str(model_path))
        self.names = self.model.names
        self.track = track
        self.image_size = image_size
        self.device = device

    def predict(
        self,
        frame: np.ndarray,
        confidence: float,
        target_classes: list[int],
    ) -> list[Detection]:
        arguments = {
            "source": frame,
            "conf": confidence,
            "classes": target_classes,
            "imgsz": self.image_size,
            "verbose": False,
        }
        if self.device:
            arguments["device"] = self.device
        if self.track:
            results = self.model.track(**arguments, persist=True, tracker="bytetrack.yaml")
        else:
            results = self.model.predict(**arguments)

        boxes = results[0].boxes
        if boxes is None or len(boxes) == 0:
            return []
        coordinates = boxes.xyxy.detach().cpu().numpy()
        confidences = boxes.conf.detach().cpu().numpy()
        class_ids = boxes.cls.detach().cpu().numpy()
        return [
            Detection(
                tuple(float(value) for value in coordinates[index]),
                float(confidences[index]),
                int(class_ids[index]),
            )
            for index in range(len(coordinates))
        ]


class LatestFrameCapture:
    """Continuously fetch a stream while retaining only its newest frame."""

    def __init__(
        self,
        url: str,
        fallback_url: str,
        source_mode: str,
        snapshot_fps: float,
        request_timeout: float,
    ) -> None:
        self.sources = [(url, self._resolve_mode(url, source_mode))]
        if fallback_url and fallback_url != url:
            self.sources.append((fallback_url, self._resolve_mode(fallback_url, "auto")))
        self.snapshot_interval = 1.0 / max(snapshot_fps, 0.1)
        self.request_timeout = request_timeout
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._frame: np.ndarray | None = None
        self._frame_id = 0
        self._last_error = "waiting for camera"
        self._thread = threading.Thread(
            target=self._run,
            name="robot-camera-capture",
            daemon=True,
        )

    @staticmethod
    def _resolve_mode(url: str, requested: str) -> str:
        if requested != "auto":
            return requested
        path = urllib.parse.urlparse(url).path.lower()
        return "snapshot" if path.endswith((".jpg", ".jpeg")) else "stream"

    @property
    def last_error(self) -> str:
        with self._lock:
            return self._last_error

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._thread.join(timeout=2.0)

    def read_latest(self, previous_id: int) -> tuple[int, np.ndarray | None]:
        with self._lock:
            if self._frame is None or self._frame_id == previous_id:
                return previous_id, None
            return self._frame_id, self._frame.copy()

    def _store(self, frame: np.ndarray) -> None:
        with self._lock:
            self._frame = frame
            self._frame_id += 1
            self._last_error = ""

    def _set_error(self, message: str) -> None:
        with self._lock:
            self._last_error = message

    def _run(self) -> None:
        source_index = 0
        while not self._stop.is_set():
            url, mode = self.sources[source_index]
            if mode == "snapshot":
                self._run_snapshot_source(url)
            else:
                self._run_stream_source(url)

            source_index = (source_index + 1) % len(self.sources)
            self._stop.wait(0.2)

    def _run_snapshot_source(self, url: str) -> None:
        consecutive_failures = 0
        while not self._stop.is_set():
            started_at = time.monotonic()
            try:
                request = urllib.request.Request(
                    url,
                    headers={"Cache-Control": "no-cache", "User-Agent": "UrFU-YOLO/1.0"},
                )
                with urllib.request.urlopen(request, timeout=self.request_timeout) as response:
                    encoded = np.frombuffer(response.read(), dtype=np.uint8)
                frame = cv2.imdecode(encoded, cv2.IMREAD_COLOR)
                if frame is None:
                    raise RuntimeError("camera response is not a JPEG image")
                self._store(frame)
                consecutive_failures = 0
            except Exception as error:  # Network failures are expected while the robot reconnects.
                consecutive_failures += 1
                self._set_error(f"{url}: {error}")
                if consecutive_failures >= DEFAULT_FAILURES_BEFORE_FALLBACK:
                    return

            remaining = self.snapshot_interval - (time.monotonic() - started_at)
            self._stop.wait(max(remaining, 0.01))

    def _run_stream_source(self, url: str) -> None:
        capture = cv2.VideoCapture(url)
        capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        if not capture.isOpened():
            self._set_error(f"cannot open video stream: {url}")
            capture.release()
            return

        while not self._stop.is_set():
            ok, frame = capture.read()
            if not ok or frame is None:
                self._set_error(f"camera stream disconnected: {url}")
                break
            self._store(frame)

        capture.release()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run YOLO on an HTTP robot-camera feed and send JSON over UDP.",
    )
    parser.add_argument(
        "--stream-url",
        default=os.environ.get("ROBOT_CAMERA_URL", DEFAULT_STREAM_URL),
        help="MJPEG/video URL or repeatedly fetched JPEG URL",
    )
    parser.add_argument(
        "--fallback-stream-url",
        default=os.environ.get("ROBOT_CAMERA_FALLBACK_URL", DEFAULT_FALLBACK_STREAM_URL),
        help="camera URL used after the primary source fails",
    )
    parser.add_argument(
        "--source-mode",
        choices=("auto", "stream", "snapshot"),
        default=DEFAULT_SOURCE_MODE,
        help="auto treats .jpg/.jpeg URLs as snapshots and all others as streams",
    )
    parser.add_argument("--model", default=str(DEFAULT_MODEL), help="INT8 ONNX or Ultralytics .pt model path")
    parser.add_argument("--udp-host", default=DEFAULT_UDP_HOST)
    parser.add_argument("--udp-port", type=int, default=DEFAULT_UDP_PORT)
    parser.add_argument("--confidence", type=float, default=DEFAULT_CONFIDENCE)
    parser.add_argument("--target-class", type=int, action="append", default=None)
    parser.add_argument("--image-size", type=int, default=DEFAULT_IMAGE_SIZE)
    parser.add_argument("--device", default=None, help="for example: 0, cpu, cuda, mps")
    parser.add_argument("--snapshot-fps", type=float, default=DEFAULT_SNAPSHOT_FPS)
    parser.add_argument("--request-timeout", type=float, default=DEFAULT_REQUEST_TIMEOUT_SECONDS)
    parser.add_argument("--no-track", action="store_true", help="disable ByteTrack for the optional .pt backend")
    parser.add_argument("--no-display", action="store_true", help="run without an OpenCV window")
    return parser.parse_args()


def detection_packet(
    frame: np.ndarray,
    detections: list[Detection],
) -> tuple[dict[str, float], tuple[int, int, int, int] | None]:
    height, width = frame.shape[:2]
    packet: dict[str, float] = {
        "angle": 0.0,
        "bbox_area_ratio": 0.0,
        "bbox_aspect_ratio": 0.0,
        "sees": 0.0,
        "conf": 0.0,
        "w": 0.0,
        "h": 0.0,
        "x1": 0.0,
        "y1": 0.0,
        "x2": 0.0,
        "y2": 0.0,
        "frame_w": float(width),
        "frame_h": float(height),
    }

    if not detections:
        return packet, None

    best_detection = max(
        detections,
        key=lambda item: max(0.0, item.xyxy[2] - item.xyxy[0])
        * max(0.0, item.xyxy[3] - item.xyxy[1]),
    )
    x1, y1, x2, y2 = best_detection.xyxy
    box_width = max(0.0, x2 - x1)
    box_height = max(0.0, y2 - y1)
    center_x = (x1 + x2) * 0.5
    confidence = best_detection.confidence
    bbox_area_ratio = (box_width * box_height) / (width * height)
    # Pixel width / pixel height. The simulator applies the frame aspect ratio
    # to its normalized viewport box to produce the same quantity.
    bbox_aspect_ratio = box_width / box_height if box_height > 0.0 else 0.0

    packet.update(
        angle=float(np.clip((center_x - width * 0.5) / (width * 0.5), -1.0, 1.0)),
        bbox_area_ratio=float(np.clip(bbox_area_ratio, 0.0, 1.0)),
        bbox_aspect_ratio=float(bbox_aspect_ratio),
        sees=1.0,
        conf=confidence,
        w=box_width,
        h=box_height,
        x1=x1,
        y1=y1,
        x2=x2,
        y2=y2,
    )
    integer_box = tuple(int(round(value)) for value in (x1, y1, x2, y2))
    return packet, integer_box


def draw_overlay(
    frame: np.ndarray,
    packet: dict[str, float],
    box: tuple[int, int, int, int] | None,
    fps: float,
) -> np.ndarray:
    output = frame.copy()
    if box is not None:
        x1, y1, x2, y2 = box
        cv2.rectangle(output, (x1, y1), (x2, y2), (40, 220, 40), 2)
        label = f"ball {packet['conf']:.2f}"
        cv2.putText(
            output,
            label,
            (x1, max(20, y1 - 7)),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.6,
            (40, 220, 40),
            2,
            cv2.LINE_AA,
        )
    cv2.putText(
        output,
        f"YOLO {fps:.1f} FPS",
        (10, 25),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.65,
        (0, 255, 255),
        2,
        cv2.LINE_AA,
    )
    return output


def main() -> int:
    args = parse_args()
    model_path = Path(args.model).expanduser().resolve()
    if not model_path.exists():
        print(f"Model not found: {model_path}", file=sys.stderr)
        return 2

    target_classes = args.target_class if args.target_class is not None else list(DEFAULT_TARGET_CLASSES)
    print(f"Loading model: {model_path}")
    try:
        if model_path.suffix.lower() == ".onnx":
            detector = OnnxYoloDetector(model_path)
        else:
            detector = UltralyticsDetector(
                model_path,
                track=not args.no_track,
                image_size=args.image_size,
                device=args.device,
            )
    except RuntimeError as error:
        print(str(error), file=sys.stderr)
        return 2
    print(f"Classes: {detector.names}")
    print(f"Camera: {args.stream_url} ({args.source_mode})")
    print(f"Camera fallback: {args.fallback_stream_url}")
    print(f"Unity UDP: {args.udp_host}:{args.udp_port}")
    if not args.no_display:
        print("Press Q or Esc in the video window to stop.")

    capture = LatestFrameCapture(
        args.stream_url,
        args.fallback_stream_url,
        args.source_mode,
        args.snapshot_fps,
        args.request_timeout,
    )
    udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    capture.start()

    previous_frame_id = -1
    smoothed_fps = 0.0
    last_inference_at = time.perf_counter()
    last_status_at = 0.0

    try:
        while True:
            frame_id, frame = capture.read_latest(previous_frame_id)
            if frame is None:
                now = time.monotonic()
                if now - last_status_at >= 2.0:
                    print(f"Waiting for camera: {capture.last_error}")
                    last_status_at = now
                if not args.no_display and (cv2.waitKey(1) & 0xFF) in (ord("q"), 27):
                    break
                time.sleep(0.005)
                continue

            previous_frame_id = frame_id
            detections = detector.predict(frame, args.confidence, target_classes)
            packet, best_box = detection_packet(frame, detections)
            udp_socket.sendto(
                json.dumps(packet, separators=(",", ":")).encode("utf-8"),
                (args.udp_host, args.udp_port),
            )

            now = time.perf_counter()
            instantaneous_fps = 1.0 / max(now - last_inference_at, 1e-6)
            smoothed_fps = instantaneous_fps if smoothed_fps == 0.0 else 0.9 * smoothed_fps + 0.1 * instantaneous_fps
            last_inference_at = now

            if now - last_status_at >= 1.0:
                target_status = f"ball conf={packet['conf']:.2f}" if packet["sees"] > 0.5 else "no ball"
                print(f"{smoothed_fps:5.1f} FPS | {target_status}")
                last_status_at = now

            if not args.no_display:
                cv2.imshow("UrFU robot - YOLO", draw_overlay(frame, packet, best_box, smoothed_fps))
                if (cv2.waitKey(1) & 0xFF) in (ord("q"), 27):
                    break
    except KeyboardInterrupt:
        pass
    finally:
        capture.stop()
        udp_socket.close()
        cv2.destroyAllWindows()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
