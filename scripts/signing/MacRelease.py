"""Developer ID signing and notarization using Apple's installed tools only.

No password, private key, Apple ID, certificate import or account creation API is
accepted. Credentials must already exist in the macOS user's default Keychain.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import plistlib
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP_ID = "xyz.mathgpt.desktop"
ENTITLEMENTS = Path(__file__).with_name("Mac.entitlements.plist")
REQUIRED_ENTITLEMENTS = {"com.apple.security.cs.allow-jit": True}
TOOLS = {"codesign": "/usr/bin/codesign", "security": "/usr/bin/security",
         "ditto": "/usr/bin/ditto", "xcrun": "/usr/bin/xcrun",
         "spctl": "/usr/sbin/spctl"}
spec = importlib.util.spec_from_file_location("mathgpt_mac_package", ROOT / "scripts/pack-mac.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class ReleaseError(RuntimeError):
    pass


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


class Commands:
    def __init__(self):
        self.records = []

    def run(self, label, tool, arguments, allow_failure=False, keep_output=True):
        env = os.environ.copy()
        env.update({"LC_ALL": "C", "LANG": "en_US.UTF-8"})
        result = subprocess.run([TOOLS[tool], *map(str, arguments)], capture_output=True,
                                text=True, encoding="utf-8", errors="replace", env=env)
        # The only credential reference accepted is a non-secret Keychain profile
        # name. Never add Apple passwords, account credentials or key data here.
        record = {"step": label, "tool": tool, "exit_code": result.returncode}
        if keep_output:
            record.update({"stdout": result.stdout, "stderr": result.stderr})
        self.records.append(record)
        print(f"{label}: {'ok' if result.returncode == 0 else 'rejected'}", flush=True)
        if result.returncode and not allow_failure:
            raise ReleaseError(f"{label} failed (exit {result.returncode}); release stopped")
        return result


def read_config(path=None, environ=None):
    environ = os.environ if environ is None else environ
    config = json.loads(path.read_text(encoding="utf-8")) if path else {}
    allowed = {"schemaVersion", "certificateSha1", "teamId", "notaryProfile"}
    if not isinstance(config, dict) or set(config) - allowed:
        raise ReleaseError("Signing config accepts only schemaVersion, certificateSha1, teamId and notaryProfile; store credentials in Keychain")
    if config.get("schemaVersion", 1) != 1:
        raise ReleaseError("Unsupported Mac signing config schema")
    for field, variable in (("certificateSha1", "MATHGPT_MAC_CERTIFICATE_SHA1"),
                            ("teamId", "MATHGPT_MAC_TEAM_ID"),
                            ("notaryProfile", "MATHGPT_MAC_NOTARY_PROFILE")):
        value = environ.get(variable) or config.get(field)
        if not isinstance(value, str) or not value.strip():
            raise ReleaseError(f"Missing Mac signing setting: {field}")
        config[field] = value.strip()
    config["certificateSha1"] = config["certificateSha1"].upper()
    if not re.fullmatch(r"[0-9A-F]{40}", config["certificateSha1"]):
        raise ReleaseError("certificateSha1 must identify an existing Developer ID Application identity")
    if not re.fullmatch(r"[A-Z0-9]{10}", config["teamId"]):
        raise ReleaseError("teamId must be the existing Apple Developer team identifier")
    if not re.fullmatch(r"[A-Za-z0-9._ -]{1,128}", config["notaryProfile"]) or config["notaryProfile"].startswith("-"):
        raise ReleaseError("notaryProfile must be a local Keychain profile name")
    return config


def require_developer_id(identities, config):
    entries = re.findall(r'^\s*\d+\)\s+([0-9A-Fa-f]{40})\s+"([^"]+)"', identities, re.MULTILINE)
    names = [name for fingerprint, name in entries if fingerprint.upper() == config["certificateSha1"]]
    if len(names) != 1 or not names[0].startswith("Developer ID Application: ") or not names[0].endswith(f'({config["teamId"]})'):
        raise ReleaseError("The selected valid Developer ID Application identity and private key are not available for the configured team")
    return names[0]


def json_response(result, label):
    try:
        value = json.loads(result.stdout)
    except (json.JSONDecodeError, TypeError) as error:
        raise ReleaseError(f"{label} did not return valid JSON") from error
    if not isinstance(value, dict):
        raise ReleaseError(f"{label} did not return a JSON object")
    return value


def preflight(config_path, commands):
    if sys.platform != "darwin":
        raise ReleaseError("Developer ID release requires real macOS; Windows/Linux may build Preview only")
    if sys.version_info < (3, 11):
        raise ReleaseError("Mac release requires Python 3.11 or newer before building")
    config = read_config(config_path)
    for executable in (*TOOLS.values(), "/usr/bin/sips", "/usr/bin/iconutil"):
        if not Path(executable).is_file():
            raise ReleaseError(f"Missing Apple build tool: {Path(executable).name}")
    for tool in ("notarytool", "stapler"):
        commands.run(f"Find {tool}", "xcrun", ["--find", tool], keep_output=False)
    enabled = commands.run("Gatekeeper enabled", "spctl", ["--status"])
    if "assessments enabled" not in (enabled.stdout + enabled.stderr).lower():
        raise ReleaseError("Gatekeeper assessments must be enabled on the release host")
    identities = commands.run("Find Developer ID identity", "security", ["find-identity", "-v", "-p", "codesigning"], keep_output=False)
    config["identityName"] = require_developer_id(identities.stdout, config)
    profile = commands.run("Validate notary Keychain profile", "xcrun",
                           ["notarytool", "history", "--keychain-profile", config["notaryProfile"],
                            "--output-format", "json"], keep_output=False)
    if not isinstance(json_response(profile, "notarytool history").get("history"), list):
        raise ReleaseError("notarytool could not validate the configured Keychain profile")
    if plistlib.loads(ENTITLEMENTS.read_bytes()) != REQUIRED_ENTITLEMENTS:
        raise ReleaseError("Release entitlements must contain only the .NET JIT entitlement")
    return config


def validate_bundle(bundle, runtime):
    if bundle.name != "MathGPT.app" or bundle.is_symlink():
        raise ReleaseError("Expected a fresh MathGPT.app bundle")
    for path in bundle.rglob("*"):
        if path.is_symlink():
            raise ReleaseError("This release layout does not support symbolic links")
        if path.is_dir() and path.suffix in (".app", ".framework", ".xpc", ".bundle"):
            raise ReleaseError("New nested code bundle requires an explicit signing layout")
    info = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
    if info.get("CFBundleIdentifier") != APP_ID or info.get("CFBundleExecutable") != "MathGPT":
        raise ReleaseError("Bundle identity must remain xyz.mathgpt.desktop / MathGPT")
    version = package.version_from_project()
    if info.get("CFBundleVersion") != version or info.get("CFBundleShortVersionString") != version:
        raise ReleaseError("Bundle and Mac project versions must match before signing")
    icon = bundle / "Contents/Resources/MathGPT.icns"
    if icon.read_bytes()[:4] != b"icns":
        raise ReleaseError("Final app icon is missing")
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md", "MathGPT.Bridge-source.zip", "使用说明.txt"):
        if not (bundle / "Contents/Resources" / name).is_file():
            raise ReleaseError("Final bundle resources are incomplete")
    native = package.validate_native_files(bundle, runtime)
    if any(not (path.stat().st_mode & 0o111) for path in native):
        raise ReleaseError("Native code must have executable permissions before signing")
    return version, native


def validate_signature_metadata(text, team, identifier=None):
    if (f"TeamIdentifier={team}" not in text or
            not re.search(r"^Authority=Developer ID Application: .+\(" + re.escape(team) + r"\)$", text, re.MULTILINE) or
            not re.search(r"^Timestamp=.+$", text, re.MULTILINE) or
            not re.search(r"flags=0x[0-9a-fA-F]+\([^)]*\bruntime\b", text) or
            "Signature=adhoc" in text):
        raise ReleaseError("Signature lacks the required Developer ID team, secure timestamp or hardened runtime")
    if identifier and f"Identifier={identifier}\n" not in text + "\n":
        raise ReleaseError("Signature identifier does not match the stable app identifier")


def validate_notary_result(value):
    if value.get("status") != "Accepted" or not re.fullmatch(
            r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", str(value.get("id", ""))):
        raise ReleaseError("Notarization was not Accepted; no formal ZIP will be produced")
    return value["id"]


def verify_notary_log(value, submission_id, archive_hash, team):
    # Apple's documented log uses uppercase job UUIDs and does not promise teamId
    # or sha256 fields. The job must match the accepted submit response; verify
    # optional metadata whenever supplied, with the signing team checked always.
    if str(value.get("jobId", "")).lower() != submission_id.lower() or value.get("status") != "Accepted":
        raise ReleaseError("Notary log does not prove acceptance of this submission")
    if value.get("teamId") is not None and value["teamId"] != team:
        raise ReleaseError("Notary log belongs to a different team")
    if value.get("sha256") is not None and str(value["sha256"]).lower() != archive_hash:
        raise ReleaseError("Notary log belongs to a different archive")


def verify_code(commands, bundle, native, team):
    requirement = ('anchor apple generic and certificate leaf[field.1.2.840.113635.100.6.1.13] exists'
                   f' and certificate leaf[subject.OU] = "{team}"')
    commands.run("Verify bundle recursively", "codesign", ["--verify", "--deep", "--strict", "--verbose=4", "-R", requirement, bundle])
    host = bundle / "Contents/MacOS/MathGPT"
    for path in [*native, bundle]:
        label = path.relative_to(bundle).as_posix() if path != bundle else "MathGPT.app"
        commands.run(f"Verify {label}", "codesign", ["--verify", "--strict", "--verbose=4", "-R", requirement, path])
        display = commands.run(f"Inspect {label}", "codesign", ["--display", "--verbose=4", path])
        validate_signature_metadata(display.stdout + display.stderr, team, APP_ID if path in (host, bundle) else None)
        entitled = commands.run(f"Entitlements {label}", "codesign",
                               ["--display", "--entitlements", "-", "--xml", path])
        actual = plistlib.loads(entitled.stdout.encode("utf-8")) if entitled.stdout.strip() else {}
        if actual != (REQUIRED_ENTITLEMENTS if path in (host, bundle) else {}):
            raise ReleaseError(f"Unexpected release entitlements: {label}")


def assess_gatekeeper(commands, bundle, before_notary=False):
    result = commands.run("Gatekeeper before notarization" if before_notary else "Gatekeeper notarized app",
                          "spctl", ["--assess", "--type", "execute", "--verbose=4", bundle],
                          allow_failure=before_notary)
    output = result.stdout + result.stderr
    if before_notary and result.returncode and "source=Unnotarized Developer ID" in output:
        return "unnotarized"
    if result.returncode or "source=Notarized Developer ID" not in output:
        raise ReleaseError("Gatekeeper did not accept the notarized Developer ID app")
    return "accepted"


def inventory(bundle):
    return {path.relative_to(bundle).as_posix(): sha256(path)
            for path in sorted(bundle.rglob("*")) if path.is_file()}


def release(bundle, runtime, output, evidence, config, commands):
    version, native = validate_bundle(bundle, runtime)
    output.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    target = output / f"MathGPT-{version}-mac-{runtime.removeprefix('osx-')}.zip"
    manifest = target.with_suffix(".json")
    if target.exists() or manifest.exists():
        raise ReleaseError("Formal package already exists; use a fresh output directory instead of replacing a release")
    attempt = {"started_utc": utc_now(), "runtime": runtime, "version": version,
               "signed_notarized": False, "status": "in_progress",
               "real_mac_tested": False, "runtime_functional_tested": False}
    try:
        host = bundle / "Contents/MacOS/MathGPT"
        ordered = sorted((path for path in native if path != host),
                         key=lambda path: (-len(path.parts), path.as_posix())) + [host]
        for path in ordered:
            relative = path.relative_to(bundle).as_posix()
            identifier = APP_ID if path == host else APP_ID + ".native." + re.sub(r"[^A-Za-z0-9.-]", "-", relative)
            arguments = ["--force", "--sign", config["certificateSha1"], "--timestamp",
                         "--options", "runtime", "--identifier", identifier]
            if path == host:
                arguments += ["--entitlements", ENTITLEMENTS]
            commands.run(f"Sign {relative}", "codesign", [*arguments, path])
        commands.run("Sign outer app", "codesign",
                     ["--force", "--sign", config["certificateSha1"], "--timestamp",
                      "--options", "runtime", "--identifier", APP_ID,
                      "--entitlements", ENTITLEMENTS, bundle])
        verify_code(commands, bundle, native, config["teamId"])
        assess_gatekeeper(commands, bundle, before_notary=True)
        # The submitted archive is temporary. A final archive is created only
        # after the .app has its ticket stapled and all verifications pass.
        with tempfile.TemporaryDirectory(prefix=".mathgpt-notary-", dir=output) as folder:
            staging = Path(folder)
            submission = staging / "submission.zip"
            commands.run("Create notarization ZIP", "ditto",
                         ["-c", "-k", "--sequesterRsrc", "--keepParent", bundle, submission])
            submitted_hash = sha256(submission)
            response = commands.run("Submit for notarization", "xcrun",
                                    ["notarytool", "submit", submission, "--keychain-profile",
                                     config["notaryProfile"], "--wait", "--output-format", "json"],
                                    allow_failure=True)
            result = json_response(response, "notarytool submit")
            write_json(evidence / "notary-submission.json", result)
            submission_id = result.get("id")
            log = None
            if re.fullmatch(r"[0-9a-fA-F-]{36}", str(submission_id)):
                log_result = commands.run("Read notarization log", "xcrun",
                                          ["notarytool", "log", submission_id, "--keychain-profile",
                                           config["notaryProfile"]])
                log = json_response(log_result, "notarytool log")
                write_json(evidence / "notary-log.json", log)
            if response.returncode:
                raise ReleaseError("notarytool submit failed; no formal ZIP will be produced")
            submission_id = validate_notary_result(result)
            verify_notary_log(log or {}, submission_id, submitted_hash, config["teamId"])
            commands.run("Staple notarization ticket", "xcrun", ["stapler", "staple", "-v", bundle])
            commands.run("Validate stapled ticket", "xcrun", ["stapler", "validate", "-v", bundle])
            verify_code(commands, bundle, native, config["teamId"])
            assess_gatekeeper(commands, bundle)
            sealed_inventory = inventory(bundle)
            final_zip = staging / target.name
            commands.run("Create final stapled ZIP", "ditto",
                         ["-c", "-k", "--sequesterRsrc", "--keepParent", bundle, final_zip])
            expanded = staging / "expanded"
            commands.run("Expand final ZIP", "ditto", ["-x", "-k", final_zip, expanded])
            restored = expanded / "MathGPT.app"
            _, restored_native = validate_bundle(restored, runtime)
            if inventory(restored) != sealed_inventory:
                raise ReleaseError("Final ZIP changed sealed bundle contents")
            verify_code(commands, restored, restored_native, config["teamId"])
            commands.run("Validate archived stapled ticket", "xcrun", ["stapler", "validate", "-v", restored])
            assess_gatekeeper(commands, restored)
            result = {"runtime": runtime, "version": version, "channel": "release",
                      "file": target.name, "bytes": final_zip.stat().st_size, "sha256": sha256(final_zip),
                      "macho": True, "native_file_count": len(native), "unix_executable_bits": True,
                      "signed_notarized": True, "developer_id_signed": True, "hardened_runtime": True,
                      "team_id": config["teamId"], "certificate_sha1": config["certificateSha1"],
                      "bundle_identifier": APP_ID, "notarization_status": "Accepted",
                      "notarization_submission_id": submission_id, "submitted_archive_sha256": submitted_hash,
                      "notarization_log_sha256_checked": log.get("sha256") is not None,
                      "notarization_log_team_checked": log.get("teamId") is not None,
                      "stapled": True, "gatekeeper_assessed": True, "archive_roundtrip_verified": True,
                      "signing_host_macos": True, "real_mac_tested": False,
                      "runtime_functional_tested": False, "created_utc": utc_now()}
            # Keep the result beside the ZIP as a commit marker. A failure before
            # this point can never produce a ready release manifest.
            manifest_temp = staging / manifest.name
            write_json(manifest_temp, result)
            # Both paths are on the same filesystem. Exclusive hard links avoid
            # POSIX rename silently replacing a release from another build.
            target.hardlink_to(final_zip)
            try:
                manifest.hardlink_to(manifest_temp)
            except OSError:
                target.unlink()
                raise
            attempt.update({"status": "complete", "signed_notarized": True, "file": target.name})
            return result
    except Exception as error:
        attempt.update({"status": "failed", "failure_type": type(error).__name__})
        raise
    finally:
        attempt["finished_utc"] = utc_now()
        write_json(evidence / "release-attempt.json", attempt)
        write_json(evidence / "commands.json", commands.records)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("preflight", "release"))
    parser.add_argument("--config", type=Path)
    parser.add_argument("--runtime", choices=package.RUNTIMES)
    parser.add_argument("--bundle", type=Path)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build/packages")
    parser.add_argument("--evidence-dir", type=Path, default=ROOT / "build/verification/mac-signing")
    args = parser.parse_args()
    commands = Commands()
    try:
        config = preflight(args.config, commands)
        if args.action == "preflight":
            print(json.dumps({"stage": "preflight", "ready_to_sign": True, "signed_notarized": False}))
        else:
            if args.runtime is None or args.bundle is None:
                raise ReleaseError("release requires --runtime and a prepared --bundle")
            print(json.dumps(release(args.bundle.resolve(), args.runtime, args.output_dir.resolve(),
                                     args.evidence_dir.resolve(), config, commands)))
        return 0
    except (ReleaseError, OSError, ValueError, plistlib.InvalidFileException) as error:
        print(f"Mac release stopped: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
