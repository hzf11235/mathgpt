"""Contract tests only: no certificate creation, Apple access or signed outputs."""
import importlib.util
import json
import plistlib
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("mac_release_under_test", ROOT / "scripts/signing/MacRelease.py")
mac = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mac)

# Public-format parser fixtures only; these are not credentials or certificates.
TEAM = "TESTTEAM01"
FINGERPRINT = "0" * 40
JOB = "abcdef12-abcd-4abc-8abc-abcdef123456"
CONFIG = {"schemaVersion": 1, "certificateSha1": FINGERPRINT,
          "teamId": TEAM, "notaryProfile": "unit-parser-fixture"}
METADATA = (
    "Identifier=xyz.mathgpt.desktop\n"
    "CodeDirectory v=20500 size=123 flags=0x10000(runtime) hashes=1\n"
    "Authority=Developer ID Application: Parser Fixture (TESTTEAM01)\n"
    "TeamIdentifier=TESTTEAM01\n"
    "Timestamp=Jan 1, 2026 at 00:00:00\n"
)


class MacSigningContracts(unittest.TestCase):
    def test_wrong_host_stops_before_credential_or_tool_access(self):
        commands = Mock()
        with patch.object(mac.sys, "platform", "win32"), patch.object(mac, "read_config") as config:
            with self.assertRaisesRegex(mac.ReleaseError, "real macOS"):
                mac.preflight(None, commands)
        config.assert_not_called()
        commands.run.assert_not_called()

    def test_missing_credentials_cannot_be_ready(self):
        with self.assertRaisesRegex(mac.ReleaseError, "Missing Mac signing"):
            mac.read_config(None, {})
        with tempfile.TemporaryDirectory() as folder:
            config = Path(folder) / "empty.json"
            config.write_text(json.dumps({"schemaVersion": 1, "certificateSha1": "", "teamId": "", "notaryProfile": ""}), encoding="utf-8")
            with self.assertRaises(mac.ReleaseError):
                mac.read_config(config, {})

    def test_password_fields_are_rejected_without_echoing_values(self):
        with tempfile.TemporaryDirectory() as folder:
            config = Path(folder) / "bad.json"
            config.write_text(json.dumps({**CONFIG, "password": "DO-NOT-ECHO"}), encoding="utf-8")
            with self.assertRaises(mac.ReleaseError) as caught:
                mac.read_config(config, {})
            self.assertNotIn("DO-NOT-ECHO", str(caught.exception))

    def test_config_refs_can_come_from_environment(self):
        actual = mac.read_config(None, {"MATHGPT_MAC_CERTIFICATE_SHA1": FINGERPRINT,
                                       "MATHGPT_MAC_TEAM_ID": TEAM,
                                       "MATHGPT_MAC_NOTARY_PROFILE": "unit-parser-fixture"})
        self.assertEqual(actual["certificateSha1"], FINGERPRINT)
        self.assertEqual(actual["teamId"], TEAM)

    def test_only_selected_developer_id_team_is_accepted(self):
        for name in ("Apple Development: Fixture (TESTTEAM01)",
                     "Developer ID Application: Fixture (OTHERTEAM1)", "adhoc"):
            with self.subTest(name=name), self.assertRaises(mac.ReleaseError):
                mac.require_developer_id(f'  1) {FINGERPRINT} "{name}"', CONFIG)
        self.assertEqual(mac.require_developer_id(
            f'  1) {FINGERPRINT} "Developer ID Application: Fixture ({TEAM})"', CONFIG),
            f"Developer ID Application: Fixture ({TEAM})")

    def test_debug_and_memory_bypass_entitlements_are_absent(self):
        self.assertEqual(plistlib.loads(mac.ENTITLEMENTS.read_bytes()),
                         {"com.apple.security.cs.allow-jit": True})

    def test_incomplete_signature_metadata_is_rejected(self):
        mac.validate_signature_metadata(METADATA, TEAM, mac.APP_ID)
        for altered in (METADATA.replace("runtime", ""), METADATA.replace("Timestamp=", "Signed Time="),
                        METADATA.replace(TEAM, "OTHERTEAM1"), METADATA + "Signature=adhoc\n",
                        METADATA.replace(mac.APP_ID, "other.application")):
            with self.subTest(metadata=altered), self.assertRaises(mac.ReleaseError):
                mac.validate_signature_metadata(altered, TEAM, mac.APP_ID)

    def test_notary_status_must_be_final_accepted_with_submission_id(self):
        for status in ("Invalid", "Rejected", "In Progress", "", None):
            with self.subTest(status=status), self.assertRaises(mac.ReleaseError):
                mac.validate_notary_result({"status": status, "id": JOB})
        with self.assertRaises(mac.ReleaseError):
            mac.validate_notary_result({"status": "Accepted", "id": "missing"})
        self.assertEqual(mac.validate_notary_result({"status": "Accepted", "id": JOB}), JOB)

    def test_notary_log_is_bound_to_exact_archive_and_team(self):
        accepted = {"jobId": JOB, "status": "Accepted", "sha256": "a" * 64, "teamId": TEAM}
        mac.verify_notary_log(accepted, JOB, "a" * 64, TEAM)
        # Apple's documentation omits these optional fields and uses uppercase
        # UUIDs; acceptance is still bound to the same returned submission ID.
        mac.verify_notary_log({"jobId": JOB.upper(), "status": "Accepted"}, JOB, "a" * 64, TEAM)
        for field, bad in (("jobId", "other"), ("status", "Invalid"), ("sha256", "b" * 64), ("teamId", "OTHERTEAM1")):
            with self.subTest(field=field), self.assertRaises(mac.ReleaseError):
                mac.verify_notary_log({**accepted, field: bad}, JOB, "a" * 64, TEAM)

    def test_gatekeeper_unnotarized_is_allowed_only_before_submission(self):
        commands = Mock()
        commands.run.return_value = subprocess.CompletedProcess([], 1, "", "rejected\nsource=Unnotarized Developer ID\n")
        self.assertEqual(mac.assess_gatekeeper(commands, Path("MathGPT.app"), before_notary=True), "unnotarized")
        with self.assertRaises(mac.ReleaseError):
            mac.assess_gatekeeper(commands, Path("MathGPT.app"))
        commands.run.return_value = subprocess.CompletedProcess([], 0, "", "accepted\nsource=Notarized Developer ID\n")
        self.assertEqual(mac.assess_gatekeeper(commands, Path("MathGPT.app")), "accepted")
        commands.run.return_value = subprocess.CompletedProcess([], 1, "", "rejected\nsource=no usable signature\n")
        with self.assertRaises(mac.ReleaseError):
            mac.assess_gatekeeper(commands, Path("MathGPT.app"), before_notary=True)

    def test_signing_failure_never_emits_formal_package_or_ready_manifest(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            bundle = base / "MathGPT.app"
            host = bundle / "Contents/MacOS/MathGPT"
            commands = Mock()
            commands.records = []
            commands.run.side_effect = mac.ReleaseError("Unit fixture: signing stopped")
            with patch.object(mac, "validate_bundle", return_value=("0.0.0", [host])):
                with self.assertRaises(mac.ReleaseError):
                    mac.release(bundle, "osx-arm64", base / "output", base / "evidence", CONFIG, commands)
            self.assertEqual(list((base / "output").glob("*")), [])
            attempt = json.loads((base / "evidence/release-attempt.json").read_text(encoding="utf-8"))
            self.assertEqual(attempt["status"], "failed")
            self.assertFalse(attempt["signed_notarized"])

    def test_existing_formal_release_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            target = base / "MathGPT-0.0.0-mac-arm64.zip"
            target.write_bytes(b"existing package fixture")
            commands = Mock()
            with patch.object(mac, "validate_bundle", return_value=("0.0.0", [])):
                with self.assertRaisesRegex(mac.ReleaseError, "already exists"):
                    mac.release(base / "MathGPT.app", "osx-arm64", base, base / "evidence", CONFIG, commands)
            self.assertEqual(target.read_bytes(), b"existing package fixture")
            commands.run.assert_not_called()

    def test_notary_stapler_and_gatekeeper_failures_cannot_emit_release(self):
        # All commands below are mocks. No certificate, Apple request or signed
        # package is produced; each path must stop before final ZIP creation.
        for failure in ("Submit for notarization", "Read notarization log",
                        "Staple notarization ticket", "Validate stapled ticket",
                        "Gatekeeper notarized app"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as folder:
                base = Path(folder)
                bundle = base / "MathGPT.app"
                host = bundle / "Contents/MacOS/MathGPT"
                helper = bundle / "Contents/MacOS/MathGPT.Bridge"
                library = bundle / "Contents/MacOS/libcoreclr.dylib"
                calls = []
                submission_hash = None

                def command(label, tool, arguments, **options):
                    nonlocal submission_hash
                    calls.append((label, list(map(str, arguments))))
                    if label == "Create notarization ZIP":
                        archive = Path(arguments[-1])
                        archive.write_bytes(b"unit archive fixture - not signed")
                        submission_hash = mac.sha256(archive)
                    if label == "Submit for notarization":
                        return subprocess.CompletedProcess([], 1 if label == failure else 0,
                            json.dumps({"id": JOB, "status": "Invalid" if label == failure else "Accepted"}), "")
                    if label == "Read notarization log":
                        return subprocess.CompletedProcess([], 0, json.dumps({
                            "jobId": JOB, "status": "Accepted", "teamId": TEAM,
                            "sha256": "wrong" if label == failure else submission_hash}), "")
                    if label == failure:
                        raise mac.ReleaseError("Unit fixture rejection")
                    if label == "Gatekeeper before notarization":
                        return subprocess.CompletedProcess([], 1, "", "source=Unnotarized Developer ID\n")
                    return subprocess.CompletedProcess([], 0, "", "")

                commands = Mock()
                commands.records = []
                commands.run.side_effect = command
                with patch.object(mac, "validate_bundle", return_value=("0.0.0", [host, helper, library])), \
                        patch.object(mac, "verify_code"):
                    with self.assertRaises(mac.ReleaseError):
                        mac.release(bundle, "osx-arm64", base / "output", base / "evidence", CONFIG, commands)
                self.assertEqual(list((base / "output").glob("*")), [])
                attempt = json.loads((base / "evidence/release-attempt.json").read_text(encoding="utf-8"))
                self.assertFalse(attempt["signed_notarized"])
                self.assertEqual(attempt["status"], "failed")
                self.assertFalse(any(label == "Create final stapled ZIP" for label, _ in calls))
                sign_calls = [(label, arguments) for label, arguments in calls if label.startswith("Sign ")]
                self.assertEqual(len(sign_calls), 4)
                for label, arguments in sign_calls:
                    self.assertNotIn("--deep", arguments)
                    self.assertEqual("--entitlements" in arguments,
                                     label in ("Sign outer app", "Sign Contents/MacOS/MathGPT"))
                submit = next(arguments for label, arguments in calls if label == "Submit for notarization")
                self.assertIn("--keychain-profile", submit)
                self.assertNotIn("--password", submit)
                self.assertNotIn("--apple-id", submit)

    def test_macho_architecture_is_checked_from_headers(self):
        with tempfile.TemporaryDirectory() as folder:
            binary = Path(folder) / "native"
            binary.write_bytes(b"\xcf\xfa\xed\xfe" + struct.pack("<I", 0x0100000C))
            self.assertEqual(mac.package.macho_architectures(binary), {0x0100000C})
            binary.write_bytes(b"\xca\xfe\xba\xbe" + struct.pack(">I", 1))
            with self.assertRaisesRegex(ValueError, "fat Mach-O"):
                mac.package.macho_architectures(binary)

    def test_unrecognized_json_from_apple_is_not_success(self):
        for text in ("", "Accepted", "[]"):
            with self.subTest(text=text), self.assertRaises(mac.ReleaseError):
                mac.json_response(subprocess.CompletedProcess([], 0, text, ""), "fixture")


if __name__ == "__main__":
    unittest.main()
