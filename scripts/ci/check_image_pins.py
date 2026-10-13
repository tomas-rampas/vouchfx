"""Read-only public-registry digest/tag audit. No Docker, writes, credentials or retries."""

import hashlib
import json
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
PINS = Path(__file__).with_name("image-pins.json")
INDEX_TYPES = {"application/vnd.oci.image.index.v1+json",
               "application/vnd.docker.distribution.manifest.list.v2+json"}
MANIFEST_TYPES = {"application/vnd.oci.image.manifest.v1+json",
                  "application/vnd.docker.distribution.manifest.v2+json"}
ACCEPT = ", ".join(sorted(INDEX_TYPES | MANIFEST_TYPES))


def load_pins(path=PINS):
    images = json.loads(Path(path).read_text(encoding="utf-8"))["images"]
    if not images or len({row["name"] for row in images}) != len(images):
        raise ValueError("Pin inventory must be nonempty and have unique names")
    for row in images:
        if row["registry"] not in {"mcr.microsoft.com", "registry-1.docker.io"}:
            raise ValueError("Only the reviewed public registries are allowed")
        if not re.fullmatch(r"[a-z0-9-]+/[a-z0-9-]+", row["repository"]):
            raise ValueError("Invalid public repository")
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", row["tag"]) or "latest" in row["tag"]:
            raise ValueError("Use an explicit upstream release tag")
        if not re.fullmatch(r"sha256:[a-f0-9]{64}", row["digest"]):
            raise ValueError("Invalid SHA-256 pin")
        if row["platformPolicy"] not in {"multiarch", "linux-amd64-only"}:
            raise ValueError("Unknown platform policy")
    return images


def fetch(url, headers, deadline):
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError("120-second acceptance deadline exceeded")
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=min(15, remaining)) as response:
        if time.monotonic() >= deadline:
            raise TimeoutError("120-second acceptance deadline exceeded")
        body = response.read(4 * 1024 * 1024 + 1)
        if time.monotonic() >= deadline:
            raise TimeoutError("120-second acceptance deadline exceeded")
        if len(body) > 4 * 1024 * 1024:
            raise ValueError("Registry metadata exceeds 4 MiB")
        return body, response.headers


def verified_manifest(body, headers):
    digest = "sha256:" + hashlib.sha256(body).hexdigest()
    if headers.get("Docker-Content-Digest") != digest:
        raise ValueError("Registry digest header disagrees with manifest bytes")
    manifest = json.loads(body)
    media = headers.get("Content-Type", "").split(";", 1)[0]
    if media not in INDEX_TYPES | MANIFEST_TYPES or manifest.get("mediaType") != media:
        raise ValueError("Unsupported or inconsistent manifest media type")
    return digest, media, manifest


def check_image(row, deadline):
    headers = {"Accept": ACCEPT}
    if row["registry"] == "registry-1.docker.io":
        query = urllib.parse.urlencode({"service": "registry.docker.io",
                                        "scope": "repository:" + row["repository"] + ":pull"})
        token_body, _ = fetch("https://auth.docker.io/token?" + query, {}, deadline)
        # Anonymous public pull capability is ephemeral and never logged or saved.
        token = json.loads(token_body)["token"]
        headers["Authorization"] = "Bearer " + token
    base = "https://" + row["registry"] + "/v2/" + row["repository"]
    tag_body, tag_headers = fetch(base + "/manifests/" + row["tag"], headers, deadline)
    digest, media, manifest = verified_manifest(tag_body, tag_headers)
    if digest != row["digest"]:
        raise ValueError("Upstream release tag differs from the reviewed digest")
    pin_body, pin_headers = fetch(base + "/manifests/" + row["digest"], headers, deadline)
    if verified_manifest(pin_body, pin_headers)[0] != digest or pin_body != tag_body:
        raise ValueError("Digest reference differs from the release-tag manifest")
    if row["platformPolicy"] == "multiarch":
        if media not in INDEX_TYPES:
            raise ValueError("Expected an upstream multi-arch index")
        platforms = {(m.get("platform", {}).get("os"), m.get("platform", {}).get("architecture"))
                     for m in manifest.get("manifests", [])}
        if not {("linux", "amd64"), ("linux", "arm64")} <= platforms:
            raise ValueError("Index must retain Linux amd64 and arm64")
    else:
        if row["name"] != "asb-sql" or media not in MANIFEST_TYPES:
            raise ValueError("Single-platform exception applies only to the SQL sidecar")
        config_digest = manifest["config"]["digest"]
        if not re.fullmatch(r"sha256:[a-f0-9]{64}", config_digest):
            raise ValueError("Invalid config digest")
        config_body, _ = fetch(base + "/blobs/" + config_digest, headers, deadline)
        if "sha256:" + hashlib.sha256(config_body).hexdigest() != config_digest:
            raise ValueError("Config bytes disagree with manifest digest")
        config = json.loads(config_body)
        if (config.get("os"), config.get("architecture")) != ("linux", "amd64"):
            raise ValueError("SQL upstream single-platform expectation changed")
    return digest


def main():
    deadline = time.monotonic() + 120
    failed = 0
    for row in load_pins():
        try:
            digest = check_image(row, deadline)
            print(row["name"] + ": " + row["tag"] + " -> " + digest + " PASS", flush=True)
        except (KeyError, ValueError, TimeoutError, OSError) as error:
            # Do not print HTTP response bodies, headers or anonymous tokens.
            reason = str(error) if isinstance(error, (ValueError, TimeoutError)) else type(error).__name__
            print(row["name"] + ": FAIL (" + reason + ")", file=sys.stderr, flush=True)
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
