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
#      /usr/lib/dotnet.
#      A container in which the .NET 8 version of this hook ran carries Microsoft-feed
#      .NET packages (SDK, runtimes, dotnet-host, hostfxr, targeting packs), the
#      microsoft-prod apt list and its keyring. Left in place they make a mixed-vendor
#      install: Ubuntu's dotnet-host-10.0 conflicts with Microsoft's dotnet-host, so apt
#      removes the muxer and cross-grades hostfxr, stranding the old SDK and runtimes
#      in /usr/share/dotnet with no host. So, whenever it runs as root, the hook first
#      deletes the Microsoft list, keyring and legacy apt preference if they exist. It
#      then purges every installed dotnet*/aspnetcore*/netstandard* package whose
#      version is not an Ubuntu build, but only when that earlier hook's footprint is
#      present: one of those three files existed when this run started, or a non-Ubuntu
#      dotnet-host (the package that conflicts) is installed. Otherwise non-Ubuntu .NET
#      packages, which someone may have installed on purpose, are left alone. The purge
#      is simulated first: if the simulation fails, or prints any action other than a
#      purge of exactly those packages, the hook stops and purges nothing. A fresh
#      container skips that step. Also whenever it runs as root, it (re)writes an apt
#      preference that pins those packages to Ubuntu's <codename>-updates and
#      <codename>-security suites only, replacing any wider pin an earlier version wrote;
#      a missing or malformed VERSION_CODENAME stops the hook before the pin is written.
#      The install runs with --no-remove, so it fails rather than removing any package.
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
# the session sees, so it also reports how many packages a purge removed.
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
# Written by the .NET 8 version of this hook: Microsoft's jammy feed, its signing key, and
# the preference that pinned dotnet packages to it.
LEGACY_MS_LIST=/etc/apt/sources.list.d/microsoft-prod.list
LEGACY_MS_KEYRING=/usr/share/keyrings/microsoft-prod.gpg
LEGACY_MS_PREFS=/etc/apt/preferences.d/dotnet-microsoft
# Set by remove_microsoft_feed to the number of packages it purged, for the summary line.
PURGED_COUNT=0

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

# Installed .NET packages that are not Ubuntu archive builds, one name per line. Every
# Ubuntu build of these source packages carries "ubuntu" in its version
# (10.0.115-0ubuntu1~24.04.1); Microsoft's feed versions do not (8.0.427-1, 9.0.4-1).
foreign_dotnet_packages() {
  dpkg-query -W -f='${Status}\t${Package}\t${Version}\n' 'dotnet*' 'aspnetcore*' 'netstandard*' 2>/dev/null |
    awk -F'\t' '$1 ~ / installed$/ && $3 !~ /ubuntu/ { print $2 }' || true
}

# True when the first argument, without any :arch suffix, names one of the remaining
# arguments, also compared without their :arch suffix. Exact string comparison only.
in_package_set() {
  local want="${1%%:*}" pkg
  shift
  for pkg in "$@"; do
    [ "${pkg%%:*}" = "$want" ] && return 0
  done
  return 1
}

remove_microsoft_feed() {
  local -a foreign refused
  local file sim line verb name legacy_footprint=false
  # The Microsoft list, keyring and preference go first, so no apt operation below can
  # see the Microsoft feed or its priority-1001 pin. Whether any of them was present is
  # recorded before it is deleted: it is the evidence that gates the purge below.
  for file in "$LEGACY_MS_LIST" "$LEGACY_MS_KEYRING" "$LEGACY_MS_PREFS"; do
    if [ -e "$file" ]; then
      legacy_footprint=true
      log "Removing ${file}."
      rm -f "$file"
    fi
  done

  mapfile -t foreign < <(foreign_dotnet_packages)
  if [ "${#foreign[@]}" -eq 0 ]; then
    return 0
  fi
  # Purge only when the earlier hook's footprint is here: its feed files were present
  # at the start of this run, or a non-Ubuntu dotnet-host is installed (the package
  # that conflicts with Ubuntu's dotnet-host-10.0). Without either, these packages came
  # from somewhere this hook did not touch, and they are left in place.
  if [ "$legacy_footprint" != true ] && ! in_package_set dotnet-host "${foreign[@]}"; then
    log "Leaving ${#foreign[@]} non-Ubuntu .NET package(s) in place: no Microsoft feed file was present and no non-Ubuntu dotnet-host is installed."
    return 0
  fi
  # Purge exactly these, and nothing else. The simulation runs first; if it fails the
  # hook stops and purges nothing. An action line is one that starts with Inst, Remv,
  # Purg or Conf followed by a space or the end of the line. Every action line must be
  # a Purg of a package in the set; any other action, or a Purg of any other package,
  # refuses the purge rather than carrying it out. Lines that do not start with one of
  # those four verbs (headings, "Need to get ...") are not actions.
  sim="$(apt-get purge -s "${foreign[@]}")" || {
    log "ERROR: the simulated purge of ${foreign[*]} failed; nothing was purged."
    exit 1
  }
  refused=()
  while IFS= read -r line; do
    [[ "$line" =~ ^(Inst|Remv|Purg|Conf)( |$) ]] || continue
    read -r verb name _ <<<"$line"
    if [ "$verb" != "Purg" ] || [ -z "${name:-}" ] || ! in_package_set "$name" "${foreign[@]}"; then
      refused+=("$line")
    fi
  done <<<"$sim"
  if [ "${#refused[@]}" -gt 0 ]; then
    log "ERROR: purging the Microsoft-feed .NET packages (${foreign[*]}) would also do: ${refused[*]}. Nothing was purged; remove them yourself."
    exit 1
  fi
  log "Purging Microsoft-feed .NET packages: ${foreign[*]}."
  apt-get purge -y -qq "${foreign[@]}" >/dev/null
  PURGED_COUNT="${#foreign[@]}"
}

