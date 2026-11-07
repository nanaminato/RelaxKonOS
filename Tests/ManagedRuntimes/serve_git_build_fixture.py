#!/usr/bin/env python3
"""Read-only Git smart HTTPS for the isolated MB-03 repository."""

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os
import ssl
import subprocess
from urllib.parse import urlsplit


class GitHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.serve_git()

    def do_POST(self):
        self.serve_git()

    def serve_git(self):
        target = urlsplit(self.path)
        if (self.command, target.path, target.query) not in {
            ("GET", "/mb03/source.git/info/refs", "service=git-upload-pack"),
            ("POST", "/mb03/source.git/git-upload-pack", ""),
        }:
            self.send_error(404)
            return

        try:
            size = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            self.send_error(400)
            return
        if not 0 <= size <= 65536 or self.headers.get("Transfer-Encoding"):
            self.send_error(400)
            return
        env = dict(os.environ, GIT_PROJECT_ROOT=self.server.repository_root,
                   GIT_HTTP_EXPORT_ALL="1", REQUEST_METHOD=self.command,
                   PATH_INFO=target.path, QUERY_STRING=target.query,
                   CONTENT_TYPE=self.headers.get("Content-Type", ""),
                   CONTENT_LENGTH=str(size), HTTP_GIT_PROTOCOL=self.headers.get("Git-Protocol", ""))
        try:
            result = subprocess.run(["git", "http-backend"], input=self.rfile.read(size),
                                    stdout=subprocess.PIPE, env=env, timeout=15, check=False)
        except subprocess.TimeoutExpired:
            self.send_error(504)
            return
        headers, separator, body = result.stdout.partition(b"\r\n\r\n")
        if result.returncode or not separator:
            self.send_error(502)
            return
        fields = [line.decode("ascii").split(":", 1) for line in headers.split(b"\r\n")]
        status = next((int(value.split()[0]) for name, value in fields if name.lower() == "status"), 200)
        self.send_response(status)
        for name, value in fields:
            if name.lower() not in {"status", "content-length"}:
                self.send_header(name, value.strip())
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("address", help="Address of the private Docker bridge")
    parser.add_argument("repository_root")
    parser.add_argument("certificate")
    parser.add_argument("key")
    args = parser.parse_args()
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(args.certificate, args.key)
    with ThreadingHTTPServer((args.address, 443), GitHandler) as server:
        server.repository_root = args.repository_root
        server.socket = context.wrap_socket(server.socket, server_side=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
