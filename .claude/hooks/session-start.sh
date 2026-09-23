#!/usr/bin/env bash
# .claude/hooks/session-start.sh: SessionStart bootstrap for Claude Code on the web.
#
# Registered in .claude/settings.json. It runs only in a Claude Code on the web
# container ($CLAUDE_CODE_REMOTE=true); a local machine keeps whatever SDK its owner
# installed and is never touched.
#
# What it guarantees, idempotently:
#   1. A .NET 8 SDK that satisfies global.json (8.0.400, rollForward latestFeature).
#      The web sandbox's egress proxy blocks builds.dotnet.microsoft.com (where
#      dotnet-install.sh downloads from), and Ubuntu 24.04's own dotnet-sdk-8.0 is an
#      8.0.1xx build, below global.json's floor. Microsoft's Ubuntu 22.04 (jammy) apt
#      feed carries the 8.0.4xx band and installs cleanly on noble, so that is the
#      source used. An apt preference pins every dotnet package to that feed, so apt
#      never mixes Ubuntu's own host/runtime packages into the Microsoft SDK.
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
# setup script, which no branch controls, and delete this file and its registration
# in .claude/settings.json.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_DIR=/usr/share/dotnet
MS_KEYRING=/usr/share/keyrings/microsoft-prod.gpg
MS_LIST=/etc/apt/sources.list.d/microsoft-prod.list
MS_PREFS=/etc/apt/preferences.d/dotnet-microsoft

log() { printf '[session-start] %s\n' "$*" >&2; }

# The system-wide steps (apt, /usr/bin/dotnet, /etc/profile.d) run only as root, which
# is what a Claude Code on the web container is. This hook NEVER escalates: there is no
# sudo, because a checked-in hook that elevated itself would hand any branch a privileged
# path. Not root and no SDK is a loud refusal; not root with an SDK present skips only
# the system-wide writes.
is_root() { [ "$(id -u)" -eq 0 ]; }

# True when an SDK in the 8.0.4xx band or later is installed. Captured before grep so
# `grep -q` exiting early cannot SIGPIPE `dotnet` into a pipefail.
sdk_ok() {
  local sdks
  sdks="$("$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null || true)"
  grep -Eq '^8\.0\.[4-9][0-9]{2} ' <<<"$sdks"
}

install_sdk() {
  export DEBIAN_FRONTEND=noninteractive
  local arch line
  arch="$(dpkg --print-architecture)"
  line="deb [arch=${arch} signed-by=${MS_KEYRING}] https://packages.microsoft.com/ubuntu/22.04/prod jammy main"

  if [ ! -s "$MS_KEYRING" ]; then
    log "Adding Microsoft's package signing key."
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor --yes -o "$MS_KEYRING"
  fi
  if [ "$(cat "$MS_LIST" 2>/dev/null)" != "$line" ]; then
    printf '%s\n' "$line" >"$MS_LIST"
  fi
  printf 'Package: dotnet* aspnetcore* netstandard*\nPin: origin "packages.microsoft.com"\nPin-Priority: 1001\n' \
    >"$MS_PREFS"

  # Refresh only the Microsoft list (seconds), keeping the image's other lists as they
  # are; fall back to a full refresh if a dependency then cannot be resolved.
  log "Installing dotnet-sdk-8.0 from Microsoft's jammy feed."
  apt-get update -qq -o Dir::Etc::sourcelist=sources.list.d/microsoft-prod.list \
    -o Dir::Etc::sourceparts=- -o APT::Get::List-Cleanup=0
  if ! apt-get install -y -qq dotnet-sdk-8.0 >/dev/null; then
    log "Retrying after a full apt refresh."
    apt-get update -qq
    apt-get install -y -qq dotnet-sdk-8.0 >/dev/null
  fi
}

write_profile() {
  local profile=/etc/profile.d/dotnet.sh want
  want='# Written by .claude/hooks/session-start.sh (Claude Code on the web).
export DOTNET_ROOT=/usr/share/dotnet
case ":$PATH:" in *":/usr/share/dotnet:"*) ;; *) PATH="/usr/share/dotnet:$PATH" ;; esac
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
    log "ERROR: no .NET 8.0.4xx SDK under ${DOTNET_DIR}, and installing one needs root. This hook never escalates; install the SDK yourself."
    exit 1
  fi
  install_sdk
  sdk_ok || { log "ERROR: dotnet-sdk-8.0 installed, but no 8.0.4xx SDK is visible under ${DOTNET_DIR}."; exit 1; }
fi
if is_root; then
  [ "$(readlink -f /usr/bin/dotnet 2>/dev/null)" = "$DOTNET_DIR/dotnet" ] || ln -sf "$DOTNET_DIR/dotnet" /usr/bin/dotnet
else
  log "Not root: /usr/bin/dotnet and /etc/profile.d/dotnet.sh are left as they are; the session environment is still written."
fi
write_profile

export DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
echo "session-start: .NET SDK $("$DOTNET_DIR/dotnet" --version) ready (global.json floor 8.0.400)."
