#!/usr/bin/env python3
"""Offline re-check of the fixtures against the server-side template predicates.

This script does not talk to a server. It mirrors the decision order of:

  * ``ApplicationArchiveSafety.ExtractAsync``      (extraction safety and bounds)
  * ``ApplicationTemplateCatalog.PublishRoot``     (single top-level directory unwrapping)
  * ``JavaJarTemplate`` / ``DotNetPublishTemplate`` / ``PythonProjectTemplate``
    ``PrepareBuildContextAsync``, in the same order the server checks things

The point is to prove *before* touching Docker that every positive fixture passes preflight and that
every negative fixture fails on the predicate it was built for. A fixture that fails for an
unintended reason is worse than no fixture, because it would be read as evidence of something else.

Exit code 0 means every case matched its expectation; 1 means at least one did not.
"""

from __future__ import annotations

import io
import json
import sys
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

# --------------------------------------------------------------------------- server-side constants

MAXIMUM_ARCHIVE_ENTRIES = 20000
MAXIMUM_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
MAXIMUM_PATH_DEPTH = 32
SUPPORTED_PLATFORMS = ("linux/amd64", "linux/arm64", "linux/arm")

INVALID_REQUEST = "application-deployment.invalid_request"
ARCHIVE_UNAVAILABLE = "application-deployment.archive_unavailable"
ARCHIVE_UNSAFE_ENTRY = "application-deployment.archive_unsafe_entry"
ARCHIVE_TOO_MANY_ENTRIES = "application-deployment.archive_too_many_entries"
ARCHIVE_EXPANDED_TOO_LARGE = "application-deployment.archive_expanded_too_large"
ARCHIVE_CONTENT_INVALID = "application-deployment.archive_content_invalid"
IMAGE_REFERENCE_INVALID = "application-deployment.image_reference_invalid"
IMAGE_PLATFORM_MISMATCH = "application-deployment.image_platform_mismatch"
ENTRY_POINT_INVALID = "application-deployment.entry_point_invalid"
RUNTIME_MISMATCH = "application-deployment.runtime_mismatch"


