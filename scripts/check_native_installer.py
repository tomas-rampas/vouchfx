#!/usr/bin/env python3
"""Install, discover on PATH, and remove one candidate on a fresh hosted runner.

Never run this acceptance journey on a developer machine. Extraction checks in
release.yml remain separate; only this journey measures native installation.
"""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import plistlib
import re
import shutil
import stat
import subprocess
import sys

UPGRADE_CODE = "{7C9D4A2B-E3F5-4B6A-8D1C-9E2F3A4B5C6D}"
RECEIPT = "com.vouchfx.cli"
MAC_ROOT = PurePosixPath("usr/local/lib/vouchfx")
MAC_LINK = PurePosixPath("usr/local/bin/vouchfx")
MAC_SHARED = {PurePosixPath(p) for p in ("usr", "usr/local", "usr/local/lib", "usr/local/bin")}


class InvalidJourney(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise InvalidJourney(message)


def require_hosted(environment):
    require(environment.get("GITHUB_ACTIONS") == "true" and
            environment.get("RUNNER_ENVIRONMENT") == "github-hosted",
            "Native installer acceptance requires an isolated GitHub-hosted runner")


def validate_version(version):
    require(re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version) is not None,
            "Expected a release version without shell syntax or build metadata")


def assert_cli_version(output, version):
    text = output.strip()
    require(text == version or re.fullmatch(re.escape(version) + r"\+[0-9A-Za-z.-]+", text) is not None,
            f"Installed CLI reported {text!r}, expected {version!r}")


def mac_receipt_paths(output):
    paths = set()
    for line in output.splitlines():
        path = PurePosixPath(line)
        require(line and not path.is_absolute() and ".." not in path.parts and
                str(path) == line and path not in paths, f"Unsafe or duplicate receipt path: {line!r}")
        require(path in MAC_SHARED or path == MAC_LINK or path == MAC_ROOT or MAC_ROOT in path.parents,
                f"Receipt contains a foreign path: {line!r}")
        paths.add(path)
    require(MAC_ROOT in paths and MAC_LINK in paths, "Receipt omitted owned CLI directory or PATH link")
    return paths - MAC_SHARED


