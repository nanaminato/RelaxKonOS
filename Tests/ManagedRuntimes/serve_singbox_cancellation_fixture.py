#!/usr/bin/env python3
"""HTTPS fixture that pauses reviewed sing-box archives after 64 KiB for cancellation acceptance.

Serves only catalog.json and the two named archives. Does not configure host trust,
install software or accept control requests over HTTP. The gate file is local test evidence.
"""
import argparse
import functools
import http.server
import ipaddress
import json
from pathlib import Path
import shutil
import ssl
import time


ALLOWED = {"/catalog.json", "/sing-box-1.13.21-linux-amd64.tar.gz", "/sing-box-1.14.3-linux-amd64.tar.gz"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", required=True)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--certificate", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--gate-file", required=True, type=Path)
    parser.add_argument("--pause-seconds", type=int, default=45)
    args = parser.parse_args()
    address = ipaddress.ip_address(args.bind)
    if address.version != 4 or not any(address in ipaddress.ip_network(cidr) for cidr in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")):
        parser.error("an exact RFC1918 IPv4 test host is required")
    if not 1024 < args.port < 65536 or not 1 <= args.pause_seconds <= 120:
        parser.error("invalid fixture port or bounded pause")
    if not args.directory.is_dir() or not args.gate_file.is_absolute() or not args.gate_file.parent.is_dir() or args.gate_file.exists():
        parser.error("an existing fixture directory and a fresh absolute local gate file are required")

    class Handler(http.server.SimpleHTTPRequestHandler):
        def do_GET(self):
            if self.path not in ALLOWED:
                self.send_error(404)
                return
            super().do_GET()

        def do_HEAD(self):
            if self.path not in ALLOWED:
                self.send_error(404)
                return
            super().do_HEAD()

        def copyfile(self, source, output):
            try:
                if self.path.endswith(".tar.gz"):
                    initial = source.read(65536)
                    output.write(initial)
                    output.flush()
                    temporary = args.gate_file.with_suffix(".tmp")
                    temporary.write_text(json.dumps({"path": self.path, "bytesSent": len(initial)}))
                    temporary.replace(args.gate_file)
                    time.sleep(args.pause_seconds)
                shutil.copyfileobj(source, output)
            except (BrokenPipeError, ConnectionResetError):
                pass

    server = http.server.ThreadingHTTPServer((args.bind, args.port), functools.partial(Handler, directory=str(args.directory)))
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(args.certificate, args.key)
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
