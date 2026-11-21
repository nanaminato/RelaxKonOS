"""Exercise Direct Deploy against two actual Debug desktop client processes."""

from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from ctypes import WinDLL, wintypes
from pathlib import Path
from tempfile import mkdtemp
import hashlib
import json
import os
import subprocess
import time
from xml.sax.saxutils import escape


REPO = Path(__file__).resolve().parents[2]
CLIENT = REPO / "Client/RelaxKonOS.Client.Desktop/bin/Debug/net10.0/RelaxKonOS.dll"
CLI = REPO / "Tools/RelaxKonOS.DevCli/bin/Debug/net10.0/RelaxKonOS.DevCli.dll"
DESKTOP_HOST = REPO / "Tests/Client/RelaxKonOS.WindowPreviews.Tests/bin/Debug/net10.0/RelaxKonOS.WindowPreviews.Tests.dll"
APP_ID = "com.relaxkonos.tests.direct-deploy"
PILOT_ID = "com.relaxkonos.example.image-viewer"


def wait_for(check, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if value := check():
            return value
        time.sleep(0.4)
    raise AssertionError(f"Timed out waiting for {check.__name__}")


def run(arguments, env):
    return subprocess.run(arguments, cwd=REPO, env=env, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=180)


def dotnet(env, *args):
    result = run(["dotnet", *args], env)
    assert result.returncode == 0, result.stdout + result.stderr


def cli(env, *args, code="ok"):
    proc = run(["dotnet", str(CLI), *map(str, args)], env)
    result = json.loads(proc.stdout)
    assert result["code"] == code, (result, proc.stderr)
    assert result["ok"] == (proc.returncode == 0) == (code == "ok"), result
    return result


@contextmanager
def process(env, log, *args):
    with log.open("wb") as output, log.with_suffix(".err").open("wb") as error:
        proc = subprocess.Popen(["dotnet", *map(str, args)], cwd=REPO, env=env, stdout=output, stderr=error,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            yield proc
        finally:
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=15)


@contextmanager
def lock_dll(path):
    kernel = WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE)
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    handle = kernel.CreateFileW(str(path), 0x80000000, 1, None, 3, 0, None)
    assert handle not in (None, wintypes.HANDLE(-1).value), path
    try:
        try:
            with path.open("r+b"):
                raise AssertionError("Expected the DLL write handle to be denied")
        except PermissionError:
            pass
        yield
    finally:
        kernel.CloseHandle(handle)


def write_source(project, title):
    source = f'''using Avalonia.Controls;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;

public sealed class SmokeApp : IExternalRemoteApplication
{{
    public ApplicationManifest Manifest => new(new AppId("{APP_ID}"), "Smoke", "1.0");
    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken)
    {{
        context.Windows.ShowWindow(Dependency.Marker.Title + " {title}", new TextBlock {{ Text = "Direct Deploy" }});
        {'throw new InvalidOperationException("Fixture activation failed");' if title == 'throws' else 'return Task.CompletedTask;'}
    }}
}}
'''
    (project / "SmokeApp.cs").write_text(source if title != "invalid" else source + "\n!!!", encoding="utf-8")


