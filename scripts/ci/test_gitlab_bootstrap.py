"""Exercise the actual GitLab client bootstrap with inert command boundaries."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from docker_ci_support import BASH, ROOT, workflow


class GitLabBootstrapTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.script = workflow(ROOT / "ci/gitlab/vouchfx-run.gitlab-ci.yml")[".vouchfx-run"]["before_script"][0]

    def run_bootstrap(self, arch="x86_64", existing=False, checksum_exit=0, curl_exit=0):
        # Functions substitute process boundaries, never the bootstrap's control flow.
        # No network, extraction, installation or deletion is performed by these stubs.
        prefix = r'''set -eu
command() { if [ "$1" = -v ] && [ "$2" = docker ]; then [ "$EXISTING" = 1 ]; else builtin command "$@"; fi; }
uname() { printf '%s\n' "$ARCH"; }
mktemp() { printf '%s\n' /inert-bootstrap-fixture; }
rm() { printf 'cleanup\n'; }
curl() { printf 'curl %s\n' "$*"; return "$CURL_EXIT"; }
sha256sum() { read -r digest archive; printf 'checksum %s\n' "$digest"; return "$CHECKSUM_EXIT"; }
tar() { printf 'extract\n'; }
install() { printf 'install %s\n' "$*"; }
docker() { printf 'docker %s\n' "$*"; }
'''
        env = dict(os.environ, ARCH=arch, EXISTING=str(int(existing)),
                   CHECKSUM_EXIT=str(checksum_exit), CURL_EXIT=str(curl_exit))
        return subprocess.run([BASH, "-c", prefix + self.script], env=env,
                              capture_output=True, text=True, timeout=10)

    def test_existing_client_is_retained(self):
        result = self.run_bootstrap(existing=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("docker --version\n", result.stdout)

    def test_supported_architectures_install_only_after_checksum(self):
        for arch, target in (("x86_64", "x86_64"), ("aarch64", "aarch64"), ("arm64", "aarch64")):
            with self.subTest(arch=arch):
                result = self.run_bootstrap(arch=arch)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn(f"/stable/{target}/docker-27.5.1.tgz", result.stdout)
                self.assertLess(result.stdout.index("checksum "), result.stdout.index("extract\n"))
                self.assertLess(result.stdout.index("extract\n"), result.stdout.index("install "))
                self.assertIn("cleanup\n", result.stdout)
                self.assertTrue(result.stdout.endswith("docker --version\n"))

    def test_download_and_checksum_failures_prevent_installation(self):
        for options, expected in (({"curl_exit": 22}, 22), ({"checksum_exit": 1}, 1)):
            with self.subTest(options=options):
                result = self.run_bootstrap(**options)
                self.assertEqual(expected, result.returncode, result.stderr)
                self.assertNotIn("extract\n", result.stdout)
                self.assertNotIn("install ", result.stdout)
                self.assertNotIn("docker --version", result.stdout)
                self.assertIn("cleanup\n", result.stdout)

    def test_unsupported_architecture_fails_without_download(self):
        result = self.run_bootstrap(arch="s390x")
        self.assertEqual(1, result.returncode)
        self.assertNotIn("curl ", result.stdout)
        self.assertIn("supply an image with a Docker client", result.stderr)

    def test_actual_block_has_valid_shell_syntax_and_shellcheck(self):
        with tempfile.TemporaryDirectory(prefix="vouchfx-gitlab-shell-") as scratch:
            script = Path(scratch) / "bootstrap.sh"
            script.write_bytes(("#!/bin/sh\n" + self.script).encode("utf-8"))
            for command in ([BASH, "-n", str(script)],
                            [shutil.which("shellcheck"), "--shell=sh", str(script)]):
                result = subprocess.run(command, capture_output=True, text=True, timeout=15)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)


class GitLabDaemonRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.script = workflow(ROOT / "ci/gitlab/vouchfx-run.gitlab-ci.yml")[".vouchfx-run"]["script"][0]

    def test_run_exit_and_reports_survive_product_failure(self):
        for code in (0, 1, 3, 4):
            with self.subTest(code=code), tempfile.TemporaryDirectory(prefix="vouchfx-gitlab-run-") as scratch:
                prefix = r'''set -eu
docker() {
  case "$1" in
    create) printf 'runner-id\n' ;;
    exec) case "$3" in bash) return "$RUN_EXIT" ;; esac ;;
    cp) case "$2" in runner-id:*) printf 'report-copy\n' ;; esac ;;
    rm) printf 'runner-cleanup\n' > "$CLEANUP_MARKER" ;;
  esac
}
'''
                env = dict(os.environ, CI_PROJECT_DIR=scratch, VOUCHFX_SCENARIO_PATH="suite",
                           VOUCHFX_DOTNET_IMAGE="sdk:fixture", VOUCHFX_FAIL_ON_ENV_ERROR="false",
                           VOUCHFX_FAIL_ON_INCONCLUSIVE="false", RUN_EXIT=str(code),
                           CLEANUP_MARKER=str(Path(scratch) / "cleanup-marker"))
                result = subprocess.run([BASH, "-c", prefix + self.script], env=env,
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(code, result.returncode, result.stdout + result.stderr)
                self.assertEqual(2, result.stdout.count("report-copy"))
                self.assertIn(f"vouchfx exit code: {code}", result.stdout)
                self.assertEqual("runner-cleanup\n", (Path(scratch) / "cleanup-marker").read_text())

    def test_missing_reports_warn_without_changing_the_exit(self):
        # The engine exits 0 and writes no report when it discovers no scenario, so a
        # missing report must not turn that run, or any other verdict, into a new code.
        for code in (0, 1, 3, 4):
            with self.subTest(code=code), tempfile.TemporaryDirectory(prefix="vouchfx-gitlab-run-") as scratch:
                prefix = r'''set -eu
docker() {
  case "$1" in
    create) printf 'runner-id\n' ;;
    exec) case "$3" in bash) return "$RUN_EXIT" ;; esac ;;
    cp) case "$2" in runner-id:*) return 1 ;; esac ;;
    rm) printf 'runner-cleanup\n' > "$CLEANUP_MARKER" ;;
  esac
}
'''
                env = dict(os.environ, CI_PROJECT_DIR=scratch, VOUCHFX_SCENARIO_PATH="suite",
                           VOUCHFX_DOTNET_IMAGE="sdk:fixture", VOUCHFX_FAIL_ON_ENV_ERROR="false",
                           VOUCHFX_FAIL_ON_INCONCLUSIVE="false", RUN_EXIT=str(code),
                           CLEANUP_MARKER=str(Path(scratch) / "cleanup-marker"))
                result = subprocess.run([BASH, "-c", prefix + self.script], env=env,
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(code, result.returncode, result.stdout + result.stderr)
                self.assertIn("WARNING: no results.xml to retain", result.stderr)
                self.assertIn("WARNING: no report.html to retain", result.stderr)
                self.assertIn(f"vouchfx exit code: {code}", result.stdout)
                self.assertEqual("runner-cleanup\n", (Path(scratch) / "cleanup-marker").read_text())
