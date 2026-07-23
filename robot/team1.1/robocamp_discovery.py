#!/usr/bin/env python3
import socketserver


DISCOVERY_RESPONSE = b"ROBOCAMP_158\n"


class DiscoveryHandler(socketserver.BaseRequestHandler):
    def handle(self):
        self.request.sendall(DISCOVERY_RESPONSE)


class DiscoveryServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    with DiscoveryServer(("0.0.0.0", 10003), DiscoveryHandler) as server:
        server.serve_forever()
