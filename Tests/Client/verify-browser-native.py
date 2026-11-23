"""Verify native WebView navigation in the official Windows Browser package."""

import argparse
import os
import subprocess
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Event, Thread


def run(runner, package):
    completed = Event()

    class Page(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/":
                content = b"<script>location.replace('/complete')</script>"
            elif self.path == "/complete":
                completed.set()
                content = b"<title>RelaxKonOS native browser acceptance</title>"
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)

    with (ThreadingHTTPServer(("127.0.0.1", 0), Page) as server,
          tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as profile):
        worker = Thread(target=server.serve_forever)
        worker.start()
        try:
            result = subprocess.run(
                [os.path.abspath(runner), "--official-browser-native", package,
                 f"http://127.0.0.1:{server.server_port}/"],
                env=dict(os.environ, WEBVIEW2_USER_DATA_FOLDER=profile),
                timeout=60,
            )
            if result.returncode == 0 and not completed.is_set():
                raise RuntimeError("The loopback server did not observe the script navigation")
            return result.returncode
        finally:
            server.shutdown()
            worker.join()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runner", help="Windows Release test executable with its embedded application manifest")
    parser.add_argument("package")
    args = parser.parse_args()
    raise SystemExit(run(args.runner, args.package))
