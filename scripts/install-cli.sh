#!/usr/bin/env bash
# Installs or updates lrw, the command line of Linux Remote Worker, on macOS and Linux (Ubuntu and others). When
# Claude Code is installed it also registers lrw as the MCP server "lrw" and adds the linux-remote-worker skill.
# No root, no .NET, no clone:
#
#   curl -fsSL https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.sh | bash
#   wget -qO-  https://raw.githubusercontent.com/JustDimus/linux-remote-worker/main/scripts/install-cli.sh | bash
#
# Options (piped: "| bash -s -- <options>"):
#   --mcp-profile <name>  saved lrw profile the MCP server connects to by default
#   --no-mcp              do not register the MCP server
#   --no-skill            do not add the skill
#   --version <tag>       a release such as v0.1.0 instead of the newest
#   --force               download again even when that version is installed
#   --uninstall           remove lrw, its link, its MCP registration and the skill (saved servers and logs stay)
#
# Run it again to update - or, after installing Claude Code, to connect lrw to it. The program goes to
# ~/.local/share/lrw/lrw, linked as ~/.local/bin/lrw - the same places the skill's lrw-setup.ps1 uses - and the
# download is checked against the release's SHA256SUMS.txt. The skill comes from the same release and goes to
# ~/.claude/skills/linux-remote-worker; a skill put there some other way (a skills pack) is left alone.
# Everything runs inside main, so a download cut short executes nothing.
set -euo pipefail

REPO="JustDimus/linux-remote-worker"
INSTALL_DIR="$HOME/.local/share/lrw"
BIN_DIR="$HOME/.local/bin"
EXE="$INSTALL_DIR/lrw"
LINK="$BIN_DIR/lrw"
CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
SKILL_DIR="$CLAUDE_DIR/skills/linux-remote-worker"
SKILL_MARK=".installed-by-lrw-install-cli"
SKILL_FILES="SKILL.md reference.md scripts/lrw-setup.ps1"
TAG=""
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

