#!/usr/bin/env bash
# .claude/hooks/session-start.sh: SessionStart bootstrap for Claude Code on the web.
#
# Registered in .claude/settings.json. It runs only in a Claude Code on the web
# container ($CLAUDE_CODE_REMOTE=true); a local machine keeps whatever SDK its owner
# installed and is never touched.
#
# What it guarantees, idempotently:
#   1. A .NET 10 SDK that satisfies global.json (10.0.100, rollForward latestFeature).
#      The web sandbox's egress proxy blocks builds.dotnet.microsoft.com (where
#      dotnet-install.sh downloads from), and Microsoft's Ubuntu 22.04 (jammy) apt feed,
#      which this hook used for .NET 8, publishes no dotnet-sdk-10.0. Ubuntu 24.04's own
#      archive (noble-updates/noble-security) carries dotnet-sdk-10.0 in the 10.0.1xx
#      band, which satisfies global.json, so that is the source used. It installs under
#      /usr/lib/dotnet. An apt preference pins every dotnet package to Ubuntu's archive,
#      so apt never mixes Microsoft's feed packages into the Ubuntu SDK; Ubuntu's
#      dotnet-host-10.0 conflicts with Microsoft's dotnet-host, so a .NET 8 SDK this hook
#      installed from the Microsoft feed in an earlier container is replaced.
#   2. /usr/bin/dotnet, and DOTNET_ROOT/PATH in /etc/profile.d/dotnet.sh and in
#      $CLAUDE_ENV_FILE, so login shells and the session's own tool calls agree.
#
# Deliberately NOT installed here: the published `vouchfx` global tool. This repository
# IS the engine: its tests resolve the CLI they built themselves (BuiltCli.Resolve()),
# never one on PATH, so a published tool would only shadow the source build for anyone
# typing `vouchfx` by hand. The sibling repos (vouchfx-mcp, vouchfx-samples,
# vouchfx-providers) install the CLI at their own pins.
#
# Synchronous by design: the session starts only once the SDK is present, so nothing
# races a half-installed toolchain. On a container that already has the SDK (the web
# environment caches container state after this hook completes) it finishes in well
# under a second. Progress goes to stderr; the single summary line on stdout is what
# the session sees.
#
# Trust model. This file comes from the checked-out branch, and the SessionStart hook
# runs it at session start (as root in a web container) before anyone has read the
# diff. So .claude/ is treated like .github/workflows/: it is code-owned in
# .github/CODEOWNERS, and web sessions should be opened only on branches you trust.
# The alternative is to move this bootstrap into the Claude Code environment's own
# setup script, which no branch controls, and delete this file and its registration:
# remove only the "hooks" block from .claude/settings.json, because that file also
# carries the project permission rules, which are not part of this bootstrap.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_DIR=/usr/lib/dotnet
DOTNET_PREFS=/etc/apt/preferences.d/dotnet-ubuntu
# Written by the .NET 8 version of this hook; it pinned dotnet packages to Microsoft's feed.
LEGACY_MS_PREFS=/etc/apt/preferences.d/dotnet-microsoft

log() { printf '[session-start] %s\n' "$*" >&2; }
REPO_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# The system-wide steps (apt, /usr/bin/dotnet, /etc/profile.d) run only as root, which
# is what a Claude Code on the web container is. This hook NEVER escalates: there is no
# sudo, because a checked-in hook that elevated itself would hand any branch a privileged
# path. Not root and no SDK is a loud refusal; not root with an SDK present skips only
# the system-wide writes.
is_root() { [ "$(id -u)" -eq 0 ]; }

# True when an SDK under $DOTNET_DIR resolves this repository's global.json. The dotnet
# host applies the pinned version and its rollForward policy itself, and exits non-zero
# (145) when no installed SDK satisfies them, so this check cannot drift from global.json
# the way a hard-coded version band could.
sdk_ok() { (cd "$REPO_DIR" && "$DOTNET_DIR/dotnet" --version) >/dev/null 2>&1; }

install_sdk() {
  export DEBIAN_FRONTEND=noninteractive

  rm -f "$LEGACY_MS_PREFS"
  printf 'Package: dotnet* aspnetcore* netstandard*\nPin: release o=Ubuntu\nPin-Priority: 1001\n' \
    >"$DOTNET_PREFS"

  # dotnet-sdk-10.0 is published in noble-updates and noble-security, so the image's own
  # Ubuntu lists need a refresh; the package archive is reachable through the proxy.
  log "Installing dotnet-sdk-10.0 from Ubuntu's noble archive."
  apt-get update -qq
  apt-get install -y -qq dotnet-sdk-10.0 >/dev/null
}

write_profile() {
  local profile=/etc/profile.d/dotnet.sh want
  want='# Written by .claude/hooks/session-start.sh (Claude Code on the web).
export DOTNET_ROOT=/usr/lib/dotnet
case ":$PATH:" in *":/usr/lib/dotnet:"*) ;; *) PATH="/usr/lib/dotnet:$PATH" ;; esac
case ":$PATH:" in *":${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools:"*) ;; *) PATH="$PATH:${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools" ;; esac
export PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1'
  if is_root && [ "$(cat "$profile" 2>/dev/null)" != "$want" ]; then
    printf '%s\n' "$want" >"$profile"
  fi
  # Once per file: a session can fire SessionStart more than once (resume, clear, compact),
  # and the block's first line is the marker that says it is already there.
  if [ -n "${CLAUDE_ENV_FILE:-}" ] && ! grep -qxF "# Written by .claude/hooks/session-start.sh (Claude Code on the web)." "$CLAUDE_ENV_FILE" 2>/dev/null; then
    printf '%s\n' "$want" >>"$CLAUDE_ENV_FILE"
  fi
}

if ! sdk_ok; then
  if ! is_root; then
    log "ERROR: no SDK under ${DOTNET_DIR} satisfies global.json, and installing one needs root. This hook never escalates; install the SDK yourself."
    exit 1
  fi
  install_sdk
  sdk_ok || { log "ERROR: dotnet-sdk-10.0 installed, but no SDK under ${DOTNET_DIR} satisfies global.json."; exit 1; }
fi
if is_root; then
  [ "$(readlink -f /usr/bin/dotnet 2>/dev/null)" = "$DOTNET_DIR/dotnet" ] || ln -sf "$DOTNET_DIR/dotnet" /usr/bin/dotnet
else
  log "Not root: /usr/bin/dotnet and /etc/profile.d/dotnet.sh are left as they are; the session environment is still written."
fi
write_profile

export DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
echo "session-start: .NET SDK $("$DOTNET_DIR/dotnet" --version) ready (global.json floor 10.0.100)."
