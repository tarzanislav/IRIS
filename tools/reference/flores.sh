#!/usr/bin/env bash
# CI only: FLORES-200 devtest (CC BY-SA 4.0) – only measured with, never stored or learned from. Downloads the archive
# once, checks its SHA-256 and extracts the devtest of the given languages to /tmp/flores/<code>.devtest.
# Usage: tools/reference/flores.sh eng_Latn slk_Latn ...
set -euo pipefail
FLORES_URL=https://dl.fbaipublicfiles.com/nllb/flores200_dataset.tar.gz
FLORES_SHA256=b8b0b76783024b85797e5cc75064eb83fc5288b41e9654dabc7be6ae944011f6
mkdir -p /tmp/flores
if [ ! -f /tmp/flores/flores.tar.gz ]; then
  curl -fsSL "$FLORES_URL" -o /tmp/flores/flores.tar.gz
fi
sha=$(sha256sum /tmp/flores/flores.tar.gz | cut -c1-64)
if [ "$sha" != "$FLORES_SHA256" ]; then
  echo "::error title=FLORES-200 changed::SHA-256 $sha, expected $FLORES_SHA256 – the test set is not the one measured so far"
  exit 1
fi
for code in "$@"; do
  [[ "$code" =~ ^[a-z]{3}_[A-Z][a-z]{3}$ ]] || { echo "::error::not a FLORES language code: $code"; exit 1; }
  if [ ! -f "/tmp/flores/$code.devtest" ]; then
    tar -xzf /tmp/flores/flores.tar.gz -C /tmp/flores --wildcards "*/devtest/$code.devtest"
    mv "$(find /tmp/flores -path "*/devtest/$code.devtest" -not -path "/tmp/flores/$code.devtest" | head -1)" "/tmp/flores/$code.devtest"
  fi
done
echo "FLORES-200 $FLORES_URL, SHA-256 $sha" | tee /tmp/flores/info.txt
