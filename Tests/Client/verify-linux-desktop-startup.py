"""Verify a published desktop provisions its profile and opens and closes an X11 window."""

import argparse
import ctypes as c
import json
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


def find_window(process, titles=("RelaxKonOS",)):
    title_pattern = "|".join(re.escape(title) for title in titles)
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"Desktop exited before opening a window (status {process.returncode}).")
        tree = subprocess.check_output(["xwininfo", "-root", "-tree"], text=True, timeout=5)
        for window in re.findall(rf'^\s+(0x[\da-f]+)\s+"(?:{title_pattern})":', tree, re.MULTILINE):
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
    raise RuntimeError(f"No visible window titled {titles!r} belonging to the test process appeared.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("publish_directory", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--apphost", action="store_true", help="Run the published RelaxKonOS executable directly.")
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument("--profile", choices=["minimal", "standard", "developer"])
    selection.add_argument("--first-run", action="store_true", help="Dismiss the initial app selection and verify Minimal.")
    args = parser.parse_args()
    publish = args.publish_directory.resolve(strict=True)
    entry = publish / ("RelaxKonOS" if args.apphost else "RelaxKonOS.dll")
    command = [str(entry.resolve(strict=True))]
    if not args.apphost:
        command.insert(0, args.dotnet)
    profile_path = publish / "OfficialApps" / "installation-profiles.json"
    profile = args.profile or "minimal"
    expected = set()
    if profile != "minimal" or profile_path.is_file():
        profiles = json.loads(profile_path.read_bytes())
        expected = {entry["appId"] for entry in profiles["profiles"][profile] if entry["installPolicy"] == "offline"}
    if not args.first_run:
        command.append(f"--installation-profile={profile}")
    with tempfile.TemporaryDirectory(prefix="relaxkonos-desktop-") as temporary:
        home = Path(temporary)
        env = {**os.environ, "HOME": temporary}
        for key, name in {"CONFIG": "config", "DATA": "data", "CACHE": "cache", "STATE": "state"}.items():
            env[f"XDG_{key}_HOME"] = str(home / name)
        with (home / "desktop.log").open("w+", encoding="utf-8") as log:
            process = subprocess.Popen(command,
                                       cwd=publish, env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                if args.first_run:
                    dialog = find_window(process, ("Choose desktop apps", "选择桌面应用", "デスクトップアプリの選択"))
                    request_close(dialog)
                window = find_window(process)
                drive = home / "data" / "RelaxKonOS" / "SystemDrive"
                if not (drive / "System").is_dir():
                    raise RuntimeError("Desktop did not initialize the system drive under the isolated data directory.")
                if args.first_run or profile_path.is_file():
                    saved = drive / "System" / "installation-profile.json"
                    if not saved.is_file() or json.loads(saved.read_bytes()).get("profile") != profile:
                        raise RuntimeError(f"Desktop did not save the {profile} selection.")
                installed = {path.parent.name for path in (drive / "Programs" / "External").glob("*/current.json")}
                if installed != expected:
                    raise RuntimeError(f"Profile provisioning mismatch: missing {sorted(expected - installed)}, "
                                       f"unexpected {sorted(installed - expected)}.")
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
    if args.first_run:
        print("Linux Release opened the first-run app selection, reached sign-in with no preinstalled apps, and exited with status 0.")
    else:
        print(f"Linux Release opened a visible X11 window with {len(expected)} {profile} packages and exited with status 0.")


if __name__ == "__main__":
    main()
