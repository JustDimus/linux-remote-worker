#!/usr/bin/env bash
# Installs, updates, checks or removes lrw - the command line of Linux Remote Worker - on macOS and Linux (Ubuntu and
# others), together with its MCP server and skill for Claude Code. Plain bash: no root, no .NET, no clone.
#
#   curl -fsSL https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.sh | bash
#   wget -qO-  https://github.com/JustDimus/linux-remote-worker/releases/latest/download/install-cli.sh | bash
#
# Options (piped: "| bash -s -- <options>"):
#   --status              show what is installed and whether an update exists
#   --uninstall           remove lrw, its link, its MCP registration and the skill (saved servers and logs stay)
#   --version <tag>       a release such as v0.1.0 instead of the newest
#   --mcp-profile <name>  saved lrw profile the MCP server connects to by default
#   --no-mcp              do not register the MCP server
#   --no-skill            do not add the skill
#   --no-path             leave PATH alone
#   --force               download again even when that version is installed
#
# scripts/install-cli.ps1 does the same on Windows:
# - lrw: the release build for this OS and CPU, checked against the release's SHA256SUMS.txt, goes to
#   ~/.local/share/lrw/lrw, linked as ~/.local/bin/lrw (added to PATH in the shell profile when missing). Run it again
#   to update; the program is replaced by a rename, so a running lrw keeps working until it restarts.
# - Claude Code, when installed: lrw is registered as the MCP server "lrw" (this needs the claude command on PATH),
#   and the release's skill package (lrw-skill.tar.gz, checked the same way) goes to
#   ~/.claude/skills/linux-remote-worker (CLAUDE_CONFIG_DIR is honoured). A skill put there some other way is left
#   alone.
# - --uninstall removes all of that; saved servers and logs stay.
# The newest version comes from releases/latest/download/version.txt, GitHub's permanent link to a file of the
# newest release. Everything runs inside main, so a download cut short executes nothing.
set -euo pipefail

REPO="JustDimus/linux-remote-worker"
INSTALL_DIR="$HOME/.local/share/lrw"
EXE="$INSTALL_DIR/lrw"
BIN_DIR="$HOME/.local/bin"
LINK="$BIN_DIR/lrw"
CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
SKILL_DIR="$CLAUDE_DIR/skills/linux-remote-worker"
SKILL_MARK=".installed-by-lrw-install-cli"
WORK=""
STAGE=""

cleanup() { rm -rf "$WORK" "$STAGE"; }
trap cleanup EXIT

fail() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

# fetch <url> [file] - curl, or wget on systems without it (a fresh Ubuntu desktop)
fetch() {
  if command -v curl >/dev/null 2>&1; then
    if [ -n "${2:-}" ]; then curl -fsSL --retry 3 -o "$2" "$1"; else curl -fsSL --retry 3 "$1"; fi
  elif command -v wget >/dev/null 2>&1; then
    if [ -n "${2:-}" ]; then wget -q -O "$2" "$1"; else wget -q -O - "$1"; fi
  else
    fail "curl or wget is needed (Ubuntu: sudo apt install curl)"
  fi
}

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

has_claude() { command -v claude >/dev/null 2>&1 || [ -d "$CLAUDE_DIR" ]; }

installed_version() { "$EXE" version --text </dev/null 2>/dev/null || true; }

# version_newer <a> <b> - whether version a (x.y.z) is newer than b
version_newer() {
  local IFS=. a b i
  read -r -a a <<<"$1"
  read -r -a b <<<"$2"
  for i in 0 1 2; do
    if [ "${a[i]:-0}" -gt "${b[i]:-0}" ]; then return 0; fi
    if [ "${a[i]:-0}" -lt "${b[i]:-0}" ]; then return 1; fi
  done
  return 1
}

# The newest release's tag, empty when GitHub cannot be asked
latest_tag() {
  local version
  version=$(fetch "https://github.com/$REPO/releases/latest/download/version.txt" 2>/dev/null | tr -d '[:space:]') || true
  case "$version" in [0-9]*.[0-9]*.[0-9]*) echo "v$version" ;; esac
}

