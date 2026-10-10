#!/usr/bin/env bash
# Official release assets, pinned by version AND SHA-256. No global installation.
set -euo pipefail
tools="$RUNNER_TEMP/vouchfx-workflow-tools"
mkdir -p "$tools"
# actionlint v1.7.12 source tag: 914e7df21a07ef503a81201c76d2b11c789d3fca.
curl -fsSLo "$tools/actionlint.tar.gz" https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz
curl -fsSLo "$tools/shellcheck.tar.xz" https://github.com/koalaman/shellcheck/releases/download/v0.11.0/shellcheck-v0.11.0.linux.x86_64.tar.xz
curl -fsSLo "$tools/yq" https://github.com/mikefarah/yq/releases/download/v4.53.2/yq_linux_amd64
printf '%s\n' \
  "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  $tools/actionlint.tar.gz" \
  "8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198  $tools/shellcheck.tar.xz" \
  "d56bf5c6819e8e696340c312bd70f849dc1678a7cda9c2ad63eebd906371d56b  $tools/yq" | sha256sum --check --strict
tar -xzf "$tools/actionlint.tar.gz" -C "$tools" actionlint
tar -xJf "$tools/shellcheck.tar.xz" -C "$tools" --strip-components=1 shellcheck-v0.11.0/shellcheck
chmod +x "$tools/actionlint" "$tools/shellcheck" "$tools/yq"
printf '%s\n' "$tools" >> "$GITHUB_PATH"
"$tools/actionlint" -version
"$tools/shellcheck" --version
"$tools/yq" --version
