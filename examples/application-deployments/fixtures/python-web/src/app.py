"""RelaxKonOS application-deployment fixture: a Flask web workload with pinned dependencies.

The PythonProject template requires:
  * a requirements.txt at the publish root whose every non-comment line is pinned (``==`` or a
    direct artifact with a ``#sha256=`` fragment), because dependencies are installed at image
    build time only and a container start must never reach a package index,
  * a program entry naming an importable module, resolved as ``python -m <entry>``.

The template does not inject a port variable, so this workload reads ``PORT`` from the environment
and falls back to 8080. Add a non-secret configuration entry ``PORT`` when the deployment definition
uses a container port other than 8080.
"""

import os
import platform
from datetime import datetime, timezone

from flask import Flask, jsonify

app = Flask(__name__)


def _port() -> int:
    raw = os.environ.get("PORT", "8080").strip()
    port = int(raw)
    if not 1 <= port <= 65535:
        raise ValueError(f"PORT is not a usable TCP port: {raw}")
    return port


@app.get("/healthz")
def healthz():
    return "ok", 200


@app.get("/")
def index():
    return jsonify(
        app="relaxkonos-ad-python-web",
        python=platform.python_version(),
        flask=__import__("flask").__version__,
        host=platform.node(),
        time=datetime.now(timezone.utc).isoformat(),
    )


@app.errorhandler(404)
def not_found(_error):
    # A wrong healthCheckPath must fail the readiness check rather than answer 200 by accident.
    return "not found", 404


if __name__ == "__main__":
    listening = _port()
    print(f"[python-web] listening on 0.0.0.0:{listening}", flush=True)
    print(f"[python-web] python {platform.python_version()}", flush=True)
    print(f"[python-web] health path /healthz", flush=True)
    app.run(host="0.0.0.0", port=listening, debug=False, use_reloader=False)
