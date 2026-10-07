"""Compose standalone launchers from the ordered canonical source fragments."""
import argparse
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent
OUTPUTS = {"windows": "RelaxKonOS-Deploy.ps1", "linux": "relaxkonos-deploy.sh"}


def compose(platform: str) -> str:
    parts = (ROOT / "src" / f"{platform}.txt").read_text(encoding="utf-8").splitlines()
    for part in parts:
        if not re.fullmatch(rf"{platform}/[a-z-]+\.inc\.(ps1|sh)", part):
            raise ValueError(f"Invalid launcher fragment: {part}")
    return "".join((ROOT / "src" / part).read_text(encoding="utf-8") for part in parts)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    for platform, name in OUTPUTS.items():
        encoding = "utf-8-sig" if platform == "windows" else "utf-8"
        data = compose(platform).encode(encoding)
        target = args.output / name
        if args.check:
            if not target.is_file() or target.read_text(encoding=encoding).encode(encoding) != data:
                raise SystemExit(f"Generated launcher is stale: {target}. Run {Path(__file__).name}.")
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            if not target.is_file() or target.read_bytes() != data:
                target.write_bytes(data)


if __name__ == "__main__":
    main()
