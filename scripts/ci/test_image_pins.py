"""Inert regression coverage for actual image pins and the registry audit."""

import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock

import check_image_pins as audit
from docker_ci_support import ROOT, extract_pull, image_boundary


WHOAMI_FIXTURES = [
    "DcpFlightRecorderDockerTests.cs", "HeadlessTopologyTests.cs",
    "HttpRestBodyAssertionDockerTests.cs", "HttpSoapDockerTests.cs",
    "PinnedHostPortDockerTests.cs", "TopologyTeardownLeakTests.cs",
    "TraceExpectOtlpDockerTests.cs",
]


def encoded(value):
    return json.dumps(value, separators=(",", ":")).encode()


def manifest_reply(value):
    body = encoded(value)
    digest = "sha256:" + hashlib.sha256(body).hexdigest()
    return body, {"Docker-Content-Digest": digest, "Content-Type": value["mediaType"]}


class PinSourceContractTests(unittest.TestCase):
    def test_inventory_matches_actual_sidecar_fixtures_and_ci_pull_list(self):
        pins = {row["name"]: row for row in audit.load_pins()}
        self.assertEqual({"asb-sql", "vault", "whoami", "rustfs"}, set(pins))
        pull_list = image_boundary(extract_pull()).group().decode()
        for row in pins.values():
            registry = "mcr.microsoft.com/" if row["registry"] == "mcr.microsoft.com" else ""
            self.assertIn(registry + row["repository"] + "@" + row["digest"], pull_list)
        self.assertNotIn("mssql/server:2022-latest", pull_list)
        self.assertNotIn("hashicorp/vault:latest", pull_list)
        mapper = (ROOT / "src/Engine/Vouchfx.Engine.Orchestration/EnvironmentMapper.cs").read_text(encoding="utf-8")
        self.assertIn(pins["asb-sql"]["digest"].split(":")[1], mapper)
        self.assertIn(".WithImageSHA256(AzureServiceBusSqlImageDigest)", mapper)
        self.assertIn(pins["rustfs"]["digest"].split(":")[1], mapper)
        fixtures = ROOT / "tests/Vouchfx.Engine.Orchestration.Tests"
        for name in WHOAMI_FIXTURES:
            source = (fixtures / name).read_text(encoding="utf-8")
            with self.subTest(fixture=name):
                self.assertIn("traefik/whoami@" + pins["whoami"]["digest"], source)
                self.assertNotIn('"traefik/whoami"', source)
                self.assertNotIn("traefik/whoami:latest", source)
        vault = (fixtures / "VaultSecretSourceDockerTests.cs").read_text(encoding="utf-8")
        self.assertIn('"hashicorp/vault@' + pins["vault"]["digest"] + '"', vault)
        self.assertNotIn('"hashicorp/vault"', vault)

    def test_freshness_is_read_only_bounded_and_scheduled(self):
        workflow = (ROOT / ".github/workflows/image-pin-freshness.yml").read_text(encoding="utf-8")
        self.assertIn("schedule:", workflow)
        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("timeout-minutes: 5", workflow)
        self.assertIn("contents: read", workflow)
        self.assertIn("python -B scripts/ci/check_image_pins.py", workflow)
        self.assertNotIn("contents: write", workflow)
        self.assertNotIn("issues:", workflow)
        self.assertNotIn("continue-on-error", workflow)


