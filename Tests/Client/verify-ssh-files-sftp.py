"""Run the official SSH Files package check against an in-memory loopback SFTP server.

Requires Paramiko and a Release build of the preview test runner. Pass the test
runner DLL and the built .roapp with its companion .sha256 file.
"""

import argparse
import base64
import hashlib
import io
import os
import posixpath
import secrets
import socket
import stat
import subprocess
from concurrent.futures import ThreadPoolExecutor
from threading import Event

import paramiko


class UploadHandle(paramiko.SFTPHandle):
    def __init__(self, flags, files, name):
        super().__init__(flags)
        self.files = files
        self.name = name
        self.writefile = io.BytesIO()

    def close(self):
        self.files[self.name] = self.writefile.getvalue()
        super().close()


class Directory(paramiko.SFTPServerInterface):
    def __init__(self, server, files, directories):
        super().__init__(server)
        self.files = files
        self.directories = directories

    def canonicalize(self, path):
        return "/" + super().canonicalize(path).lstrip("/")

    def stat(self, path):
        path = self.canonicalize(path)
        if path in self.files:
            return self.file_attributes(path)
        if path not in self.directories:
            return paramiko.SFTP_NO_SUCH_FILE
        directory = paramiko.SFTPAttributes()
        directory.filename = posixpath.basename(path)
        directory.st_mode = stat.S_IFDIR | 0o555
        return directory

    lstat = stat

    def list_folder(self, path):
        path = self.canonicalize(path)
        if path not in self.directories:
            return paramiko.SFTP_NO_SUCH_FILE
        return [self.stat(child) for child in sorted(self.files.keys() | self.directories)
                if child != path and posixpath.dirname(child) == path]

    def mkdir(self, path, attr):
        path = self.canonicalize(path)
        if path not in {"/tree", "/tree/nested", "/tree/empty"}:
            return paramiko.SFTP_PERMISSION_DENIED
        if path in self.directories or posixpath.dirname(path) not in self.directories:
            return paramiko.SFTP_FAILURE
        self.directories.add(path)
        return paramiko.SFTP_OK

    def open(self, path, flags, attr):
        path = self.canonicalize(path)
        if (path in {"/upload.txt", "/tree/root.txt", "/tree/nested/child.txt", "/tree/fixture.txt"}
                and flags & os.O_CREAT and flags & os.O_EXCL
                and path not in self.files and posixpath.dirname(path) in self.directories):
            return UploadHandle(flags, self.files, path)
        if flags != os.O_RDONLY:
            return paramiko.SFTP_PERMISSION_DENIED
        if path not in self.files:
            return paramiko.SFTP_NO_SUCH_FILE
        handle = paramiko.SFTPHandle(flags)
        handle.readfile = io.BytesIO(self.files[path])
        return handle

    def file_attributes(self, path):
        file = paramiko.SFTPAttributes()
        file.filename = posixpath.basename(path)
        file.st_mode = stat.S_IFREG | 0o644
        file.st_size = len(self.files[path])
        file.st_mtime = 1
        return file

    def rename(self, oldpath, newpath):
        oldpath, newpath = map(self.canonicalize, (oldpath, newpath))
        if (oldpath, newpath) not in {("/upload.txt", "/renamed.txt"), ("/tree/fixture.txt", "/tree/empty/fixture.txt")}:
            return paramiko.SFTP_PERMISSION_DENIED
        if oldpath not in self.files:
            return paramiko.SFTP_NO_SUCH_FILE
        if newpath in self.files:
            return paramiko.SFTP_FAILURE
        self.files[newpath] = self.files.pop(oldpath)
        return paramiko.SFTP_OK

    def remove(self, path):
        path = self.canonicalize(path)
        if path != "/renamed.txt":
            return paramiko.SFTP_PERMISSION_DENIED
        if self.files.pop(path, None) is None:
            return paramiko.SFTP_NO_SUCH_FILE
        return paramiko.SFTP_OK


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
    files = {"/fixture.txt": b"RelaxKonOS SFTP fixture\n"}
    directories = {"/"}
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
                transport.set_subsystem_handler("sftp", paramiko.SFTPServer, Directory, files, directories)
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