def fixture(root):
    app = root / "fixture"
    dependency = root / "dependency"
    app.mkdir()
    dependency.mkdir()
    (dependency / "PrivateDependency.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>''', encoding="utf-8")
    (dependency / "Marker.cs").write_text('namespace Dependency; public static class Marker { public static string Title => "Smoke"; }', encoding="utf-8")
    sdk = escape(str(REPO / "Framework/RelaxKonOS.App.SDK/RelaxKonOS.App.SDK.csproj"))
    (app / "Smoke.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{sdk}" />
    <ProjectReference Include="../dependency/PrivateDependency.csproj" />
    <PackageReference Include="Avalonia" />
  </ItemGroup>
</Project>''', encoding="utf-8")
    (app / "manifest.json").write_text(json.dumps(dict(id=APP_ID, displayName="Smoke", version="1.0",
        permissionModelVersion=2, entryAssembly="lib/net10.0/Smoke.dll", entryType="SmokeApp")), encoding="utf-8")
    write_source(app, "v1")
    return app


def fingerprint_release_packages():
    release = Path(os.environ["LOCALAPPDATA"]) / "RelaxKonOS/SystemDrive/Programs/External"
    if not release.is_dir():
        return {}
    return {pointer.parent.name: hashlib.sha256(pointer.read_bytes()).hexdigest()
            for pointer in release.glob("*/current.json")}


def main():
    assert os.name == "nt", "This acceptance check uses the Windows desktop and Windows file sharing."
    assert all(path.is_file() for path in (CLIENT, CLI, DESKTOP_HOST)), "Build Debug desktop client, DevCli and WindowPreviews.Tests first."
    parent = REPO / ".codex-scratch"
    parent.mkdir(exist_ok=True)
    root = Path(mkdtemp(prefix="direct-deploy-acceptance-", dir=parent))
    print(f"Acceptance logs: {root}", flush=True)
    build_temp = root / "tmp"
    build_temp.mkdir()
    env = dict(os.environ, RELAXKONOS_DEV_DATA_HOME=str(root), RELAXKONOS_DEV_ENABLE="1",
               RELAXKONOS_DEVCLI_TMP=str(build_temp), TEMP=str(build_temp), TMP=str(build_temp),
               MSBUILDDISABLENODEREUSE="1", NuGetAudit="false", DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="true")
    for key in ("RELAXKONOS_DEV_TOKEN", "RELAXKONOS_DEV_PORT", "RELAXKONOS_DEV_ENDPOINT"):
        env.pop(key, None)
    env.setdefault("NUGET_PACKAGES", str(Path.home() / ".nuget/packages"))
    app = fixture(root)
    release_before = fingerprint_release_packages()
    env_a = dict(env, RELAXKONOS_DEV_PROFILE="smoke-a")
    env_b = dict(env, RELAXKONOS_DEV_PROFILE="smoke-b")

    def profile(env_for_profile):
        return root / "RelaxKonOS/Debug" / env_for_profile["RELAXKONOS_DEV_PROFILE"]

    def pair(env_for_profile, proc, expected_apps=()):
        def pairing_ready():
            assert proc.poll() is None, "Desktop process exited during startup; see host log."
            store = profile(env_for_profile) / "developer-mode.json"
            if not store.exists():
                return None
            env_for_profile["RELAXKONOS_DEV_TOKEN"] = json.loads(store.read_text(encoding="utf-8-sig"))["PairingToken"]
            reply = run(["dotnet", str(CLI), "status"], env_for_profile)
            return json.loads(reply.stdout) if reply.returncode == 0 else None
        status = wait_for(pairing_ready, seconds=35)
        assert status["processId"] == proc.pid and tuple(app["appId"] for app in status["apps"]) == expected_apps, status
        return status

    def app_status():
        return cli(env_a, "diagnostics", APP_ID)["apps"][0]

    def assert_active(title, version_id):
        state = app_status()
        assert state["registered"] and state["assemblyLoaded"], state
        assert state["windows"] == [f"Smoke {title}"] and state["currentVersionId"] == version_id, state

    login_env = dict(env, RELAXKONOS_DEV_PROFILE="smoke-login")
    with process(login_env, root / "host-login.log", CLIENT) as login:
        pair(login_env, login)
        offline = cli(login_env, "deploy", app, "--no-launch")
        assert offline["installation"] == "passed" and offline["windowLaunch"] == "notRun", offline
        cli(login_env, "reload", APP_ID, code="launch_blocked")
        update = cli(login_env, "deploy", app, "--no-build", "--no-launch")
        assert update["currentVersionId"] != offline["currentVersionId"], update
        assert cli(login_env, "status")["processId"] == login.pid
    print("The standard Debug desktop executable installed a package from the login screen with --no-launch.", flush=True)
    with process(login_env, root / "host-login-restarted.log", CLIENT) as restarted:
        pair(login_env, restarted, (APP_ID,))
        assert cli(login_env, "rollback", APP_ID, "--no-launch")["currentVersionId"] == offline["currentVersionId"]
    print("The standard Debug desktop executable restored the previous version after restart.", flush=True)

    host_a = root / "host-a.log"
    host_b = root / "host-b.log"
    with process(env_a, host_a, DESKTOP_HOST, "--direct-deploy-desktop") as a, \
            process(env_b, host_b, DESKTOP_HOST, "--direct-deploy-desktop") as b:
        status_a, status_b = pair(env_a, a), pair(env_b, b)
        wait_for(lambda: all("Direct Deploy desktop ready." in log.read_text(encoding="utf-8") for log in (host_a, host_b)), seconds=30)
        assert status_a["port"] != status_b["port"]
        cli(dict(env_a, RELAXKONOS_DEV_TOKEN=env_b["RELAXKONOS_DEV_TOKEN"]), "status", code="unauthorized")
        cli(env_a, "--profile", "smoke-b", "--port", status_a["port"], "status", code="profile_mismatch")

        v1 = cli(env_a, "deploy", app)
        assert [v1[key] for key in ("installation", "assemblyLoad", "windowLaunch", "uiTest")] == ["passed", "passed", "passed", "notRun"], v1
        assert_active("v1", v1["currentVersionId"])
        old = profile(env_a) / "SystemDrive/Programs/External" / APP_ID / "versions" / v1["currentVersionId"] / "lib/net10.0/Smoke.dll"
        digest = hashlib.sha256(old.read_bytes()).digest()
        with lock_dll(old):
            write_source(app, "v2")
            v2 = cli(env_a, "deploy", app / "Smoke.csproj")
            assert v2["currentVersionId"] != v1["currentVersionId"]
            assert hashlib.sha256(old.read_bytes()).digest() == digest
            assert_active("v2", v2["currentVersionId"])
        print("Project deploy, live source update and Windows-locked DLL passed.", flush=True)

        write_source(app, "invalid")
        cli(env_a, "deploy", app, code="build_failed")
        assert_active("v2", v2["currentVersionId"])
        write_source(app, "throws")
        failure = cli(env_a, "deploy", app, code="activation_failed")
        assert failure["installation"] == "failed" and failure["windowLaunch"] == "failed"
        assert_active("v2", v2["currentVersionId"])

        write_source(app, "v3")
        compiled = root / "compiled"
        dotnet(env_a, "publish", str(app / "Smoke.csproj"), "-c", "Debug", "-o", str(compiled), "-p:UsedAvaloniaProducts=")
        dependency = compiled / "PrivateDependency.dll"
        saved_dependency = dependency.read_bytes()
        dependency.unlink()
        failed_load = cli(env_a, "deploy", compiled, "--manifest", app / "manifest.json", code="assembly_load_failed")
        assert failed_load["assemblyLoad"] == "failed" and failed_load["currentVersionId"] == v2["currentVersionId"]
        assert_active("v2", v2["currentVersionId"])
        dependency.write_bytes(saved_dependency)
        v3 = cli(env_a, "deploy", compiled, "--manifest", app / "manifest.json", "--no-launch")
        assert v3["windowLaunch"] == "notRun" and app_status()["windows"] == []
        assert cli(env_a, "reload", APP_ID)["installation"] == "unchanged"
        assert_active("v3", v3["currentVersionId"])
        assert cli(env_a, "rollback", APP_ID)["currentVersionId"] == v2["currentVersionId"]
        assert_active("v2", v2["currentVersionId"])
        print("Build, dependency and activation failures preserved the active window. Compiled output, reload and rollback passed.", flush=True)

        direct_log = root / "watch-direct.log"
        with process(env_a, direct_log, CLI, "watch", "--direct", app):
            def watch_results():
                return [json.loads(line) for line in direct_log.read_text(encoding="utf-8").splitlines() if line.endswith("}")]
            def wait_for_count(count):
                return wait_for(lambda: rows if len(rows := watch_results()) >= count else None)
            first = wait_for_count(1)[0]
            assert first["ok"], first
            (app / "artifacts").mkdir(exist_ok=True)
            (app / "artifacts" / "ignored.cs").write_text("!!!", encoding="utf-8")
            # Keep a generated-output event out of both the file watcher and MSBuild's source glob.
            (app / "artifacts" / "ignored.cs").unlink()
            (app / "bin" / "ignored.txt").write_text("ignored", encoding="utf-8")
            (app / "obj" / "ignored.txt").write_text("ignored", encoding="utf-8")
            time.sleep(1)
            assert len(watch_results()) == 1
            write_source(app, "invalid")
            assert wait_for_count(2)[1]["code"] == "build_failed"
            assert_active("v3", first["currentVersionId"])
            write_source(app, "v4")
            watched = wait_for_count(3)[2]
            assert watched["ok"] and watched["uiTest"] == "notRun", watched
            assert_active("v4", watched["currentVersionId"])
        print("watch --direct emitted JSONL, ignored output folders and recovered after a build failure.", flush=True)

        with ThreadPoolExecutor(max_workers=2) as parallel:
            operations = list(parallel.map(lambda _: cli(env_a, "deploy", compiled, "--manifest", app / "manifest.json"), range(2)))
        versions = {result["currentVersionId"] for result in operations}
        last = app_status()["currentVersionId"]
        assert len(versions) == 2 and last in versions
        assert cli(env_a, "rollback", APP_ID)["currentVersionId"] == (versions - {last}).pop()

        reserved = root / "official-id.json"
        reserved.write_text((app / "manifest.json").read_text(encoding="utf-8").replace(APP_ID, "relaxkonos.imageviewer"), encoding="utf-8")
        cli(env_a, "deploy", compiled, "--manifest", reserved, code="invalid_manifest")
        pilot = cli(env_a, "deploy", REPO / "examples/ImageViewerPilot")
        assert pilot["appId"] == PILOT_ID and pilot["windowLaunch"] == "passed" and pilot["uiTest"] == "notRun", pilot
        pilot_package = root / "image-viewer-pilot.roapp"
        dotnet(env_a, str(CLI), "pack", str(REPO / "examples/ImageViewerPilot"), "--output", str(pilot_package), "--install")
        pilot_state = cli(env_a, "diagnostics", PILOT_ID)["apps"][0]
        assert pilot_state["currentVersionId"] != pilot["currentVersionId"] and pilot_state["windows"] == ["Image Viewer Pilot"], pilot_state
        print("Serialized deployments, reserved system ID rejection and Image Viewer pilot activation passed.", flush=True)

        legacy = root / "legacy.roapp"
        dotnet(env_a, str(CLI), "pack", str(app), "--output", str(legacy))
        dotnet(env_a, str(CLI), "install", str(legacy))
        start = app_status()["currentVersionId"]
        with process(env_a, root / "watch-package.log", CLI, "watch", legacy):
            watching = wait_for(lambda: state if (state := app_status())["currentVersionId"] != start else None)
            write_source(app, "v5")
            dotnet(env_a, str(CLI), "pack", str(app), "--output", str(legacy))
            wait_for(lambda: (state := app_status())["currentVersionId"] != watching["currentVersionId"]
                     and state["windows"] == ["Smoke v5"])
        with process(env_a, root / "watch-project.log", CLI, "watch", app):
            watching = wait_for(lambda: (state if (state := app_status())["windows"] == ["Smoke v5"]
                and (app / "artifacts/Smoke.roapp").exists() else None))
            write_source(app, "v6")
            wait_for(lambda: (state := app_status())["currentVersionId"] != watching["currentVersionId"]
                     and state["windows"] == ["Smoke v6"])

        assert cli(env_a, "status")["processId"] == a.pid
        assert cli(env_b, "status")["apps"] == [] and cli(env_b, "status")["processId"] == b.pid
        assert not (profile(env_b) / "SystemDrive/Programs/External" / APP_ID).exists()
        assert fingerprint_release_packages() == release_before
        print(".roapp install, both legacy watch modes, Debug profile isolation and unchanged Release pointers passed.", flush=True)


if __name__ == "__main__":
    main()