# download_release <file> - a file of release $TAG into $WORK, checked against the release's SHA256SUMS.txt.
# Returns 1 when it cannot be downloaded, 2 when it does not match.
download_release() {
  local file="$1" expected
  [ -n "$WORK" ] || WORK=$(mktemp -d)
  if [ ! -s "$WORK/SHA256SUMS.txt" ]; then
    fetch "https://github.com/$REPO/releases/download/$TAG/SHA256SUMS.txt" "$WORK/SHA256SUMS.txt" 2>/dev/null || return 1
  fi
  fetch "https://github.com/$REPO/releases/download/$TAG/$file" "$WORK/$file" 2>/dev/null || return 1
  expected=$(awk -v f="$file" '$2 == f || $2 == "*" f { print $1 }' "$WORK/SHA256SUMS.txt")
  { [ -n "$expected" ] && [ "$(sha256 "$WORK/$file")" = "$expected" ]; } || return 2
}

mcp_points_here() { claude mcp get lrw </dev/null 2>/dev/null | grep -qF "$INSTALL_DIR"; }

platform() {
  local os arch
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    *) fail "this script is for macOS and Linux - on Windows run scripts/install-cli.ps1" ;;
  esac
  case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    arm64 | aarch64) arch=arm64 ;;
    *) fail "unsupported CPU $(uname -m): lrw is built for x64 and arm64" ;;
  esac
  # A terminal running under Rosetta reports x86_64 on Apple Silicon - the native build is the right one
  if [ "$os" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ]; then
    arch=arm64
  fi
  echo "$os-$arch"
}

show_status() {
  local current latest verdict mcp skill path
  current=$(installed_version)
  if [ -n "$current" ]; then echo "lrw:    $current ($EXE)"; else echo "lrw:    not installed"; fi
  latest=$(latest_tag)
  if [ -z "$latest" ]; then
    echo "newest: unknown (GitHub could not be asked)"
  else
    if [ -z "$current" ]; then verdict="run this script to install it"
    elif [ "v$current" = "$latest" ]; then verdict="up to date"
    elif version_newer "$current" "${latest#v}"; then verdict="newer than the newest release"
    else verdict="update available - run this script again"; fi
    echo "newest: $latest ($verdict)"
  fi
  if [ -L "$LINK" ] && [ "$(readlink "$LINK")" = "$EXE" ]; then
    case ":$PATH:" in
      *":$BIN_DIR:"*) path="yes ($LINK)" ;;
      *) path="linked as $LINK, but $BIN_DIR is not on PATH in this terminal" ;;
    esac
  else
    path="no"
  fi
  echo "PATH:   $path"
  if command -v claude >/dev/null 2>&1; then
    if mcp_points_here; then mcp="registered"; else mcp="not registered"; fi
  elif [ -d "$CLAUDE_DIR" ]; then mcp="unknown (the claude command is not on PATH)"
  else mcp="Claude Code is not installed"; fi
  echo "MCP:    $mcp"
  if [ -f "$SKILL_DIR/$SKILL_MARK" ]; then skill="installed ($SKILL_DIR)"
  elif [ -d "$SKILL_DIR" ]; then skill="another copy, left alone ($SKILL_DIR)"
  else skill="not installed"; fi
  echo "skill:  $skill"
}

