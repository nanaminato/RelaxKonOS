#!/usr/bin/env python3
"""Create a test-only signed source outside the host being tested (requires cryptography).

Never uploads a signing key or changes host trust. Inspect and explicitly confirm the generated
policy using the installed Helper's local administrator CLI. Serve catalog.json and the two
unchanged archives over certificate-validated HTTPS at --base-uri.
"""
import argparse
import base64
import datetime
import gzip
import hashlib
import ipaddress
import json
import os
import re
import urllib.parse
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils


FIXTURES = {
    "1.19.10": "a40d28be99cd46649ff0fa9f293a9b593fd98c0507b2623927f0ff2bd587d294",
    "1.19.30": "cf06ce2c7d1421bdbda14ee4a5b6046672dc35ebf8eecd8e77504ec3c0ed9a84",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-uri", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--older-archive", required=True)
    parser.add_argument("--newer-archive", required=True)
    parser.add_argument("--output", required=True, help="new local directory; never the remote mirror directory")
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
    policy = dict(sourceId=args.source, runtimeId="mihomo", publisher="RelaxKonOS isolated HTTP acceptance (reviewed Mihomo fixtures)",
                  kind="HttpsManifest", baseUri=args.base_uri, manifestPath="catalog.json", keyId=args.source + "-key",
                  publicKeySpki=base64.b64encode(key.public_key().public_bytes(serialization.Encoding.DER,
                      serialization.PublicFormat.SubjectPublicKeyInfo)).decode(), enabled=True, trustRevision=0,
                  channels=["Stable"], maximumMetadataBytes=1048576, maximumValiditySeconds=86400,
                  downloadPrefixes=[args.base_uri], redirectPrefixes=[],
                  privateNetworks=[dict(cidr=str(address) + ("/32" if address.version == 4 else "/128"), port=uri.port or 443)])
    releases = []
    for version, path in (("1.19.10", args.older_archive), ("1.19.30", args.newer_archive)):
        archive = Path(path).read_bytes()
        if hashlib.sha256(archive).hexdigest() != FIXTURES[version]:
            parser.error("archive does not match reviewed upstream digest: " + version)
        binary = gzip.decompress(archive)
        filename = f"mihomo-linux-amd64-v{version}.gz"
        releases.append(dict(releaseId=args.source + "-v" + version, runtimeId="mihomo", originalVersion="v" + version,
            channel="Stable", publishedAt=None, downloadUri=args.base_uri + filename,
            target=dict(os="linux", architecture="amd64", distribution=None, libc=None, installMode="Archive", serviceHost="systemd"),
            artifact=dict(artifactId="mihomo-" + version, sha256=FIXTURES[version], maximumBytes=len(archive), format="gz",
                entryPoint="mihomo", verificationIdentity=policy["keyId"],
                layout=dict(maximumExpandedBytes=len(binary), maximumEntries=1,
                    files=[dict(path="mihomo", length=len(binary), sha256=hashlib.sha256(binary).hexdigest(), executable=True)]))))
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
