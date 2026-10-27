"""Checks that every shipped language pack has the same semantic keys as English,
and that every key the client source asks for actually exists in English.

The second half is the guard that was missing: a typo or a newly introduced
`{loc:Loc ...}` / `LocalizedText.Get(...)` used to surface only at runtime as
`[missing: <key>]`, with no test failing. Keys composed at call sites (prefix
concatenation such as `"install_state." + state`) cannot be resolved statically,
so only their prefix is required to match at least one English key.
"""
import json
import re
import sys
from pathlib import Path


root = Path(__file__).resolve().parents[1] / "Client" / "RelaxKonOS.Client"
language_dir = root / "Localization"
# Resource segments may use protocol enum names (for example `install_state.Idle`).
# Keep the namespace segment lowercase while accepting those established enum suffixes.
valid_key = re.compile(r"[a-z][a-z0-9-]*(?:[._][A-Za-z0-9-]+)*$")
allowed_document_keys = {"Culture", "DisplayName", "SortOrder", "Strings"}
errors: list[str] = []

# Every call site that resolves a resource key. `ThemeBrushes.Get` deliberately does
# not appear here: it looks up theme brushes, not text.
key_call = re.compile(
    r"(?:LocalizedText|LocalizedStatus"
    r"|_localization|_navigationLocalization|localization)"
    r"\.(?:Get|Ref|RefWithFallback|Format|Key)\(\s*\"([^\"]*)\"")
# These helpers take only the last segment and prepend their own namespace, so the
# literal has to be checked as `prefix + literal` instead of as a whole key.
suffix_call = re.compile(
    r"(ComponentObservationText\.Key|TunnelProblemText\.Format)\(\s*\"([^\"]*)\"")
suffix_prefix = {
    "ComponentObservationText.Key": "component_center.domain.",
    "TunnelProblemText.Format": "tunnels.problem.",
}
xaml_key = re.compile(r"\{loc:Loc\s+([A-Za-z0-9_.]+)\s*\}")


def load_language(culture: str) -> dict[str, str]:
    strings: dict[str, str] = {}
    paths = sorted((language_dir / culture).glob("*.json"))
    if not paths:
        errors.append(f"{culture}: no localization files")

    for path in paths:
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as error:
            errors.append(f"{path.relative_to(language_dir)}: invalid JSON ({error.msg})")
            continue

        if document.get("Culture") != culture:
            errors.append(f"{path.relative_to(language_dir)}: Culture must be {culture}")
        unexpected = set(document) - allowed_document_keys
        if unexpected:
            errors.append(f"{path.relative_to(language_dir)}: unexpected top-level keys {', '.join(sorted(unexpected))}")
        values = document.get("Strings")
        if not isinstance(values, dict):
            errors.append(f"{path.relative_to(language_dir)}: Strings must be an object")
            continue

        for key, value in values.items():
            if key in strings:
                errors.append(f"{path.relative_to(language_dir)}: duplicate key {key}")
            elif not isinstance(value, str) or not value.strip():
                errors.append(f"{path.relative_to(language_dir)}: {key} must have non-empty text")
            else:
                strings[key] = value

    return strings


cultures = sorted(path.name for path in language_dir.iterdir() if path.is_dir() and not path.name.startswith("."))
if "en-US" not in cultures:
    errors.append("Missing en-US localization pack")
    english: dict[str, str] = {}
    expected: set[str] = set()
else:
    english = load_language("en-US")
    expected = set(english)

errors.extend(f"non-semantic English key: {key}" for key in expected if not valid_key.fullmatch(key))

# Format arguments must line up: `{0}` at the end of a string is as much a placeholder as `{0}` before
# punctuation, and a translation that drops it silently loses data instead of failing.
format_argument = re.compile(r"(?<!\{)\{(\d+)[,}:]")


def format_arguments(value: str) -> str:
    return ",".join(sorted(format_argument.findall(value)))


for culture in cultures:
    if culture == "en-US":
        continue
    translated = load_language(culture)
    actual = set(translated)
    missing, extra = expected - actual, actual - expected
    if missing:
        errors.append(f"{culture}: missing {', '.join(sorted(missing))}")
    if extra:
        errors.append(f"{culture}: extra {', '.join(sorted(extra))}")
    for key in sorted(expected & actual):
        if format_arguments(english[key]) != format_arguments(translated[key]):
            errors.append(f"{culture}: format arguments differ for {key}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)


def collect_requested_keys() -> tuple[dict[str, str], dict[str, str]]:
    """Returns ({key: first location}, {prefix: first location}) referenced by the client source."""
    requested: dict[str, str] = {}
    prefixes: dict[str, str] = {}
    sources = sorted(
        path
        for pattern in ("*.cs", "*.axaml")
        for path in root.rglob(pattern)
        if not {"bin", "obj"} & set(path.parts)
    )
    for path in sources:
        location_root = path.relative_to(root).as_posix()
        for line_number, line in enumerate(
            path.read_text(encoding="utf-8-sig", errors="replace").splitlines(), 1
        ):
            if line.lstrip().startswith(("//", "///", "*", "<!--")):
                continue
            location = f"{location_root}:{line_number}"
            for match in key_call.finditer(line):
                literal = match.group(1)
                if "{" in literal:
                    literal = literal.split("{", 1)[0]
                if not literal:
                    continue  # fully dynamic argument; nothing to verify
                # `"login.local_manage_" + key` continues the key after the literal.
                if literal.endswith(".") or line[match.end():].lstrip().startswith("+"):
                    prefixes.setdefault(literal, location)
                else:
                    requested.setdefault(literal, location)
            for match in suffix_call.finditer(line):
                requested.setdefault(suffix_prefix[match.group(1)] + match.group(2), location)
            for match in xaml_key.finditer(line):
                requested.setdefault(match.group(1), location)
    return requested, prefixes


requested_keys, prefix_keys = collect_requested_keys()
unresolved = {key: where for key, where in requested_keys.items() if key not in expected}
for key, where in sorted(unresolved.items()):
    errors.append(f"{where}: key {key} is not defined in en-US")
for prefix, where in sorted(prefix_keys.items()):
    if not any(key.startswith(prefix) for key in expected):
        errors.append(f"{where}: composed key prefix {prefix} matches no en-US key")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

print(
    f"Localization verified: {len(expected)} keys in every language pack; "
    f"{len(requested_keys)} referenced by the client, {len(prefix_keys)} composed prefixes, "
    f"{len(expected) - len(requested_keys)} not referenced directly (dynamic keys)."
)