latest_tag() {
  local tag
  tag=$(fetch "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p') || true
  [ -n "$tag" ] || fail "could not ask GitHub for the newest release of $REPO"
  echo "$tag"
}

install_lrw() {
  local rid tag current archive expected
  rid=$(platform)
  tag=${VERSION:-$(latest_tag)}
  case "$tag" in v*) ;; *) tag="v$tag" ;; esac
  TAG="$tag"

  if [ -x "$EXE" ] && [ "$FORCE" != 1 ]; then
    current=$("$EXE" version --text </dev/null 2>/dev/null) || current=""
    if [ "v$current" = "$tag" ]; then
      echo "lrw $current is already installed - nothing to download."
      return
    fi
  fi

  WORK=$(mktemp -d)
  archive="lrw-$rid.tar.gz"
  echo "Downloading lrw $tag for $rid..."
  fetch "https://github.com/$REPO/releases/download/$tag/$archive" "$WORK/$archive" ||
    fail "could not download $archive of $tag"
  fetch "https://github.com/$REPO/releases/download/$tag/SHA256SUMS.txt" "$WORK/SHA256SUMS.txt" ||
    fail "could not download SHA256SUMS.txt of $tag"
  expected=$(awk -v f="$archive" '$2 == f || $2 == "*" f { print $1 }' "$WORK/SHA256SUMS.txt")
  [ -n "$expected" ] || fail "$archive is not listed in SHA256SUMS.txt of $tag"
  [ "$(sha256 "$WORK/$archive")" = "$expected" ] || fail "$archive does not match SHA256SUMS.txt - nothing was installed"

  # Unpack next to the program, then rename into place: works even while lrw is running
  mkdir -p "$INSTALL_DIR"
  STAGE=$(mktemp -d "$INSTALL_DIR/.stage.XXXXXX")
  tar -xzf "$WORK/$archive" -C "$STAGE"
  [ -f "$STAGE/lrw" ] || fail "$archive does not contain lrw"
  chmod +x "$STAGE/lrw"
  for file in "$STAGE"/*; do mv -f "$file" "$INSTALL_DIR/"; done
  if [ "$(uname -s)" = Darwin ]; then xattr -d com.apple.quarantine "$EXE" 2>/dev/null || true; fi

  current=$("$EXE" version --text </dev/null) || fail "the installed $EXE does not start"
  echo "lrw $current installed in $INSTALL_DIR."
}

link_lrw() {
  mkdir -p "$BIN_DIR"
  if [ -L "$LINK" ] && [ "$(readlink "$LINK")" = "$EXE" ]; then return; fi
  if [ -e "$LINK" ] || [ -L "$LINK" ]; then
    echo "warning: $LINK already exists and is not this installation's link - left alone (the program is $EXE)." >&2
    return
  fi
  ln -s "$EXE" "$LINK"
}

# Makes new terminals find ~/.local/bin; returns 1 when this terminal does not have it yet
add_to_path() {
  local rc line
  case ":$PATH:" in *":$BIN_DIR:"*) return 0 ;; esac
  # shellcheck disable=SC2016 # the profile expands it
  line='export PATH="$HOME/.local/bin:$PATH"'
  case "$(basename "${SHELL:-}")" in
    zsh) rc="$HOME/.zshrc" ;;
    bash) if [ "$(uname -s)" = Darwin ]; then rc="$HOME/.bash_profile"; else rc="$HOME/.bashrc"; fi ;;
    *)
      echo "Add $BIN_DIR to your PATH to run lrw by name."
      return 1
      ;;
  esac
  if ! grep -qsF "$line" "$rc"; then
    printf '\n# lrw (Linux Remote Worker)\n%s\n' "$line" >>"$rc"
    echo "Added $BIN_DIR to PATH in $rc."
  fi
  return 1
}

register_mcp() {
  local manual="claude mcp add --scope user lrw -- \"$EXE\" mcp${PROFILE:+ --profile $PROFILE}"
  if ! command -v claude >/dev/null 2>&1; then
    if has_claude; then
      echo "The claude command is not on PATH - register the MCP server with: $manual"
    else
      echo "Claude Code is not installed - after installing it, run this script again to connect lrw (MCP server and skill)."
    fi
    return
  fi
  claude mcp remove lrw --scope user </dev/null >/dev/null 2>&1 || true
  if claude mcp add --scope user lrw -- "$EXE" mcp ${PROFILE:+--profile "$PROFILE"} </dev/null >/dev/null; then
    echo "Registered the MCP server 'lrw' in Claude Code (user scope)."
  else
    echo "warning: claude mcp add failed - run it by hand: $manual" >&2
  fi
}

has_claude() { command -v claude >/dev/null 2>&1 || [ -d "$CLAUDE_DIR" ]; }

# The skill's files from the release lrw came from, so its instructions match the program
install_skill() {
  local file new
  if ! has_claude; then return; fi
  if [ -d "$SKILL_DIR" ] && [ ! -f "$SKILL_DIR/$SKILL_MARK" ]; then
    echo "A linux-remote-worker skill is already in $SKILL_DIR (not from this script) - left alone."
    return
  fi
  [ -n "$WORK" ] || WORK=$(mktemp -d)
  new="$WORK/skill"
  for file in $SKILL_FILES; do
    mkdir -p "$new/$(dirname "$file")"
    if ! fetch "https://raw.githubusercontent.com/$REPO/$TAG/.claude/skills/linux-remote-worker/$file" "$new/$file"; then
      echo "warning: could not download the skill's $file of $TAG - skill not installed." >&2
      return
    fi
  done
  if ! grep -q '^name: linux-remote-worker' "$new/SKILL.md"; then
    echo "warning: the downloaded SKILL.md is not the lrw skill - skill not installed." >&2
    return
  fi
  echo "Installed by scripts/install-cli.sh from $TAG; install-cli.sh --uninstall removes it." >"$new/$SKILL_MARK"
  mkdir -p "$(dirname "$SKILL_DIR")"
  rm -rf "$SKILL_DIR"
  mv "$new" "$SKILL_DIR"
  echo "Added the Claude Code skill: $SKILL_DIR."
}

uninstall_lrw() {
  # Only the registration that points at this installation
  if command -v claude >/dev/null 2>&1 && claude mcp get lrw </dev/null 2>/dev/null | grep -qF "$INSTALL_DIR"; then
    claude mcp remove lrw --scope user </dev/null >/dev/null 2>&1 && echo "Removed the MCP server 'lrw' from Claude Code."
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
  echo "lrw is uninstalled. Saved servers and logs are kept."
}

usage() {
  cat <<'EOF'
Installs or updates lrw (Linux Remote Worker's command line); with Claude Code installed, also its MCP
server and skill. Run it again to update, or to connect lrw to a Claude Code installed later.
  install-cli.sh [--mcp-profile <name>] [--no-mcp] [--no-skill] [--version <tag>] [--force]
  install-cli.sh --uninstall
EOF
}

main() {
  VERSION="" PROFILE="" MCP=1 SKILL=1 FORCE=0 ACTION=install
  while [ $# -gt 0 ]; do
    case "$1" in
      --mcp-profile)
        [ $# -ge 2 ] || fail "--mcp-profile needs a profile name"
        PROFILE="$2"; shift 2 ;;
      --no-mcp) MCP=0; shift ;;
      --no-skill) SKILL=0; shift ;;
      --version)
        [ $# -ge 2 ] || fail "--version needs a release tag such as v0.1.0"
        VERSION="$2"; shift 2 ;;
      --force) FORCE=1; shift ;;
      --uninstall) ACTION=uninstall; shift ;;
      -h | --help) usage; return ;;
      *) fail "unknown option $1 (see --help)" ;;
    esac
  done

  if [ "$ACTION" = uninstall ]; then
    uninstall_lrw
    return
  fi

  install_lrw
  link_lrw
  local ready=1
  add_to_path || ready=0
  if [ "$MCP" = 1 ]; then register_mcp; fi
  if [ "$SKILL" = 1 ]; then install_skill; fi

  echo
  if [ "$ready" = 0 ]; then echo "Open a new terminal first (or run: export PATH=\"\$HOME/.local/bin:\$PATH\"), then:"; fi
  echo "  lrw profiles add <name> --host <server ip> --key <private key path> --make-default"
  echo "  lrw connection test"
}

main "$@"
