#!/usr/bin/env bash
# Prints the version after <current> for a patch, minor or major release:
#   tools/next-version.sh 0.0.3 patch   ->  0.0.4
#   tools/next-version.sh 0.0.3 minor   ->  0.1.0
#   tools/next-version.sh 0.4.2 major   ->  1.0.0
set -euo pipefail

current="${1:-}"
bump="${2:-}"

if [[ ! "$current" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
  echo "Not a version (major.minor.patch): '$current'" >&2
  exit 2
fi
major=${BASH_REMATCH[1]}
minor=${BASH_REMATCH[2]}
patch=${BASH_REMATCH[3]}

case "$bump" in
  patch) patch=$((patch + 1)) ;;
  minor) minor=$((minor + 1)); patch=0 ;;
  major) major=$((major + 1)); minor=0; patch=0 ;;
  *) echo "The bump must be patch, minor or major: '$bump'" >&2; exit 2 ;;
esac

echo "$major.$minor.$patch"