# Sets TAG to the release that is installed now
install_lrw() {
  local rid current archive
  rid=$(platform)
  TAG=${VERSION:-$(latest_tag)}
  [ -n "$TAG" ] || fail "could not ask GitHub for the newest release of $REPO"
  case "$TAG" in v*) ;; *) TAG="v$TAG" ;; esac

  current=$(installed_version)
  if [ -n "$current" ] && [ "$FORCE" != 1 ]; then
    if [ "v$current" = "$TAG" ]; then
      echo "lrw $current is already installed - nothing to download."
      return
    fi
    # Without --version, a copy newer than the newest release (a pre-release) is not taken back
    if [ -z "$VERSION" ] && version_newer "$current" "${TAG#v}"; then
      echo "lrw $current is already installed, newer than the newest release $TAG - nothing to download."
      TAG="v$current"
      return
    fi
  fi

  archive="lrw-$rid.tar.gz"
  echo "Downloading lrw $TAG for $rid..."
  download_release "$archive" || {
    [ $? = 2 ] && fail "$archive does not match SHA256SUMS.txt of $TAG - nothing was installed"
    fail "could not download $archive of $TAG"
  }

  # Unpacked next to the program and renamed into place: works even while lrw is running
  mkdir -p "$INSTALL_DIR"
  STAGE=$(mktemp -d "$INSTALL_DIR/.stage.XXXXXX")
  tar -xzf "$WORK/$archive" -C "$STAGE"
  [ -f "$STAGE/lrw" ] || fail "$archive does not contain lrw"
  chmod +x "$STAGE/lrw"
  for file in "$STAGE"/*; do mv -f "$file" "$INSTALL_DIR/"; done
  if [ "$(uname -s)" = Darwin ]; then xattr -d com.apple.quarantine "$EXE" 2>/dev/null || true; fi

  current=$(installed_version)
  [ -n "$current" ] || fail "the installed $EXE does not start"
  echo "lrw $current installed in $INSTALL_DIR."
}

# Links ~/.local/bin/lrw and makes new terminals find ~/.local/bin; READY=0 when this terminal does not have it yet
add_to_path() {
  local rc line
  mkdir -p "$BIN_DIR"
  if ! { [ -L "$LINK" ] && [ "$(readlink "$LINK")" = "$EXE" ]; }; then
    if [ -e "$LINK" ] || [ -L "$LINK" ]; then
      echo "warning: $LINK already exists and is not this installation's link - left alone (the program is $EXE)." >&2
    else
      ln -s "$EXE" "$LINK"
    fi
  fi
  case ":$PATH:" in *":$BIN_DIR:"*) return ;; esac
  READY=0
  # shellcheck disable=SC2016 # the profile expands it
  line='export PATH="$HOME/.local/bin:$PATH"'
  case "$(basename "${SHELL:-}")" in
    zsh) rc="$HOME/.zshrc" ;;
    bash) if [ "$(uname -s)" = Darwin ]; then rc="$HOME/.bash_profile"; else rc="$HOME/.bashrc"; fi ;;
    *)
      echo "Add $BIN_DIR to your PATH to run lrw by name."
      return
      ;;
  esac
  if ! grep -qsF "$line" "$rc"; then
    printf '\n# lrw (Linux Remote Worker)\n%s\n' "$line" >>"$rc"
    echo "Added $BIN_DIR to PATH in $rc."
  fi
}

register_mcp() {
  local manual="claude mcp add --scope user lrw -- \"$EXE\" mcp${PROFILE:+ --profile $PROFILE}"
  if ! command -v claude >/dev/null 2>&1; then
    echo "The claude command is not on PATH - register the MCP server with: $manual"
    return
  fi
  # The program path never changes, so a registration made earlier (maybe with a profile) stays as it is
  if [ -z "$PROFILE" ] && mcp_points_here; then
    echo "The MCP server 'lrw' is already registered in Claude Code."
    return
  fi
  claude mcp remove lrw --scope user </dev/null >/dev/null 2>&1 || true
  if claude mcp add --scope user lrw -- "$EXE" mcp ${PROFILE:+--profile "$PROFILE"} </dev/null >/dev/null; then
    echo "Registered the MCP server 'lrw' in Claude Code (user scope)."
  else
    echo "warning: claude mcp add failed - run it by hand: $manual" >&2
  fi
}

# The skill package of the release lrw came from, so its instructions match the program
install_skill() {
  local new rc=0
  if [ -d "$SKILL_DIR" ] && [ ! -f "$SKILL_DIR/$SKILL_MARK" ]; then
    echo "A linux-remote-worker skill is already in $SKILL_DIR (not from this script) - left alone."
    return
  fi
  download_release lrw-skill.tar.gz || rc=$?
  if [ "$rc" = 1 ]; then
    echo "warning: could not download lrw-skill.tar.gz of $TAG (older releases have none) - the skill was not added." >&2
    return
  elif [ "$rc" = 2 ]; then
    echo "warning: lrw-skill.tar.gz does not match SHA256SUMS.txt of $TAG - the skill was not added." >&2
    return
  fi
  new="$WORK/skill"
  rm -rf "$new"
  mkdir -p "$new"
  tar -xzf "$WORK/lrw-skill.tar.gz" -C "$new"
  if ! grep -qs '^name: linux-remote-worker' "$new/SKILL.md"; then
    echo "warning: the downloaded SKILL.md is not the lrw skill - the skill was not added." >&2
    return
  fi
  echo "Installed by scripts/install-cli.sh from $TAG; install-cli.sh --uninstall removes it." >"$new/$SKILL_MARK"
  mkdir -p "$(dirname "$SKILL_DIR")"
  rm -rf "$SKILL_DIR"
  mv "$new" "$SKILL_DIR"
  echo "Added the Claude Code skill: $SKILL_DIR."
}

uninstall_lrw() {
  if command -v claude >/dev/null 2>&1; then
    if mcp_points_here; then
      claude mcp remove lrw --scope user </dev/null >/dev/null 2>&1 && echo "Removed the MCP server 'lrw' from Claude Code."
    fi
  elif [ -d "$CLAUDE_DIR" ]; then
    echo "The claude command is not on PATH - if Claude Code still lists the MCP server lrw, remove it with: claude mcp remove lrw --scope user"
  fi
  if [ -f "$SKILL_DIR/$SKILL_MARK" ]; then
    rm -rf "$SKILL_DIR"
    echo "Removed the Claude Code skill $SKILL_DIR."
  fi
  if [ -L "$LINK" ] && [ "$(readlink "$LINK")" = "$EXE" ]; then
    rm -f "$LINK"
    echo "Removed $LINK."
  fi
  if [ -d "$INSTALL_DIR" ]; then
    rm -rf "$INSTALL_DIR"
    echo "Removed $INSTALL_DIR."
  fi
  # State of the setup script earlier versions used
  rm -rf "${XDG_STATE_HOME:-$HOME/.local/state}/lrw" "${XDG_CACHE_HOME:-$HOME/.cache}/lrw"
  echo "lrw is uninstalled. Saved servers and logs are kept."
}

usage() {
  cat <<'EOF'
Installs, updates, checks or removes lrw (Linux Remote Worker's command line) with its MCP server and skill
for Claude Code. Run it again to update, or to connect lrw to a Claude Code installed later.
  install-cli.sh [--version <tag>] [--mcp-profile <name>] [--no-mcp] [--no-skill] [--no-path] [--force]
  install-cli.sh --status
  install-cli.sh --uninstall
EOF
}

main() {
  ACTION=install VERSION="" PROFILE="" MCP=1 SKILL=1 LINKS=1 FORCE=0 TAG="" READY=1
  while [ $# -gt 0 ]; do
    case "$1" in
      --status) ACTION=status; shift ;;
      --uninstall) ACTION=uninstall; shift ;;
      --version)
        [ $# -ge 2 ] || fail "--version needs a release tag such as v0.1.0"
        VERSION="$2"; shift 2 ;;
      --mcp-profile)
        [ $# -ge 2 ] || fail "--mcp-profile needs a profile name"
        PROFILE="$2"; shift 2 ;;
      --no-mcp) MCP=0; shift ;;
      --no-skill) SKILL=0; shift ;;
      --no-path) LINKS=0; shift ;;
      --force) FORCE=1; shift ;;
      -h | --help) usage; return ;;
      *) fail "unknown option $1 (see --help)" ;;
    esac
  done

  case "$ACTION" in
    status) show_status; return ;;
    uninstall) uninstall_lrw; return ;;
  esac

  install_lrw
  if [ "$LINKS" = 1 ]; then add_to_path; fi
  if has_claude; then
    if [ "$MCP" = 1 ]; then register_mcp; fi
    if [ "$SKILL" = 1 ]; then install_skill; fi
  elif [ "$MCP" = 1 ] || [ "$SKILL" = 1 ]; then
    echo "Claude Code is not installed - after installing it, run this again to connect lrw (MCP server and skill)."
  fi

  echo
  echo "Next:"
  if [ "$READY" = 0 ]; then echo "  open a new terminal (or run: export PATH=\"\$HOME/.local/bin:\$PATH\")"; fi
  echo "  lrw profiles add <name> --host <server ip> --key <private key path> --make-default"
  echo "  lrw connection test"
}

main "$@"
