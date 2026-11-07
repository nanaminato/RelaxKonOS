"""TLS relay for the private Pebble fixture. Truncate only an explicitly selected response."""

import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import ssl
import sys
import time


root = Path(sys.argv[1])
schedule = sys.argv[2:] == ["schedule"]
full_server = schedule or sys.argv[2:] == ["full-server"]
ca_context = ssl.create_default_context(cafile=str(root / "https.pem"))


class Relay(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def relay(self):
        length = int(self.headers.get("Content-Length", "0"))
        headers = {name: self.headers[name] for name in ("Host", "Content-Type", "User-Agent")
                   if name in self.headers}
        upstream = http.client.HTTPSConnection("127.0.0.2" if full_server else "localhost",
                                               14000, context=ca_context, timeout=15)
        try:
            upstream.request(self.command, self.path, self.rfile.read(length) if length else None, headers)
            response = upstream.getresponse()
            body = response.read()
            if schedule and self.path == "/dir" and response.status == 200:
                directory = json.loads(body)
                directory.pop("renewalInfo", None)
                body = json.dumps(directory).encode()
            selected = self.headers.get("X-Fixture-Lose-Response")
            if full_server:
                selected = (root / "response-fault").read_text(encoding="ascii").strip()
            if schedule and selected == "hold-challenge" and self.path.startswith("/chalZ/") and response.status == 200:
                (root / "challenge-response-held").write_text("200", encoding="ascii")
                deadline = time.monotonic() + 25
                while not (root / "release-challenge-response").is_file():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The renewal shutdown fixture did not release the challenge response")
                    time.sleep(0.05)
            truncate = (
                selected == "order" and self.path == "/order-plz" and response.status == 201 and response.getheader("Location")
                or selected == "certificate" and self.path.startswith("/certZ/") and response.status == 200
                and body.startswith(b"-----BEGIN CERTIFICATE-----")
            )
            self.send_response(response.status)
            for name, value in response.getheaders():
                if name.lower() not in ("connection", "content-length", "transfer-encoding"):
                    self.send_header(name, value)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            if truncate:
                (root / f"truncated-{selected}").write_text(str(response.status), encoding="ascii")
                body = body[:len(body) // 2]
            if self.command != "HEAD":
                self.wfile.write(body)
        finally:
            upstream.close()

    do_GET = do_HEAD = do_POST = relay


with ThreadingHTTPServer(("127.0.0.1", 14000 if full_server else 14001), Relay) as server:
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(root / "https.pem", root / "https.key")
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    server.serve_forever()
