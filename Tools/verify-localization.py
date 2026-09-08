"""Checks that every shipped language pack has the same semantic keys as English."""
import json
import re
import sys
from pathlib import Path


root = Path(__file__).resolve().parents[1] / "Client" / "RelaxKonOS.Client"
language_dir = root / "Localization"
# Resource segments may use protocol enum names (for example `install_state.Idle`).
# Keep the namespace segment lowercase while accepting those established enum suffixes.
valid_key = re.compile(r"[a-z][a-z0-9-]*(?:[._][A-Za-z0-9-]+)*$")
errors: list[str] = []


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
    expected: set[str] = set()
else:
    expected = set(load_language("en-US"))

errors.extend(f"non-semantic English key: {key}" for key in expected if not valid_key.fullmatch(key))

for culture in cultures:
    if culture == "en-US":
        continue
    actual = set(load_language(culture))
    missing, extra = expected - actual, actual - expected
    if missing:
        errors.append(f"{culture}: missing {', '.join(sorted(missing))}")
    if extra:
        errors.append(f"{culture}: extra {', '.join(sorted(extra))}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

print(f"Localization verified: {len(expected)} keys in every language pack.")
