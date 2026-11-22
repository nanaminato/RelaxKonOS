"""Run the official SSH Files package check against a read-only loopback SFTP server.

Requires Paramiko and a Release build of the preview test runner. Pass the test
runner DLL and the built .roapp with its companion .sha256 file.
"""

import argparse
import base64
import hashlib
import os
import secrets
import socket
import stat
import subprocess
from concurrent.futures import ThreadPoolExecutor
from threading import Event

import paramiko


class Directory(paramiko.SFTPServerInterface):
    def canonicalize(self, path):
        return "/" + super().canonicalize(path).lstrip("/")

    def stat(self, path):
        if self.canonicalize(path) != "/":
            return paramiko.SFTP_NO_SUCH_FILE
        directory = paramiko.SFTPAttributes()
        directory.st_mode = stat.S_IFDIR | 0o555
        return directory

    lstat = stat

    def list_folder(self, path):
        if self.canonicalize(path) != "/":
            return paramiko.SFTP_NO_SUCH_FILE
        file = paramiko.SFTPAttributes()
        file.filename = "fixture.txt"
        file.st_mode = stat.S_IFREG | 0o444
        file.st_size = 0
        file.st_mtime = 1
        return [file]


class Server(paramiko.ServerInterface):
    def __init__(self, password):
        self.password = password

    def get_allowed_auths(self, username):
        return "password"

    def check_auth_password(self, username, password):
        if username == "preview" and secrets.compare_digest(password, self.password):
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


def run(runner, package):
    key = paramiko.RSAKey.generate(2048)
    password = secrets.token_urlsafe(24)
    fingerprint = base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip("=")
    stopped = Event()
    with socket.socket() as listener, ThreadPoolExecutor() as executor:
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        listener.settimeout(0.25)

        def serve_connection(connection):
            with connection, paramiko.Transport(connection) as transport:
                transport.add_server_key(key)
                transport.set_subsystem_handler("sftp", paramiko.SFTPServer, Directory)
                transport.start_server(server=Server(password))
                while transport.is_active() and not stopped.wait(0.1):
                    pass

        def accept_connections():
            while not stopped.is_set():
                try:
                    connection, _ = listener.accept()
                except TimeoutError:
                    continue
                executor.submit(serve_connection, connection)

        accept = executor.submit(accept_connections)
        try:
            environment = dict(os.environ, RK_TEST_SFTP_PORT=str(listener.getsockname()[1]),
                               RK_TEST_SFTP_PASSWORD=password, RK_TEST_SFTP_FINGERPRINT="SHA256:" + fingerprint)
            return subprocess.run(["dotnet", runner, "--official-roapp-only", package],
                                  env=environment, timeout=90).returncode
        finally:
            stopped.set()
            accept.result()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runner")
    parser.add_argument("package")
    args = parser.parse_args()
    raise SystemExit(run(args.runner, args.package))
