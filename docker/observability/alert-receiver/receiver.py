import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


received = []
lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/health":
            self._json(200, {"status": "ok"})
            return
        if self.path == "/received":
            with lock:
                payload = list(received)
            self._json(200, payload)
            return
        self._json(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/alerts":
            self._json(404, {"error": "not found"})
            return
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        try:
            payload = json.loads(body)
        except json.JSONDecodeError:
            self._json(400, {"error": "invalid json"})
            return
        with lock:
            received.append(payload)
        self._json(200, {"accepted": True})

    def log_message(self, format, *args):
        return

    def _json(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