class Rejected(Exception):
    """One deterministic refusal, carrying the stable problem code the server would report."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


# ------------------------------------------------------------------------------- extraction mirror


def normalize_entry(name: str) -> str | None:
    """Mirror of ``ApplicationArchiveSafety.Normalize``: canonical relative path, or None if unsafe."""
    if not name or any(ord(character) < 32 for character in name):
        return None
    normalized = name.replace("\\", "/").rstrip("/")
    if not normalized:
        return ""
    if normalized.startswith("/") or (len(normalized) >= 2 and normalized[1] == ":"):
        return None
    segments = [segment for segment in normalized.split("/") if segment]
    if len(segments) > MAXIMUM_PATH_DEPTH:
        return None
    if any(segment in (".", "..") for segment in segments):
        return None
    return "/".join(segments)


def extract_archive(blob: bytes) -> dict[str, bytes]:
    """Mirror of ``ApplicationArchiveSafety.ExtractAsync``, returning entry name -> content."""
    try:
        archive = zipfile.ZipFile(io.BytesIO(blob))
    except zipfile.BadZipFile:
        raise Rejected(ARCHIVE_UNAVAILABLE) from None

    with archive:
        entries = archive.infolist()
        if len(entries) > MAXIMUM_ARCHIVE_ENTRIES:
            raise Rejected(ARCHIVE_TOO_MANY_ENTRIES)

        declared = 0
        files: dict[str, bytes] = {}
        for entry in entries:
            relative = normalize_entry(entry.filename)
            if relative is None:
                raise Rejected(ARCHIVE_UNSAFE_ENTRY)
            if relative == "":
                continue
            # S_IFLNK in the stored Unix mode: the server materializes contents itself.
            if (entry.external_attr >> 16) & 0xF000 == 0xA000:
                raise Rejected(ARCHIVE_UNSAFE_ENTRY)
            if entry.is_dir():
                continue
            declared += entry.file_size
            if declared > MAXIMUM_EXPANDED_BYTES:
                raise Rejected(ARCHIVE_EXPANDED_TOO_LARGE)
            files[relative] = archive.read(entry)

    return files


def publish_root(files: dict[str, bytes]) -> str:
    """Mirror of ``ApplicationTemplateCatalog.PublishRoot``: a lone top-level directory is the root."""
    top_level_files = [name for name in files if "/" not in name]
    top_level_dirs = {name.split("/", 1)[0] for name in files if "/" in name}
    if len(top_level_dirs) == 1 and not top_level_files:
        return next(iter(top_level_dirs))
    return ""


def under(root: str, files: dict[str, bytes]) -> dict[str, bytes]:
    prefix = f"{root}/" if root else ""
    return {name[len(prefix):]: content for name, content in files.items() if name.startswith(prefix)}


# ------------------------------------------------------------------------------- template mirrors


def base_image_version(target_framework: str | None) -> str | None:
    """Mirror of ``DotNetPublishTemplate.BaseImageVersion``."""
    if target_framework is None or not target_framework.startswith("net"):
        return None
    segments = target_framework[len("net"):].split(".")
    if not segments or not segments[0].isdigit() or int(segments[0]) < 5:
        return None
    return f"{segments[0]}.{segments[1]}" if len(segments) >= 2 else f"{segments[0]}.0"


def is_valid_runtime_line(value: str) -> bool:
    return 1 <= len(value) <= 16 and all(part.isdigit() and part for part in value.split("."))


def is_valid_module(value: str) -> bool:
    if not 1 <= len(value) <= 128:
        return False
    for segment in value.split("."):
        if not segment:
            return False
        if not (segment[0].isascii() and (segment[0].isalpha() or segment[0] == "_")):
            return False
        if not all(character.isascii() and (character.isalnum() or character == "_") for character in segment):
            return False
    return True


def is_valid_image_reference(value: str) -> bool:
    permitted = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/:-._")
    return 1 <= len(value) <= 255 and all(character in permitted for character in value)


def is_pinned_image_reference(value: str) -> bool:
    """Mirror of ``IsPinnedImageReference``: only an explicit ``:latest`` is refused."""
    return not value.endswith(":latest") and ":latest@" not in value


def has_main_class(jar: bytes) -> bool:
    try:
        with zipfile.ZipFile(io.BytesIO(jar)) as archive:
            manifest = archive.read("META-INF/MANIFEST.MF")
    except (zipfile.BadZipFile, KeyError):
        return False
    for line in manifest.decode("utf-8", errors="replace").splitlines():
        if line.lower().startswith("main-class:") and line[len("main-class:"):].strip():
            return True
    return False


def has_locked_requirements(content: bytes) -> bool:
    for line in content.decode("utf-8", errors="replace").splitlines():
        value = line.strip()
        if not value or value.startswith("#"):
            continue
        if "==" in value:
            continue
        if " @ " in value and "#sha256=" in value.lower():
            continue
        return False
    return True


@dataclass
class DeploymentInput:
    """The parts of a deployment request the template predicates actually depend on."""

    source_kind: str
    workload: str = "Web"
    program_entry: str | None = None
    runtime_version: str | None = None
    base_image: str | None = None
    self_contained: bool = False
    image_reference: str | None = None
    archive_reference_id: str | None = None
    archive: bytes | None = None
    extra: dict[str, str] = field(default_factory=dict)


def validate(source: DeploymentInput) -> None:
    """Mirror of ``ApplicationTemplateBase.Validate`` plus each template's own override."""
    requires_image = source.source_kind == "Image"
    requires_archive = source.source_kind in ("JavaJar", "DotNetPublish", "PythonProject")
    supports_self_contained = source.source_kind == "DotNetPublish"

    if requires_image and not source.image_reference:
        raise Rejected(IMAGE_REFERENCE_INVALID)
    if requires_archive and not source.archive_reference_id:
        raise Rejected(ARCHIVE_UNAVAILABLE)
    if not requires_image and source.image_reference:
        raise Rejected(INVALID_REQUEST)
    if not requires_archive and source.archive_reference_id:
        raise Rejected(INVALID_REQUEST)
    if not supports_self_contained and source.self_contained:
        raise Rejected(INVALID_REQUEST)

    if source.image_reference:
        reference = source.image_reference.strip()
        if not (is_valid_image_reference(reference) and is_pinned_image_reference(reference)):
            raise Rejected(IMAGE_REFERENCE_INVALID)

    if source.base_image:
        base = source.base_image.strip()
        if not (is_valid_image_reference(base) and is_pinned_image_reference(base)):
            raise Rejected(IMAGE_REFERENCE_INVALID)

    if source.source_kind == "Image" and (source.base_image or source.runtime_version or source.program_entry):
        # ImageTemplate refuses unrelated runtime/base-image fields rather than ignoring them.
        raise Rejected(INVALID_REQUEST)

    if source.source_kind == "JavaJar":
        if source.program_entry:
            raise Rejected(ENTRY_POINT_INVALID)
        if source.runtime_version and not is_valid_runtime_line(source.runtime_version):
            raise Rejected(RUNTIME_MISMATCH)

    if source.source_kind == "PythonProject":
        if not source.program_entry or not is_valid_module(source.program_entry):
            raise Rejected(ENTRY_POINT_INVALID)
        if source.runtime_version and not is_valid_runtime_line(source.runtime_version):
            raise Rejected(RUNTIME_MISMATCH)


