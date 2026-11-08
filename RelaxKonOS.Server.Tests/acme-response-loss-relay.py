"""TLS relay for the private Pebble fixture. Truncate only an explicitly selected response."""

import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import ssl
import sys
import time
from urllib.parse import urlsplit


root = Path(sys.argv[1])
schedule = sys.argv[2:] == ["schedule"]
website = sys.argv[2:] == ["website"]
full_server = schedule or website or sys.argv[2:] == ["full-server"]
ca_context = ssl.create_default_context(cafile=str(root / "https.pem"))
revocation_path = None
renewal_info_path = None


class Relay(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def relay(self):
        global revocation_path, renewal_info_path
        length = int(self.headers.get("Content-Length", "0"))
        headers = {name: self.headers[name] for name in ("Host", "Content-Type", "User-Agent")
                   if name in self.headers}
        upstream = http.client.HTTPSConnection("127.0.0.2" if full_server else "localhost",
                                               14000, context=ca_context, timeout=15)
        try:
            upstream.request(self.command, self.path, self.rfile.read(length) if length else None, headers)
            response = upstream.getresponse()
            body = response.read()
            if self.path == "/dir" and response.status == 200:
                directory = json.loads(body)
                revocation_path = urlsplit(directory.get("revokeCert", "")).path
                renewal_info_path = urlsplit(directory.get("renewalInfo", "")).path.rstrip("/")
                if schedule:
                    directory.pop("renewalInfo", None)
                    body = json.dumps(directory).encode()
            selected = self.headers.get("X-Fixture-Lose-Response")
            if full_server:
                selected = (root / "response-fault").read_text(encoding="ascii").strip()
            ari_resource = renewal_info_path and self.path.startswith(renewal_info_path + "/")
            ari_lookup = self.command == "GET" and ari_resource and self.path != renewal_info_path + "/"
            ari_window = json.loads(body).get("suggestedWindow") if ari_lookup and response.status == 200 \
                and selected in ("hold-ari", "ari") else None
            if ari_resource or (renewal_info_path and self.path == renewal_info_path):
                with (root / "ari-results").open("a", encoding="ascii") as results:
                    results.write(f"{self.command} {self.path} {response.status}\n")
            if selected == "hold-ari" and ari_window:
                (root / "ari-response-held").write_text(json.dumps({"path": self.path, "window": ari_window}), encoding="ascii")
                deadline = time.monotonic() + 15
                while not (root / "release-ari-response").is_file():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The renewal information response was not released")
                    time.sleep(0.05)
            if self.command == "POST" and self.path == revocation_path:
                with (root / "revocation-results").open("a", encoding="ascii") as results:
                    results.write(f"{response.status}\n")
                if response.status == 200 and selected == "lose-revocation":
                    self.close_connection = True
                    return
                if response.status == 200 and selected == "hold-revocation":
                    (root / "revocation-response-held").touch()
                    deadline = time.monotonic() + 15
                    while not (root / "release-revocation-response").is_file():
                        if time.monotonic() >= deadline:
                            raise TimeoutError("The revocation response was not released")
                        time.sleep(0.05)
            if selected == "hold-observation" and self.command == "POST" and self.path.startswith("/my-order/") and response.status == 200:
                (root / "order-observation-held").write_text(self.path, encoding="ascii")
                deadline = time.monotonic() + 10
                while not (root / "release-order-observation").is_file():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The order observation response was not released")
                    time.sleep(0.05)
            if schedule and selected == "hold-challenge" and self.path.startswith("/chalZ/") and response.status == 200:
                (root / "challenge-response-held").write_text("200", encoding="ascii")
                deadline = time.monotonic() + 25
                while not (root / "release-challenge-response").is_file():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The renewal shutdown fixture did not release the challenge response")
                    time.sleep(0.05)
            if (website or selected == "hold-renewal-certificate") and self.path.startswith("/certZ/") \
                    and response.status == 200 and body.startswith(b"-----BEGIN CERTIFICATE-----"):
                (root / "certificate-response-held").touch()
                deadline = time.monotonic() + 25
                while not (root / "release-certificate-response").is_file():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The website fixture did not release the certificate response")
                    time.sleep(0.05)
            truncate = (
                selected == "order" and self.path == "/order-plz" and response.status == 201 and response.getheader("Location")
                or selected == "ari" and ari_window
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
