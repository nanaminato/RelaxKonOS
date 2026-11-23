"""Verify Linux OpenSSH forwarding and disconnect cleanup with loopback fixtures."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.server
import os
from pathlib import Path
import secrets
import select
import shutil
import socket
import ssl
import subprocess
from tempfile import TemporaryDirectory
from threading import Thread
import time

import paramiko


class ForwardServer(paramiko.ServerInterface):
    def __init__(self, password, target_port):
        self.password = password
        self.target_port = target_port

    def check_auth_password(self, username, password):
        if username == "fixture" and secrets.compare_digest(password, self.password):
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_direct_tcpip_request(self, channel_id, origin, destination):
        if destination == ("localhost", self.target_port):
            return paramiko.OPEN_SUCCEEDED
        return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


class HttpsFixture(http.server.BaseHTTPRequestHandler):
    def do_HEAD(self):
        self.send_response(204)
        self.end_headers()

    def log_message(self, *args):
        pass


def serve(listener, host_key, fixture, disconnect):
    connection, _ = listener.accept()
    with connection, paramiko.Transport(connection) as transport:
        transport.add_server_key(host_key)
        transport.start_server(server=fixture)
        channel = transport.accept(15)
        if channel is None:
            raise RuntimeError("OpenSSH did not open the loopback forward.")
        with channel, socket.create_connection(("127.0.0.1", fixture.target_port), timeout=5) as target:
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline and not disconnect.is_file():
                for source in select.select([channel, target], [], [], 0.1)[0]:
                    chunk = source.recv(8192)
                    if not chunk:
                        return wait_for_disconnect(disconnect, deadline)
                    (target if source is channel else channel).sendall(chunk)
            if not disconnect.is_file():
                raise RuntimeError("The client did not request the simulated disconnect.")


def wait_for_disconnect(disconnect, deadline):
    while not disconnect.is_file():
        if time.monotonic() >= deadline:
            raise RuntimeError("The client did not request the simulated disconnect.")
        time.sleep(0.05)


def run(runner, openssh):
    with TemporaryDirectory(prefix="rk-forward-") as directory:
        root = Path(directory)
        cert = root / "tls.pem"
        key = root / "tls.key"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-subj", "/CN=localhost", "-keyout", key, "-out", cert],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(cert, key)
        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), HttpsFixture) as target, \
                socket.create_server(("127.0.0.1", 0)) as listener, ThreadPoolExecutor(max_workers=1) as sessions:
            target.socket = tls.wrap_socket(target.socket, server_side=True)
            Thread(target=target.serve_forever, daemon=True).start()
            listener.settimeout(20)
            disconnect = root / "disconnect"
            host_key = paramiko.ECDSAKey.generate()
            ssh_port = listener.getsockname()[1]
            known_hosts = root / "known_hosts"
            known_hosts.write_text(f"[127.0.0.1]:{ssh_port} {host_key.get_name()} {host_key.get_base64()}\n",
                                   encoding="ascii")
            wrapper = root / "ssh"
            wrapper.write_text('#!/bin/sh\nexec "$RK_FIXTURE_OPENSSH" -F /dev/null '
                               '-o "UserKnownHostsFile=$RK_FIXTURE_KNOWN_HOSTS" '
                               '-o GlobalKnownHostsFile=/dev/null "$@"\n', encoding="utf-8")
            wrapper.chmod(0o700)
            fixture = ForwardServer(secrets.token_urlsafe(24), target.server_port)
            fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(cert.read_text(encoding="ascii"))).hexdigest()
            session = sessions.submit(serve, listener, host_key, fixture, disconnect)
            try:
                env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ["PATH"],
                           RK_FIXTURE_OPENSSH=str(openssh), RK_FIXTURE_KNOWN_HOSTS=str(known_hosts),
                           RK_FORWARD_TEST_HOST="127.0.0.1", RK_FORWARD_TEST_USER="fixture",
                           RK_FORWARD_TEST_PASSWORD=fixture.password, RK_FORWARD_TEST_TLS_SHA256=fingerprint,
                           RK_FORWARD_TEST_SSH_PORT=str(ssh_port), RK_FORWARD_TEST_TARGET_PORT=str(target.server_port),
                           RK_FORWARD_TEST_DROP_FILE=str(disconnect))
                subprocess.run(["dotnet", runner, "--forward-runtime-only"], check=True, env=env, timeout=60)
                session.result()
            finally:
                target.shutdown()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runner", type=Path)
    parser.add_argument("--openssh", type=Path, default=shutil.which("ssh"))
    args = parser.parse_args()
    if args.openssh is None:
        parser.error("OpenSSH is required. Pass --openssh with the ssh executable path.")
    run(args.runner.resolve(strict=True), args.openssh.resolve(strict=True))