class RegistryAuditTests(unittest.TestCase):
    def setUp(self):
        self.row = copy.deepcopy(audit.load_pins()[2])
        self.index = {"mediaType": "application/vnd.oci.image.index.v1+json", "schemaVersion": 2,
                      "manifests": [{"platform": {"os": "linux", "architecture": architecture}}
                                    for architecture in ["amd64", "arm64"]]}
        self.reply = manifest_reply(self.index)
        self.row["digest"] = self.reply[1]["Docker-Content-Digest"]
        self.token = (encoded({"token": "INERT-ANONYMOUS-CAPABILITY"}), {})

    def check(self, replies):
        with mock.patch.object(audit, "fetch", side_effect=replies) as fetch:
            result = audit.check_image(self.row, time.monotonic() + 120)
            self.assertEqual(3, fetch.call_count)
            self.assertTrue(all(call.args[0].startswith("https://") for call in fetch.call_args_list))
            return result

    def test_matching_multiarch_tag_and_digest_pass(self):
        self.assertEqual(self.row["digest"], self.check([self.token, self.reply, self.reply]))

    def test_repointed_tag_fails_before_digest_fetch(self):
        self.row["digest"] = "sha256:" + "0" * 64
        with self.assertRaisesRegex(ValueError, "release tag differs"),                 mock.patch.object(audit, "fetch", side_effect=[self.token, self.reply]) as fetch:
            audit.check_image(self.row, time.monotonic() + 120)
        self.assertEqual(2, fetch.call_count)

    def test_digest_header_cannot_hide_changed_bytes(self):
        body, headers = self.reply
        with self.assertRaisesRegex(ValueError, "manifest bytes"):
            audit.verified_manifest(body + b" ", headers)

    def test_missing_arm64_or_non_index_fails(self):
        for kind in ["missing-arm64", "single-manifest"]:
            value = copy.deepcopy(self.index)
            if kind == "missing-arm64":
                value["manifests"] = value["manifests"][:1]
            else:
                value["mediaType"] = "application/vnd.oci.image.manifest.v1+json"
            reply = manifest_reply(value)
            self.row["digest"] = reply[1]["Docker-Content-Digest"]
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                self.check([self.token, reply, reply])

    def test_sql_exception_verifies_actual_config_architecture_and_digest(self):
        self.row = copy.deepcopy(audit.load_pins()[0])
        config = encoded({"os": "linux", "architecture": "amd64"})
        value = {"schemaVersion": 2, "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
                 "config": {"digest": "sha256:" + hashlib.sha256(config).hexdigest()}}
        reply = manifest_reply(value)
        self.row["digest"] = reply[1]["Docker-Content-Digest"]
        self.assertEqual(self.row["digest"], self.check([reply, reply, (config, {})]))
        with self.assertRaisesRegex(ValueError, "Config bytes"),                 mock.patch.object(audit, "fetch", side_effect=[reply, reply, (config + b" ", {})]):
            audit.check_image(self.row, time.monotonic() + 120)
        bad_config = encoded({"os": "linux", "architecture": "arm64"})
        value["config"]["digest"] = "sha256:" + hashlib.sha256(bad_config).hexdigest()
        bad_reply = manifest_reply(value)
        self.row["digest"] = bad_reply[1]["Docker-Content-Digest"]
        with self.assertRaisesRegex(ValueError, "single-platform expectation"),                 mock.patch.object(audit, "fetch", side_effect=[bad_reply, bad_reply, (bad_config, {})]):
            audit.check_image(self.row, time.monotonic() + 120)

    def test_exhausted_deadline_refuses_before_http(self):
        with mock.patch.object(audit.urllib.request, "urlopen") as request,                 self.assertRaises(TimeoutError):
            audit.fetch("https://registry-1.docker.io/v2/traefik/whoami/manifests/v1.12.0", {}, time.monotonic() - 1)
        request.assert_not_called()

    def test_late_open_refuses_before_reading_body(self):
        response = mock.MagicMock()
        response.__enter__.return_value = response
        with mock.patch.object(audit.time, "monotonic", side_effect=[0, 121]), \
                mock.patch.object(audit.urllib.request, "urlopen", return_value=response) as request, \
                self.assertRaisesRegex(TimeoutError, "acceptance deadline"):
            audit.fetch("https://mcr.microsoft.com/v2/mssql/server/manifests/release", {}, 120)
        self.assertEqual(15, request.call_args.kwargs["timeout"])
        response.read.assert_not_called()

    def test_body_at_or_after_deadline_is_refused(self):
        for completed_at in [120, 121]:
            response = mock.MagicMock()
            response.__enter__.return_value = response
            response.read.return_value = b"inert metadata"
            with self.subTest(completed_at=completed_at), \
                    mock.patch.object(audit.time, "monotonic", side_effect=[0, 1, completed_at]), \
                    mock.patch.object(audit.urllib.request, "urlopen", return_value=response), \
                    self.assertRaisesRegex(TimeoutError, "acceptance deadline"):
                audit.fetch("https://mcr.microsoft.com/v2/mssql/server/manifests/release", {}, 120)
            response.read.assert_called_once_with(4 * 1024 * 1024 + 1)

    def test_body_before_deadline_keeps_bounded_read_and_headers(self):
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.read.return_value = b"inert metadata"
        response.headers = {"Content-Type": "application/json"}
        with mock.patch.object(audit.time, "monotonic", side_effect=[0, 1, 119]), \
                mock.patch.object(audit.urllib.request, "urlopen", return_value=response):
            self.assertEqual((b"inert metadata", response.headers), audit.fetch(
                "https://mcr.microsoft.com/v2/mssql/server/manifests/release", {}, 120))
        response.read.assert_called_once_with(4 * 1024 * 1024 + 1)

    def test_network_failure_is_nonzero_without_exposing_capability(self):
        stderr = io.StringIO()
        with mock.patch.object(audit, "load_pins", return_value=[self.row]),                 mock.patch.object(audit, "check_image", side_effect=OSError("INERT-ANONYMOUS-CAPABILITY")),                 mock.patch.object(audit.sys, "stderr", stderr):
            self.assertEqual(1, audit.main())
        self.assertIn("FAIL (OSError)", stderr.getvalue())
        self.assertNotIn("INERT-ANONYMOUS-CAPABILITY", stderr.getvalue())

    def test_invalid_empty_duplicate_or_floating_inventory_fails(self):
        for variant in [[], [self.row, self.row], [{**self.row, "tag": "latest"}],
                        [{**self.row, "digest": "sha256:bad"}], [{**self.row, "registry": "private.example"}]]:
            with self.subTest(variant=variant), tempfile.TemporaryDirectory(prefix="vouchfx-pin-contract-") as folder:
                path = Path(folder) / "pins.json"
                path.write_text(json.dumps({"images": variant}), encoding="utf-8")
                with self.assertRaises(ValueError):
                    audit.load_pins(path)


if __name__ == "__main__":
    unittest.main(verbosity=2)
