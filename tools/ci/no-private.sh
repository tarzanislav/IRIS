#!/usr/bin/env bash
# The wall of the vault: nothing private may leave in a release or be committed. Fails when a file is a vault (it
# starts with IRISVLT1) or a model file says it was learned from private data ("private":"private:…" in its header).
# Usage: tools/ci/no-private.sh <folder or file>…  (no argument: the files of the repository)
set -euo pipefail
if [ "$#" -eq 0 ]; then
  mapfile -t files < <(git ls-files)
else
  mapfile -t files < <(find "$@" -type f)
fi

bad=0
for file in "${files[@]}"; do
  [ -f "$file" ] || continue
  if [ "$(head -c 8 "$file" 2>/dev/null | tr -d '\000')" = "IRISVLT1" ]; then
    echo "::error title=private data::$file is a vault (private data) – it must never be published or committed"
    bad=1
  elif head -c 1048576 "$file" 2>/dev/null | grep -aq '"private":"private:'; then
    echo "::error title=private data::$file was learned from private data – it must never be published or committed"
    bad=1
  fi
done

if [ "$bad" -ne 0 ]; then
  exit 1
fi

echo "no private data among ${#files[@]} files"
