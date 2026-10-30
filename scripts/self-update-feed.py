"""Sign a public portable-update feed after exact HTTPS artifact readback.

The calling PowerShell script supplies the DPAPI-protected private seed via stdin.
Only the public envelope and non-secret hash receipt are written.
"""
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import re
import sys

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
from cryptography.hazmat.primitives import serialization

PUBLIC_KEY = "sGPYRQljyVrnHjI94UdtPra31C/sdsHoMxyWA/Jf3zQ="
KEY_ID = "mathgpt-update-20261029"


def verify(path):
    data = Path(path).read_bytes()
    if len(data) > 131072:
        raise ValueError("Oversized feed")
    wrapper = json.loads(data)
    if set(wrapper) != {"schema", "key_id", "payload", "signature"} or wrapper["schema"] != 1 or wrapper["key_id"] != KEY_ID:
        raise ValueError("Invalid feed envelope")
    raw = base64.b64decode(wrapper["payload"], validate=True)
    Ed25519PublicKey.from_public_bytes(base64.b64decode(PUBLIC_KEY)).verify(base64.b64decode(wrapper["signature"], validate=True), raw)
    payload = json.loads(raw)
    now = datetime.now(timezone.utc)
    if payload["schema"] != 1 or payload["app"] != "mathgpt-desktop" or len(payload["releases"]) != 4:
        raise ValueError("Invalid feed payload")
    generated = datetime.fromisoformat(payload["generated_at_utc"].replace("Z", "+00:00"))
    expires = datetime.fromisoformat(payload["expires_at_utc"].replace("Z", "+00:00"))
    if not generated <= now + timedelta(minutes=10) < expires or expires - generated > timedelta(days=91):
        raise ValueError("Invalid feed time")
    versions = {row["version"] for row in payload["releases"]}
    if len(versions) != 1 or not re.fullmatch(r"[1-9][0-9]*\.[0-9]+\.[0-9]+", next(iter(versions))):
        raise ValueError("Mixed or invalid versions")
    if {(row["platform"], row["architecture"]) for row in payload["releases"]} != {(p, a) for p in ("windows", "mac") for a in ("x64", "arm64")}:
        raise ValueError("Invalid platform set")
    print(json.dumps({"verified": True, "version": next(iter(versions)), "sha256": hashlib.sha256(data).hexdigest()}))


def build(parameters):
    manifest = json.loads(Path(parameters["manifest"]).read_text(encoding="utf-8-sig"))
    receipt = json.loads(Path(parameters["receipt"]).read_text(encoding="utf-8-sig"))
    version = manifest["version"]
    if not re.fullmatch(r"[1-9][0-9]*\.[0-9]+\.[0-9]+", version):
        raise ValueError("Invalid public release version")
    if receipt.get("published") is not True or receipt.get("version") != version:
        raise ValueError("Publish and read back this exact desktop version before signing")
    public_files = {row["name"]: row for row in receipt["files"]}
    releases = []
    for platform in ("windows", "mac"):
        for arch in ("x64", "arm64"):
            name = f"MathGPT-{version}-Portable-win-{arch}.zip" if platform == "windows" else f"MathGPT-{version}-mac-{arch}-preview.zip"
            rows = [row for row in manifest["files"] if row["name"] == name]
            if len(rows) != 1:
                raise ValueError("All four exact platform packages are required")
            row = rows[0]
            actual = public_files.get(name, {})
            if (actual.get("full_https_readback") is not True or actual.get("sha256") != row["sha256"] or
                    actual.get("bytes") != row["bytes"] or not re.fullmatch(r"[0-9a-f]{64}", row["sha256"]) or
                    type(row["bytes"]) is not int or not 0 < row["bytes"] < 512 * 1024 * 1024):
                raise ValueError("Artifact lacks a matching exact full HTTPS hash receipt")
            releases.append({"version": version, "platform": platform, "architecture": arch,
                             "file_name": name, "bytes": row["bytes"], "sha256": row["sha256"]})
    now = datetime.now(timezone.utc).replace(microsecond=0)
    stamp = lambda value: value.isoformat().replace("+00:00", "Z")
    payload = {"schema": 1, "app": "mathgpt-desktop", "generated_at_utc": stamp(now),
               "expires_at_utc": stamp(now + timedelta(days=90)), "releases": releases}
    raw = json.dumps(payload, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    seed = base64.b64decode(parameters["private_seed"], validate=True)
    private = Ed25519PrivateKey.from_private_bytes(seed)
    public = private.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
    if base64.b64encode(public).decode("ascii") != PUBLIC_KEY:
        raise ValueError("Signing identity differs from the embedded update public key")
    signature = private.sign(raw)
    private.public_key().verify(signature, raw)
    envelope = {"schema": 1, "key_id": KEY_ID, "payload": base64.b64encode(raw).decode("ascii"),
                "signature": base64.b64encode(signature).decode("ascii")}
    output = Path(parameters["output"])
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(envelope, stream, separators=(",", ":"))
        stream.write("\n")
    print(json.dumps({"signed": True, "version": version, "packages": len(releases), "key_id": KEY_ID,
                      "feed_sha256": hashlib.sha256(output.read_bytes()).hexdigest(), "published": False}))


if __name__ == "__main__":
    try:
        parameters = json.load(sys.stdin)
        if "verify_path" in parameters:
            verify(parameters["verify_path"])
        else:
            build(parameters)
    except Exception as error:
        # Suppress input, private data and arbitrary exception text.
        print("Update-feed generation refused: " + type(error).__name__, file=sys.stderr)
        sys.exit(1)
