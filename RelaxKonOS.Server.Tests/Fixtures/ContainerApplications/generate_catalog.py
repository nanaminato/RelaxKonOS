"""Install an acceptance manifest into the catalog of an isolated Server publish."""

import argparse
import json
from pathlib import Path
import re


POSTGRES = "docker.io/library/postgres@sha256:4717f90960a225c2d1c5dac50508c401230f693ff626e06e84e6db0a68ded508"
FIXTURE_REFERENCE = re.compile(r"127\.0\.0\.1:([1-9][0-9]{0,4})/relaxkonos-ca/(web-db|ntfy|postgres)@sha256:[a-f0-9]{64}")


def fixture_image(parser, image, repository):
    match = FIXTURE_REFERENCE.fullmatch(image)
    if match is None or int(match.group(1)) > 65535 or match.group(2) != repository:
        parser.error(f"expected an immutable reference in 127.0.0.1:<port>/relaxkonos-ca/{repository}")
    return image


def web_db_manifest(web_image, db_image):
    template = Path(__file__).resolve().parent.parent / "ApplicationCatalog" / "fixture-web-db-plan.json"
    manifest = json.loads(template.read_text(encoding="utf-8"))
    release = manifest["release"]
    release["id"] = "fixture-web-db"
    release["purpose"] = "Web and PostgreSQL acceptance fixture"
    release["description"] = "Isolated acceptance fixture with a database-backed readiness and marker API."
    images = {"web": web_image, "db": db_image}
    for service in release["services"]:
        service["imageReference"] = images[service["id"]]
    return "fixture-web-db.json", manifest


def ntfy_manifest(version, image, predecessor):
    release = {
        "id": "fixture-ntfy",
        "version": version,
        "purpose": "ntfy SQLite persistence acceptance",
        "description": "Upstream ntfy with a fixed local message cache and a loopback HTTP endpoint.",
        "webService": "web",
        "healthPath": "/v1/health",
        "services": [{
            "id": "web",
            "imageReference": image,
            "containerPort": 80,
            "volumes": [{"name": "cache", "containerPath": "/var/cache/ntfy"}],
            "dependsOn": [],
        }],
        "fields": [],
    }
    if predecessor:
        release["upgradeFrom"] = [{"version": "2.37.0", "imageReference": predecessor}]
    return f"fixture-ntfy-{version}.json", {"schemaVersion": "3", "release": release}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-publish", required=True, type=Path, help="isolated Server publish containing Assets/ApplicationCatalog")
    subparsers = parser.add_subparsers(dest="fixture", required=True)
    web_db = subparsers.add_parser("web-db")
    web_db.add_argument("--image", required=True)
    web_db.add_argument("--db-image", help="loopback mirror of the exact pinned PostgreSQL image for offline acceptance")
    ntfy = subparsers.add_parser("ntfy")
    ntfy.add_argument("--version", choices=("2.37.0", "2.37.2"), required=True)
    ntfy.add_argument("--image", required=True)
    ntfy.add_argument("--predecessor-image")
    args = parser.parse_args()

    catalog = args.server_publish / "Assets" / "ApplicationCatalog"
    if not catalog.is_dir():
        parser.error("--server-publish must contain Assets/ApplicationCatalog")
    image = fixture_image(parser, args.image, args.fixture)
    if args.fixture == "web-db":
        db_image = fixture_image(parser, args.db_image, "postgres") if args.db_image else POSTGRES
        if db_image.split("@")[1] != POSTGRES.split("@")[1]:
            parser.error("the PostgreSQL mirror must retain the pinned manifest digest")
        filename, manifest = web_db_manifest(image, db_image)
    else:
        if bool(args.predecessor_image) != (args.version == "2.37.2"):
            parser.error("--predecessor-image is required for the 2.37.2 upgrade")
        predecessor = fixture_image(parser, args.predecessor_image, "ntfy") if args.predecessor_image else None
        if predecessor and (image.split("@")[0] != predecessor.split("@")[0] or image == predecessor):
            parser.error("the compatible releases must have distinct digests in the same repository")
        filename, manifest = ntfy_manifest(args.version, image, predecessor)

    path = catalog / filename
    try:
        with path.open("x", encoding="utf-8", newline="\n") as output:
            json.dump(manifest, output, ensure_ascii=False, indent=2)
            output.write("\n")
    except FileExistsError:
        parser.error(f"catalog entry already exists: {path}")
    print(path)


if __name__ == "__main__":
    main()
