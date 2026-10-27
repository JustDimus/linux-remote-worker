#!/usr/bin/env bash
# Signs a desktop-app release for the app's updater and prints the manifest (LinuxRemoteWorker-update.sig.json):
#   tools/sign-update.sh <private key PEM> <package id> <version> <full .nupkg>
# The signed text must match UpdateManifest.SignedText in LinuxRemoteWorker.Core byte for byte.
set -euo pipefail

key="$1"
id="$2"
version="$3"
package="$4"

file=$(basename "$package")
sha=$(sha256sum "$package" | cut -d' ' -f1 | tr 'a-f' 'A-F')

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
printf 'lrw-update-v1\n%s\n%s\n%s\n%s\n' "$id" "$version" "$file" "$sha" > "$work/payload"
openssl dgst -sha256 -sign "$key" -out "$work/signature" "$work/payload"

printf '{\n  "packageId": "%s",\n  "version": "%s",\n  "file": "%s",\n  "sha256": "%s",\n  "signature": "%s"\n}\n' \
  "$id" "$version" "$file" "$sha" "$(base64 -w0 < "$work/signature")"
