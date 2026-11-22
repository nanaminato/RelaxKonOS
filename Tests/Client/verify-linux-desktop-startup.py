"""Verify a published desktop opens and closes an X11 window in Minimal mode."""

import argparse
import ctypes as c
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time


class ClientMessage(c.Structure):
    _fields_ = [
        ("type", c.c_int), ("serial", c.c_ulong), ("send_event", c.c_int),
        ("display", c.c_void_p), ("window", c.c_ulong), ("message_type", c.c_ulong),
        ("format", c.c_int), ("data", c.c_long * 5),
    ]


class XEvent(c.Union):
    _fields_ = [("client", ClientMessage), ("padding", c.c_long * 24)]


def request_close(window):
    x11 = c.CDLL("libX11.so.6")
    x11.XOpenDisplay.argtypes = [c.c_char_p]
    x11.XOpenDisplay.restype = c.c_void_p
    x11.XInternAtom.argtypes = [c.c_void_p, c.c_char_p, c.c_int]
    x11.XInternAtom.restype = c.c_ulong
    x11.XSendEvent.argtypes = [c.c_void_p, c.c_ulong, c.c_int, c.c_long, c.POINTER(XEvent)]
    x11.XCloseDisplay.argtypes = [c.c_void_p]
    display = x11.XOpenDisplay(None)
    if not display:
        raise RuntimeError("Cannot connect to the X11 display.")
    try:
        event = XEvent()
        event.client.type = 33  # X11 ClientMessage
        event.client.display = display
        event.client.window = int(window, 16)
        event.client.message_type = x11.XInternAtom(display, b"WM_PROTOCOLS", False)
        event.client.format = 32
        event.client.data[0] = x11.XInternAtom(display, b"WM_DELETE_WINDOW", False)
        if not x11.XSendEvent(display, event.client.window, False, 0, c.byref(event)):
            raise RuntimeError("Cannot send the window close request.")
    finally:
        x11.XCloseDisplay(display)


def find_window(process):
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"Desktop exited before opening a window (status {process.returncode}).")
        tree = subprocess.check_output(["xwininfo", "-root", "-tree"], text=True, timeout=5)
        for window in re.findall(r'^\s+(0x[\da-f]+)\s+"RelaxKonOS":', tree, re.MULTILINE):
            properties = subprocess.run(
                ["xprop", "-id", window, "_NET_WM_PID", "WM_PROTOCOLS"],
                capture_output=True, text=True, timeout=5,
            )
            if properties.returncode or not re.search(rf"^_NET_WM_PID\(CARDINAL\) = {process.pid}$",
                                                       properties.stdout, re.MULTILINE):
                continue
            state = subprocess.run(["xwininfo", "-id", window], capture_output=True, text=True, timeout=5)
            if "WM_DELETE_WINDOW" in properties.stdout and "Map State: IsViewable" in state.stdout:
                return window
        time.sleep(0.2)
    raise RuntimeError("No visible RelaxKonOS window belonging to the test process appeared.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("publish_directory", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--apphost", action="store_true", help="Run the published RelaxKonOS executable directly.")
    args = parser.parse_args()
    publish = args.publish_directory.resolve(strict=True)
    entry = publish / ("RelaxKonOS" if args.apphost else "RelaxKonOS.dll")
    command = [str(entry.resolve(strict=True))]
    if not args.apphost:
        command.insert(0, args.dotnet)
    command.append("--installation-profile=minimal")
    with tempfile.TemporaryDirectory(prefix="relaxkonos-desktop-") as temporary:
        home = Path(temporary)
        env = {**os.environ, "HOME": temporary}
        for key, name in {"CONFIG": "config", "DATA": "data", "CACHE": "cache", "STATE": "state"}.items():
            env[f"XDG_{key}_HOME"] = str(home / name)
        with (home / "desktop.log").open("w+", encoding="utf-8") as log:
            process = subprocess.Popen(command,
                                       cwd=publish, env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                window = find_window(process)
                if not (home / "data" / "RelaxKonOS" / "SystemDrive" / "System").is_dir():
                    raise RuntimeError("Desktop did not initialize the system drive under the isolated data directory.")
                request_close(window)
                if process.wait(timeout=12) != 0:
                    raise RuntimeError(f"Desktop closed with status {process.returncode}.")
            except (RuntimeError, subprocess.SubprocessError) as error:
                log.seek(0)
                raise SystemExit(f"{error}\n{log.read()}") from error
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
    print("Linux Release opened a visible X11 window in Minimal mode and exited with status 0.")


if __name__ == "__main__":
    main()