def prepare_java(source: DeploymentInput) -> None:
    files = extract_archive(source.archive or b"")
    root = under(publish_root(files), files)
    jars = [name for name in root if name.lower().endswith(".jar")]
    if len(jars) != 1:
        raise Rejected(ARCHIVE_CONTENT_INVALID)
    if not has_main_class(root[jars[0]]):
        raise Rejected(ENTRY_POINT_INVALID)


def prepare_dotnet(source: DeploymentInput) -> None:
    files = extract_archive(source.archive or b"")
    root = under(publish_root(files), files)

    runtime_configs = [name for name in root if "/" not in name and name.endswith(".runtimeconfig.json")]
    if len(runtime_configs) != 1:
        raise Rejected(ARCHIVE_CONTENT_INVALID)
    assembly = runtime_configs[0][: -len(".runtimeconfig.json")]
    if not assembly:
        raise Rejected(ARCHIVE_CONTENT_INVALID)

    runtime_config = json.loads(root[runtime_configs[0]])
    runtime_options = runtime_config.get("runtimeOptions")
    if not isinstance(runtime_options, dict):
        raise Rejected(ARCHIVE_CONTENT_INVALID)

    image_version = base_image_version(runtime_options.get("tfm"))
    if image_version is None:
        raise Rejected(RUNTIME_MISMATCH)
    if source.runtime_version and source.runtime_version != image_version:
        raise Rejected(RUNTIME_MISMATCH)

    framework_dependent = not isinstance(runtime_options.get("includedFrameworks"), list)
    if source.self_contained == framework_dependent:
        raise Rejected(RUNTIME_MISMATCH)

    frameworks = runtime_options.get("frameworks")
    is_web = isinstance(frameworks, list) and any(
        isinstance(entry, dict) and entry.get("name") == "Microsoft.AspNetCore.App" for entry in frameworks
    )
    if is_web != (source.workload == "Web"):
        raise Rejected(RUNTIME_MISMATCH)

    dependency_files = [name for name in root if "/" not in name and name.endswith(".deps.json")]
    runtime_target = None
    if len(dependency_files) == 1:
        name = json.loads(root[dependency_files[0]]).get("runtimeTarget", {}).get("name")
        if isinstance(name, str) and "/" in name:
            runtime_target = name.split("/", 1)[1]
    if runtime_target is not None and not runtime_target.lower().startswith("linux-"):
        raise Rejected(IMAGE_PLATFORM_MISMATCH)

    entry = assembly if source.self_contained else f"{assembly}.dll"
    if entry not in root:
        raise Rejected(ARCHIVE_CONTENT_INVALID)


def prepare_python(source: DeploymentInput) -> None:
    files = extract_archive(source.archive or b"")
    root = under(publish_root(files), files)

    requirements = root.get("requirements.txt")
    if requirements is None:
        raise Rejected(ARCHIVE_CONTENT_INVALID)
    if not has_locked_requirements(requirements):
        raise Rejected(ARCHIVE_CONTENT_INVALID)

    first_segment = (source.program_entry or "").split(".")[0]
    if f"{first_segment}.py" not in root and not any(name.startswith(f"{first_segment}/") for name in root):
        raise Rejected(ENTRY_POINT_INVALID)


PREPARE = {
    "JavaJar": prepare_java,
    "DotNetPublish": prepare_dotnet,
    "PythonProject": prepare_python,
}


def run(source: DeploymentInput) -> str | None:
    """Returns the problem code, or None when the deployment input passes preflight."""
    try:
        validate(source)
        if source.source_kind in PREPARE:
            PREPARE[source.source_kind](source)
        return None
    except Rejected as rejected:
        return rejected.code


# --------------------------------------------------------------------------------------- test cases

FIXTURES = Path(__file__).resolve().parent.parent / "fixtures"


def read(name: str) -> bytes:
    path = FIXTURES / name
    if not path.is_file():
        raise SystemExit(f"missing fixture: {path}\nrun scripts/build-fixtures.sh first")
    return path.read_bytes()


@dataclass
class Case:
    label: str
    source: DeploymentInput
    expected: str | None


