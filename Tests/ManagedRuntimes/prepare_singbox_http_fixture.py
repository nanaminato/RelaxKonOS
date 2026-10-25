#!/usr/bin/env python3
"""Prepare a test-only signed sing-box source outside the host under test.

Requires cryptography. Only the two reviewed Linux amd64 archives are accepted.
No files are extracted, no source is registered, and the private signing key must
remain on the preparation machine. Provision policy.json with Helper inspect/confirm.
"""
import argparse
import base64
import datetime
import hashlib
import ipaddress
import json
import os
import re
import tarfile
import urllib.parse
from pathlib import Path, PurePosixPath

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils


# Same reviewed upstream identities as SingBoxRuntimeAcceptance.cs.
FIXTURES = {
    "1.13.21": "24f9ef8e7234e13e71e74c3598a4164c5fe07b7b67ccc6e96cf68b54789f72cd",
    "1.14.3": "e2bdf179c15a3652955dc44867e33ca0965c9c9b91b34267dcc5a5d639a5feee",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-uri", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--older-archive", required=True)
    parser.add_argument("--newer-archive", required=True)
    parser.add_argument("--output", required=True, help="new private local directory")
    args = parser.parse_args()
    uri = urllib.parse.urlsplit(args.base_uri)
    if (uri.scheme != "https" or not uri.hostname or uri.username or uri.password or uri.query or uri.fragment
            or not uri.path.endswith("/") or not re.fullmatch(r"[A-Za-z0-9_.-]{1,110}", args.source)):
        parser.error("an exact HTTPS base directory and a valid source ID are required")
    address = ipaddress.ip_address(uri.hostname)
    networks = [ipaddress.ip_network(cidr) for cidr in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7")]
    if not any(address.version == network.version and address in network for network in networks):
        parser.error("the acceptance mirror must use one exact RFC1918 or ULA host address")
    key = ec.generate_private_key(ec.SECP256R1())
    policy = dict(sourceId=args.source, runtimeId="sing-box",
        publisher="RelaxKonOS isolated desktop acceptance (reviewed sing-box fixtures)", kind="HttpsManifest",
        baseUri=args.base_uri, manifestPath="catalog.json", keyId=args.source + "-key",
        publicKeySpki=base64.b64encode(key.public_key().public_bytes(serialization.Encoding.DER,
            serialization.PublicFormat.SubjectPublicKeyInfo)).decode(), enabled=True, trustRevision=0,
        channels=["Stable"], maximumMetadataBytes=1048576, maximumValiditySeconds=86400,
        downloadPrefixes=[args.base_uri], redirectPrefixes=[],
        privateNetworks=[dict(cidr=str(address) + ("/32" if address.version == 4 else "/128"), port=uri.port or 443)])
    releases = []
    for version, filename in (("1.13.21", args.older_archive), ("1.14.3", args.newer_archive)):
        path = Path(filename)
        with path.open("rb") as archive:
            if hashlib.file_digest(archive, "sha256").hexdigest() != FIXTURES[version]:
                parser.error("archive does not match reviewed upstream digest: " + version)
        files = []
        names = set()
        with tarfile.open(path, "r:gz") as archive:
            for member in archive:
                name = member.name
                if member.isdir():
                    continue
                if (not member.isfile() or PurePosixPath(name).is_absolute() or "\\" in name
                        or any(part in ("", ".", "..") for part in name.split("/")) or name in names):
                    parser.error("archive has an unsupported or ambiguous entry")
                names.add(name)
                with archive.extractfile(member) as file:
                    digest = hashlib.file_digest(file, "sha256").hexdigest()
                files.append(dict(path=name, length=member.size, sha256=digest,
                    executable=PurePosixPath(name).name == "sing-box"))
        entries = [file["path"] for file in files if file["executable"]]
        if len(entries) != 1:
            parser.error("archive must contain exactly one sing-box executable")
        releases.append(dict(releaseId=args.source + "-v" + version, runtimeId="sing-box", originalVersion="v" + version,
            channel="Stable", publishedAt=None, downloadUri=args.base_uri + f"sing-box-{version}-linux-amd64.tar.gz",
            target=dict(os="linux", architecture="amd64", distribution=None, libc=None, installMode="Archive", serviceHost="systemd"),
            artifact=dict(artifactId="sing-box-" + version + "-linux-amd64", sha256=FIXTURES[version],
                maximumBytes=path.stat().st_size, format="tar.gz", entryPoint=entries[0], verificationIdentity=policy["keyId"],
                layout=dict(maximumExpandedBytes=sum(file["length"] for file in files), maximumEntries=len(files) + 16, files=files))))
    now = datetime.datetime.now(datetime.timezone.utc)
    payload = json.dumps(dict(schemaVersion=1, sourceId=args.source, revision=1, issuedAt=now.isoformat(),
        expiresAt=(now + datetime.timedelta(hours=6)).isoformat(), releases=releases, revokedReleaseIds=[]), separators=(",", ":")).encode()
    r, s = utils.decode_dss_signature(key.sign(b"RelaxKonOS.RuntimeCatalog.v1\n" + payload, ec.ECDSA(hashes.SHA256())))
    envelope = dict(schemaVersion=1, keyId=policy["keyId"], payload=base64.b64encode(payload).decode(),
        signature=base64.b64encode(r.to_bytes(32, "big") + s.to_bytes(32, "big")).decode())
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=False)
    with os.fdopen(os.open(output / "publisher-private.pem", os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as file:
        file.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    (output / "policy.json").write_text(json.dumps(policy, indent=2) + "\n")
    (output / "catalog.json").write_text(json.dumps(envelope, indent=2) + "\n")
    print("Prepared test-only source:", args.source, args.base_uri)
    print("Keep publisher-private.pem outside the test host. Provision policy.json through inspect/confirm.")


if __name__ == "__main__":
    main()
