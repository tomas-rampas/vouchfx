"""Exercise the actual pack task against unsigned NuGet ZIPs; requires the .NET SDK."""

import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]
TARGETS = ROOT / "src/Cli/Vouchfx.Cli/Vouchfx.PackageCompression.targets"


class PackageCompressionTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="vouchfx-package-compression-")
        self.addCleanup(self.scratch.cleanup)
        self.directory = Path(self.scratch.name)
        self.package = self.directory / "candidate.nupkg"
        self.project = self.directory / "compression.proj"
        project = ET.Element("Project")
        ET.SubElement(project, "Import", Project=str(TARGETS))
        properties = ET.SubElement(project, "PropertyGroup")
        ET.SubElement(properties, "IsPackable").text = "true"
        ET.SubElement(properties, "PackAsTool").text = "true"
        target = ET.SubElement(project, "Target", Name="Pack")
        items = ET.SubElement(target, "ItemGroup")
        ET.SubElement(items, "NuGetPackOutput", Include="$(CompressionProbePackage)")
        ET.ElementTree(project).write(self.project, encoding="utf-8", xml_declaration=True)

    def compress(self):
        dotnet = shutil.which("dotnet")
        self.assertIsNotNone(dotnet, "The .NET SDK is required to verify the real packaging task.")
        return subprocess.run(
            [dotnet, "msbuild", str(self.project), "-t:Pack", "-nologo", "-verbosity:minimal",
             "-p:CompressionProbePackage=" + str(self.package)],
            cwd=ROOT, capture_output=True, text=True, timeout=120)

    def entry(self, name, data, mode=0o644):
        entry = zipfile.ZipInfo(name, date_time=(2020, 3, 4, 5, 6, 8))
        # A new NuGet package is created on the same OS as the pack task.
        entry.create_system = 0 if os.name == "nt" else 3
        entry.external_attr = mode << 16
        entry.comment = b"member metadata"
        return entry, data

    def write(self, entries):
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.comment = b"archive metadata"
            for entry, data in entries:
                archive.writestr(entry, data)

    def snapshot(self):
        with zipfile.ZipFile(self.package) as archive:
            return archive.comment, [
                (entry.filename, entry.date_time, entry.external_attr, entry.create_system,
                 entry.comment, archive.read(entry)) for entry in archive.infolist()]

    def test_all_payload_and_metadata_survive_and_repeated_pack_is_stable(self):
        self.write([
            self.entry("tool.nuspec", b"<package/>"),
            self.entry("tools/net8.0/any/dcp/linux-x64/tools/dcp", bytes(range(256)) * 4096, 0o755),
            self.entry("tools/net8.0/any/dcp/linux-x64/tools/NOTICE", b"licence and notices\x00" * 4096),
            self.entry("tools/net8.0/any/empty/", b"", 0o755),
            self.entry("tools/net8.0/any/\u017elu\u0165ou\u010dk\u00fd.txt", b"Unicode file name"),
        ])
        before = self.snapshot()
        original_size = self.package.stat().st_size
        result = self.compress()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(before, self.snapshot())
        self.assertLess(self.package.stat().st_size, original_size)
        first_hash = hashlib.sha256(self.package.read_bytes()).digest()
        result = self.compress()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(first_hash, hashlib.sha256(self.package.read_bytes()).digest())
        self.assertEqual([], list(self.directory.glob("*.tmp")))

    def test_signed_package_is_refused_without_changing_original(self):
        self.write([self.entry("tool.nuspec", b"<package/>"), self.entry(".signature.p7s", b"signature")])
        original = self.package.read_bytes()
        result = self.compress()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Refusing to recompress a signed NuGet package", result.stdout + result.stderr)
        self.assertEqual(original, self.package.read_bytes())
        self.assertEqual([], list(self.directory.glob("*.tmp")))

    def test_duplicate_entries_are_refused_without_changing_original(self):
        with self.assertWarns(UserWarning):
            self.write([self.entry("same.txt", b"one"), self.entry("same.txt", b"two")])
        original = self.package.read_bytes()
        result = self.compress()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Duplicate package entry", result.stdout + result.stderr)
        self.assertEqual(original, self.package.read_bytes())
        self.assertEqual([], list(self.directory.glob("*.tmp")))

    def test_malformed_package_is_refused_without_changing_original(self):
        self.package.write_bytes(b"not a ZIP archive")
        original = self.package.read_bytes()
        result = self.compress()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(original, self.package.read_bytes())
        self.assertEqual([], list(self.directory.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