def cases() -> list[Case]:
    java = read("java-http/dist/relaxkonos-ad-java-http.zip")
    dotnet_web = read("dotnet-web/dist/relaxkonos-ad-dotnet-web.zip")
    dotnet_worker = read("dotnet-worker/dist/relaxkonos-ad-dotnet-worker.zip")
    python_web = read("python-web/dist/relaxkonos-ad-python-web.zip")
    python_worker = read("python-worker/dist/relaxkonos-ad-python-worker.zip")

    def archive(kind: str, blob: bytes, **kwargs: object) -> DeploymentInput:
        return DeploymentInput(source_kind=kind, archive_reference_id="ref", archive=blob, **kwargs)  # type: ignore[arg-type]

    return [
        # ---------------------------------------------------------------- positive: must pass
        Case("java-http", archive("JavaJar", java), None),
        Case("dotnet-web", archive("DotNetPublish", dotnet_web, workload="Web"), None),
        Case("dotnet-worker", archive("DotNetPublish", dotnet_worker, workload="Worker"), None),
        Case("python-web", archive("PythonProject", python_web, program_entry="app"), None),
        Case("python-worker", archive("PythonProject", python_worker, program_entry="worker"), None),
        Case(
            "image-nginx",
            DeploymentInput(source_kind="Image", image_reference="nginx:1.29.0-alpine"),
            None,
        ),
        # ------------------------------------------------- negative: archives and entry points
        Case("java-two-jars", archive("JavaJar", read("negative/java-two-jars.zip")), ARCHIVE_CONTENT_INVALID),
        Case("java-no-main-class", archive("JavaJar", read("negative/java-no-main-class.zip")), ENTRY_POINT_INVALID),
        Case("java-with-program-entry", archive("JavaJar", java, program_entry="App"), ENTRY_POINT_INVALID),
        Case(
            "python-unpinned",
            archive("PythonProject", read("negative/python-unpinned.zip"), program_entry="app"),
            ARCHIVE_CONTENT_INVALID,
        ),
        Case("python-missing-module", archive("PythonProject", python_web, program_entry="missing"), ENTRY_POINT_INVALID),
        Case(
            "dotnet-windows-rid",
            archive("DotNetPublish", read("negative/dotnet-windows-rid.zip"), workload="Web"),
            IMAGE_PLATFORM_MISMATCH,
        ),
        # ------------------------------------------------- negative: workload must match the publish
        Case("dotnet-web-as-worker", archive("DotNetPublish", dotnet_web, workload="Worker"), RUNTIME_MISMATCH),
        Case("dotnet-worker-as-web", archive("DotNetPublish", dotnet_worker, workload="Web"), RUNTIME_MISMATCH),
        # ------------------------------------------------- negative: extraction safety
        Case("archive-traversal", archive("PythonProject", read("negative/archive-traversal.zip"), program_entry="app"), ARCHIVE_UNSAFE_ENTRY),
        Case("archive-absolute-entry", archive("PythonProject", read("negative/archive-absolute-entry.zip"), program_entry="app"), ARCHIVE_UNSAFE_ENTRY),
        Case("archive-symlink", archive("PythonProject", read("negative/archive-symlink.zip"), program_entry="app"), ARCHIVE_UNSAFE_ENTRY),
        # ------------------------------------------------- negative: image reference handling
        Case("image-latest-tag", DeploymentInput(source_kind="Image", image_reference="nginx:latest"), IMAGE_REFERENCE_INVALID),
        Case(
            "image-with-base-image",
            DeploymentInput(source_kind="Image", image_reference="nginx:1.29.0-alpine", base_image="alpine:3.21"),
            INVALID_REQUEST,
        ),
        Case(
            "image-with-program-entry",
            DeploymentInput(source_kind="Image", image_reference="nginx:1.29.0-alpine", program_entry="/bin/sh"),
            INVALID_REQUEST,
        ),
        Case(
            "archive-with-image-reference",
            archive("JavaJar", java, image_reference="nginx:1.29.0-alpine"),
            INVALID_REQUEST,
        ),
    ]


def main() -> int:
    failures = 0
    width = max(len(case.label) for case in cases())

    print(f"{'case'.ljust(width)}  {'expected':<48} {'actual':<48} result")
    print("-" * (width + 104))
    for case in cases():
        actual = run(case.source)
        passed = actual == case.expected
        failures += 0 if passed else 1
        expected = case.expected or "(passes preflight)"
        print(f"{case.label.ljust(width)}  {expected:<48} {actual or '(passes preflight)':<48} {'ok' if passed else 'MISMATCH'}")

    print()
    if failures:
        print(f"{failures} of {len(cases())} case(s) did not behave as declared")
        return 1
    print(f"all {len(cases())} case(s) behaved as declared")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