def payload_manifest(root):
    require(root.is_dir() and not root.is_symlink(), f"Not a real payload directory: {root}")
    files = {}
    for path in root.rglob("*"):
        mode = path.lstat().st_mode
        require(not path.is_symlink() and not (getattr(path.lstat(), "st_file_attributes", 0) & 1024),
                f"Unexpected link in native payload: {path}")
        if stat.S_ISREG(mode):
            files[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
        else:
            require(stat.S_ISDIR(mode), f"Unexpected payload entry: {path}")
    require(files, "Empty native payload")
    return files


class Journey:
    def __init__(self, directory):
        self.directory = directory
        require(not directory.exists(), f"Evidence directory already exists: {directory}")
        directory.mkdir(parents=True)
        self.commands = []
        self.facts = {}

    def run(self, arguments, allowed=(0,), timeout=30):
        entry = {"arguments": [str(a) for a in arguments], "allowedExitCodes": list(allowed),
                 "startedAtUTC": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                 "timeoutSeconds": timeout}
        self.commands.append(entry)
        try:
            result = subprocess.run(entry["arguments"], capture_output=True, text=True,
                                    encoding="utf8", errors="replace", timeout=timeout,
                                    creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
            entry.update(exitCode=result.returncode, stdout=result.stdout, stderr=result.stderr)
        except subprocess.TimeoutExpired as error:
            entry.update(exitCode=None, timedOut=True, stdout=str(error.stdout), stderr=str(error.stderr))
            raise InvalidJourney(f"Owned command exceeded {timeout}s: {arguments[0]}") from error
        finally:
            (self.directory / f"command-{len(self.commands):02d}.json").write_text(
                json.dumps(entry, indent=2), encoding="utf8")
        require(result.returncode in allowed, f"Command exited {result.returncode}: {arguments[0]}")
        return result

    def assert_absent(self, *paths):
        for path in paths:
            require(not os.path.lexists(path), f"Existing or residual installation path: {path}")
        require(shutil.which("vouchfx") is None, "A vouchfx command already exists or remains on PATH")

    def assert_installed(self, root, expected, publish, version, link=None):
        require(payload_manifest(root) == payload_manifest(publish), "Installed files differ from the candidate publish")
        if link is not None:
            require(link.is_symlink() and os.readlink(link) == str(expected), "Incorrect installed PATH symlink")
        found = shutil.which("vouchfx")
        require(found is not None and Path(found).resolve() == expected.resolve(),
                f"PATH discovery resolved {found!r} instead of the installed candidate")
        # Launch the bare command in a new process, with the installed PATH.
        # The extracted/archive executable is never substituted here.
        result = self.run(["vouchfx", "--version"])
        assert_cli_version(result.stdout, version)
        self.facts.update(pathCommand=found, cliVersion=result.stdout.strip(), installedPayloadExact=True)

    def linux(self, package, publish, version):
        root, link = Path("/usr/lib/vouchfx"), Path("/usr/local/bin/vouchfx")
        self.assert_absent(root, link)
        registered = self.run(["dpkg-query", "--show", "--showformat=${binary:Package}\n"]).stdout.splitlines()
        require(not any(name.split(":", 1)[0] == "vouchfx" for name in registered), "Debian vouchfx registration exists or remains")
        for field, value in (("Package", "vouchfx"), ("Version", version.split("-", 1)[0]), ("Architecture", "amd64")):
            actual = self.run(["dpkg-deb", "--field", package, field]).stdout.strip()
            require(actual == value, f"Wrong Debian {field}: {actual!r}")
        installed = False
        try:
            self.run(["sudo", "-n", "dpkg", "--install", package], timeout=300)
            installed = True
            status = self.run(["dpkg-query", "--show", "--showformat=${Status}", "vouchfx"]).stdout
            require(status == "install ok installed", f"Debian package is not configured: {status!r}")
            self.assert_installed(root, root / "vouchfx", publish, version, link)
        finally:
            if installed:
                self.run(["sudo", "-n", "dpkg", "--purge", "vouchfx"], timeout=300)
        registered = self.run(["dpkg-query", "--show", "--showformat=${binary:Package}\n"]).stdout.splitlines()
        require(not any(name.split(":", 1)[0] == "vouchfx" for name in registered), "Debian vouchfx registration exists or remains")
        self.assert_absent(root, link)
        self.facts.update(packageRegistered=False, removalComplete=True)

    def macos(self, package, publish, version):
        root, link = Path("/") / MAC_ROOT, Path("/") / MAC_LINK
        self.assert_absent(root, link)
        require(RECEIPT not in self.run(["/usr/sbin/pkgutil", "--pkgs"]).stdout.splitlines(),
                "A prior or residual vouchfx macOS receipt exists")
        installed = False
        try:
            self.run(["sudo", "-n", "/usr/sbin/installer", "-pkg", package, "-target", "/"], timeout=300)
            installed = True
            info = plistlib.loads(self.run(["/usr/sbin/pkgutil", "--pkg-info-plist", RECEIPT]).stdout.encode("utf8"))
            require(info.get("pkg-version") == version and info.get("volume") == "/" and
                    info.get("install-location") in ("", "/"), "Unexpected candidate receipt version or destination")
            self.assert_installed(root, root / "vouchfx", publish, version, link)
        finally:
            if installed:
                # --forget removes only a receipt. Remove precisely its owned files
                # first, without recursive deletion or touching shared directories.
                paths = mac_receipt_paths(self.run(["/usr/sbin/pkgutil", "--files", RECEIPT]).stdout)
                for relative in sorted(paths, key=lambda p: (len(p.parts), str(p)), reverse=True):
                    path = Path("/") / relative
                    require(all(not p.is_symlink() for p in path.parents), f"Linked parent of receipt file: {path}")
                    require(os.path.lexists(path), f"Receipt entry missing before removal: {path}")
                    if path.is_symlink() or path.is_file():
                        self.run(["sudo", "-n", "/bin/rm", "-f", str(path)])
                    else:
                        require(path.is_dir(), f"Unexpected receipt entry: {path}")
                        self.run(["sudo", "-n", "/bin/rmdir", str(path)])
                self.run(["sudo", "-n", "/usr/sbin/pkgutil", "--forget", RECEIPT])
        require(RECEIPT not in self.run(["/usr/sbin/pkgutil", "--pkgs"]).stdout.splitlines(),
                "A prior or residual vouchfx macOS receipt exists")
        self.assert_absent(root, link)
        self.facts.update(packageRegistered=False, removalComplete=True)

    @staticmethod
    def windows_products():
        import ctypes
        from ctypes import wintypes
        enumerate_product = ctypes.WinDLL("msi").MsiEnumRelatedProductsW
        enumerate_product.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPWSTR]
        enumerate_product.restype = wintypes.UINT
        products = []
        for index in range(100):
            buffer = ctypes.create_unicode_buffer(39)
            code = enumerate_product(UPGRADE_CODE, 0, index, buffer)
            if code == 259:  # ERROR_NO_MORE_ITEMS, not a generic error/absence.
                return products
            require(code == 0, f"MSI product enumeration exited {code}")
            require(re.fullmatch(r"\{[0-9A-Fa-f-]{36}\}", buffer.value) is not None, "Invalid MSI product code")
            products.append(buffer.value)
        raise InvalidJourney("MSI product enumeration exceeded its bound")

    @staticmethod
    def windows_paths():
        import winreg
        def value(hive, key):
            try:
                with winreg.OpenKey(hive, key) as handle:
                    return winreg.QueryValueEx(handle, "Path")[0]
            except FileNotFoundError:
                return ""
        return (value(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
                value(winreg.HKEY_CURRENT_USER, "Environment"))

    @staticmethod
    def windows_registry_proof():
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\vouchfx", 0,
                               winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as handle:
                return winreg.QueryValueEx(handle, "Installed")[0]
        except FileNotFoundError:
            return None

    @staticmethod
    def refresh_windows_path():
        os.environ["PATH"] = os.path.expandvars(";".join(Journey.windows_paths()))

    def windows(self, package, publish, version):
        root = Path(os.environ["ProgramFiles"]) / "vouchfx"
        expected = root / "vouchfx.exe"
        # Refuse inherited-only commands before registry PATH can hide them.
        self.assert_absent(root)
        before_path = self.windows_paths()
        self.refresh_windows_path()
        self.assert_absent(root)
        require(not self.windows_products() and self.windows_registry_proof() is None,
                "A prior vouchfx MSI product or registration exists")
        require(all(str(root).casefold() != entry.rstrip("\\").casefold()
                    for item in before_path for entry in os.path.expandvars(item).split(";")),
                "A prior vouchfx MSI PATH entry exists")
        msiexec = str(Path(os.environ["SystemRoot"]) / "System32/msiexec.exe")
        installed = False
        try:
            self.run([msiexec, "/i", package, "/qn", "/norestart", "/L*v", self.directory / "install-msi.log"],
                     allowed=(0, 3010), timeout=300)
            installed = True
            require(len(self.windows_products()) == 1 and self.windows_registry_proof() == 1,
                    "Candidate MSI product or install registration missing")
            self.refresh_windows_path()
            self.assert_installed(root, expected, publish, version)
        finally:
            if installed:
                require(len(self.windows_products()) == 1, "Refusing ambiguous MSI product removal")
                self.run([msiexec, "/x", package, "/qn", "/norestart", "/L*v", self.directory / "remove-msi.log"],
                         allowed=(0, 3010), timeout=300)
        self.refresh_windows_path()
        self.assert_absent(root)
        require(not self.windows_products() and self.windows_registry_proof() is None,
                "Candidate MSI product or registration remains")
        require(self.windows_paths() == before_path, "MSI removal did not restore machine/user PATH")
        self.facts.update(packageRegistered=False, pathRestored=True, removalComplete=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--publish", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--report-directory", type=Path, required=True)
    args = parser.parse_args()
    require_hosted(os.environ)
    validate_version(args.version)
    require(sys.platform in ("win32", "linux", "darwin"), "Unsupported installer host")
    extension = {"win32": ".msi", "linux": ".deb", "darwin": ".pkg"}[sys.platform]
    package = args.package.resolve(strict=True)
    require(package.is_file() and package.suffix == extension, "Wrong native installer type")
    runner_temp = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
    report = args.report_directory.resolve()
    require(report != runner_temp and runner_temp in report.parents, "Evidence must be under RUNNER_TEMP")
    journey = Journey(report)
    result = {"schemaVersion": 1, "hostPlatform": sys.platform, "expectedVersion": args.version,
              "packagePath": str(package), "packageSha256": hashlib.sha256(package.read_bytes()).hexdigest(),
              "sourceCommit": os.environ.get("GITHUB_SHA"), "accepted": False}
    try:
        {"win32": journey.windows, "linux": journey.linux, "darwin": journey.macos}[sys.platform](
            str(package), args.publish.resolve(strict=True), args.version)
        result["accepted"] = True
    except Exception as error:
        result["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        result.update(facts=journey.facts, commands=journey.commands)
        (report / "result.json").write_text(json.dumps(result, indent=2), encoding="utf8")
        print(json.dumps({"accepted": result["accepted"], "commands": len(journey.commands), "report": str(report)}))


if __name__ == "__main__":
    main()