# The Ubuntu codename from /etc/os-release; stops the hook when there is none, or when
# it is anything but one plain lower-case word (it is written into the apt preference).
# Callers run it in a command substitution, so its exit only ends that subshell: each
# caller must stop on its failure explicitly.
os_codename() {
  local codename
  codename="$(awk -F= '$1 == "VERSION_CODENAME" { gsub(/"/, "", $2); print $2 }' /etc/os-release 2>/dev/null || true)"
  if [ -z "$codename" ]; then
    log "ERROR: /etc/os-release names no VERSION_CODENAME, so the Ubuntu suites to pin are unknown."
    exit 1
  fi
  if ! [[ "$codename" =~ ^[a-z][a-z0-9]*$ ]]; then
    log "ERROR: VERSION_CODENAME in /etc/os-release is not a single lower-case word, so it is not written into the apt preference."
    exit 1
  fi
  printf '%s\n' "$codename"
}

# Pins dotnet*, aspnetcore* and netstandard* to Ubuntu's <codename>-updates and
# <codename>-security suites. Written whenever the hook runs as root, not only before an
# install, so a container where an earlier version of this hook wrote a wider pin gets
# this one. Rewriting it is idempotent.
write_dotnet_pin() {
  local codename
  codename="$(os_codename)" || exit 1

  # Above 1000 a pin also permits downgrades, so it names only the two suites that publish
  # dotnet-sdk-10.0, not every Ubuntu suite (backports and proposed stay at their defaults).
  printf 'Package: dotnet* aspnetcore* netstandard*\nPin: release o=Ubuntu,a=%s\nPin-Priority: 1001\n\n' \
    "${codename}-updates" "${codename}-security" >"$DOTNET_PREFS"
}

# Reached only as root, after the main block below has written the pin, so it does not
# write the pin again.
install_sdk() {
  export DEBIAN_FRONTEND=noninteractive
  local codename
  codename="$(os_codename)" || exit 1

  # dotnet-sdk-10.0 is published in <codename>-updates and -security, so the image's own
  # Ubuntu lists need a refresh; the package archive is reachable through the proxy.
  log "Installing dotnet-sdk-10.0 from Ubuntu's ${codename}-updates/${codename}-security archive."
  apt-get update -qq
  apt-get install -y -qq --no-remove dotnet-sdk-10.0 >/dev/null
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

# Runs whenever root, not only before a fresh install: a container that an earlier version
# of this hook already moved to the Ubuntu SDK may still hold the stranded Microsoft-feed
# packages and the wider pin. On a clean container the purge finds nothing and changes
# nothing, and the pin is rewritten with the same content. install_sdk relies on the pin
# written here.
if is_root; then
  export DEBIAN_FRONTEND=noninteractive
  remove_microsoft_feed
  write_dotnet_pin
fi
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
purge_note=""
if [ "$PURGED_COUNT" -gt 0 ]; then
  purge_note="; purged ${PURGED_COUNT} Microsoft-feed .NET packages"
fi
echo "session-start: .NET SDK $("$DOTNET_DIR/dotnet" --version) ready (global.json floor 10.0.100)${purge_note}."
