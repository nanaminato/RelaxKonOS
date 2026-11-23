"""Verify the installed Server Center address dialog over a loopback Paramiko SSH fixture."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import os
from pathlib import Path
import secrets
import socket
import subprocess
from threading import Event

import paramiko


class AddressServer(paramiko.ServerInterface):
    def __init__(self, password):
        self.password = password
        self.requested = Event()

    def check_auth_password(self, username, password):
        if username == "fixture-user" and password == self.password:
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_exec_request(self, channel, command):
        if command != b"hostname -I":
            return False
        self.requested.set()
        return True


def run(runner, official_apps, timeout):
    command = ["dotnet", runner] if runner.lower().endswith(".dll") else [runner]
    key = paramiko.ECDSAKey.generate()
    server = AddressServer(secrets.token_urlsafe(24))
    with socket.create_server(("127.0.0.1", 0)) as listener, ThreadPoolExecutor(max_workers=1) as executor:
        listener.settimeout(timeout)

        def serve():
            connection, _ = listener.accept()
            with connection, paramiko.Transport(connection) as transport:
                transport.add_server_key(key)
                transport.start_server(server=server)
                channel = transport.accept(10)
                if channel is None or not server.requested.wait(10):
                    raise RuntimeError("The desktop did not request the host address command.")
                with channel:
                    channel.sendall(b"2001:db8::2 invalid 192.0.2.8 192.0.2.8\n")
                    channel.send_exit_status(0)
                    channel.shutdown_write()

        session = executor.submit(serve)
        env = dict(os.environ, RK_TEST_SSH_ADDRESS_PORT=str(listener.getsockname()[1]),
                   RK_TEST_SSH_ADDRESS_PUBLIC_KEY=key.get_base64(), RK_TEST_SSH_ADDRESS_PASSWORD=server.password)
        result = subprocess.run([*command, "--server-center-package-only", official_apps], env=env, timeout=timeout)
        if result.returncode == 0:
            session.result()
        return result.returncode


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runner", type=Path)
    parser.add_argument("official_apps", type=Path)
    parser.add_argument("--timeout", type=int, default=90, help="Maximum seconds for the desktop check.")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    raise SystemExit(run(str(args.runner.resolve(strict=True)), str(args.official_apps.resolve(strict=True)), args.timeout))
