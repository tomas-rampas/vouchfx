"""Distribution acceptance policy regressions; no SDK, native processes or Docker."""

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("check_distribution", Path(__file__).parents[1] / "check_distribution.py")
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class DistributionTests(unittest.TestCase):
    def fixture(self, path):
        root = "tools/net8.0/any/dcp/13.4.2/"
        files = {"tools/net8.0/any/dcp/supported-rids.txt": "\n".join(sorted(checker.SUPPORTED_RIDS)).encode()}
        for rid in checker.SUPPORTED_RIDS:
            tools = root + rid + "/tools/"
            executable = "dcp.exe" if rid == "win-x64" else "dcp"
            if rid == "win-x64":
                header = bytearray(256)
                header[:2] = b"MZ"
                header[60:64] = (128).to_bytes(4, "little")
                header[128:134] = b"PE\0\0\x64\x86"
                binary = bytes(header)
            elif rid == "linux-x64":
                binary = b"\x7fELF\x02\x01" + bytes(12) + b"\x3e\0"
            else:
                binary = b"\xcf\xfa\xed\xfe" + (b"\x07\0\0\x01" if rid == "osx-x64" else b"\x0c\0\0\x01")
            payload = {executable: binary, "dcptun_c": b"tunnel", "NOTICE": b"notice", "LICENSE": b"licence"}
            manifest = {"files": [{"fileName": "./" + name, "checksums": [{"algorithm": "SHA256",
                         "checksumValue": hashlib.sha256(data).hexdigest()}]} for name, data in payload.items()]}
            payload["_manifest/spdx_2.2/manifest.spdx.json"] = json.dumps(manifest).encode()
            files.update({tools + name: data for name, data in payload.items()})
        self.write(path, files)
        return files

    @staticmethod
    def write(path, files):
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as package:
            for name, data in files.items():
                package.writestr(name, data)

    def test_complete_package_and_each_missing_required_component(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tool.nupkg"
            files = self.fixture(path)
            self.assertIn("four complete", checker.validate_package(path, "13.4.2"))
            for removed in files:
                with self.subTest(removed=removed):
                    self.write(path, {name: data for name, data in files.items() if name != removed})
                    with self.assertRaises(checker.InvalidDistribution):
                        checker.validate_package(path, "13.4.2")

    def test_tampered_native_binary_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tool.nupkg"
            files = self.fixture(path)
            name = "tools/net8.0/any/dcp/13.4.2/linux-x64/tools/dcp"
            files[name] += b"corruption"
            self.write(path, files)
            with self.assertRaisesRegex(checker.InvalidDistribution, "SHA256 mismatch"):
                checker.validate_package(path, "13.4.2")

    def test_wrong_pinned_version_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tool.nupkg"
            self.fixture(path)
            with self.assertRaises(checker.InvalidDistribution):
                checker.validate_package(path, "13.3.0")

    def test_source_payload_comparison_rejects_altered_notice(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "tool.nupkg"
            cache = root / "nuget"
            files = self.fixture(path)
            for rid in checker.SUPPORTED_RIDS:
                prefix = f"tools/net8.0/any/dcp/13.4.2/{rid}/tools/"
                source = cache / f"aspire.hosting.orchestration.{rid}" / "13.4.2" / "tools"
                for name, data in files.items():
                    if name.startswith(prefix):
                        target = source / name[len(prefix):]
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes(data)
            checker.validate_package(path, "13.4.2", cache)
            name = "tools/net8.0/any/dcp/13.4.2/osx-arm64/tools/NOTICE"
            files[name] = b"changed notice"
            self.write(path, files)
            with self.assertRaisesRegex(checker.InvalidDistribution, "differs from restored NuGet"):
                checker.validate_package(path, "13.4.2", cache)

    def test_self_consistent_wrong_platform_binary_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tool.nupkg"
            files = self.fixture(path)
            prefix = "tools/net8.0/any/dcp/13.4.2/linux-x64/tools/"
            files[prefix + "dcp"] = b"wrong binary format"
            manifest_path = prefix + "_manifest/spdx_2.2/manifest.spdx.json"
            manifest = json.loads(files[manifest_path])
            next(entry for entry in manifest["files"] if entry["fileName"] == "./dcp")["checksums"][0]["checksumValue"] = hashlib.sha256(files[prefix + "dcp"]).hexdigest()
            files[manifest_path] = json.dumps(manifest).encode()
            self.write(path, files)
            with self.assertRaisesRegex(checker.InvalidDistribution, "does not match declared platform"):
                checker.validate_package(path, "13.4.2")

    def test_oversized_package_is_rejected_before_zip_read(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tool.nupkg"
            with path.open("wb") as stream:
                stream.truncate(checker.MAX_PACKAGE_BYTES + 1)
            with self.assertRaisesRegex(checker.InvalidDistribution, "250000000 bytes"):
                checker.validate_package(path, "13.4.2")


if __name__ == "__main__":
    unittest.main()
