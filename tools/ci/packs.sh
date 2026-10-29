#!/usr/bin/env bash
# Release: the catalog of language packs (packs.json) for the model files in a folder, read by the application of the
# same version. Usage: tools/ci/packs.sh <folder> <pair>...   (file of a pair: opus-mt-<pair>-int8.iris; the revision
# of each pair comes from OPUS_MT_<PAIR>_REVISION, set by tools/reference/prepare-opus.sh)
set -euo pipefail
folder=$1
shift
{
  printf '{\n  "format": 1,\n  "packs": [\n'
  separator=""
  for pair in "$@"; do
    file="opus-mt-$pair-int8.iris"
    path="$folder/$file"
    [ -f "$path" ] || { echo "::error::$file is missing" >&2; exit 1; }
    variable=$(echo "OPUS_MT_${pair}_REVISION" | tr 'a-z-' 'A-Z_')
    revision=${!variable:-}
    [ -n "$revision" ] || { echo "::error::$variable is not set" >&2; exit 1; }
    size=$(stat -c %s "$path")
    sha=$(sha256sum "$path" | cut -d' ' -f1)
    printf '%s    {"pair": "%s", "file": "%s", "size": %s, "sha256": "%s", "license": "Apache-2.0", "source": "Helsinki-NLP/opus-mt-%s@%s"}' \
      "$separator" "$pair" "$file" "$size" "$sha" "$pair" "$revision"
    separator=$',\n'
  done
  printf '\n  ]\n}\n'
}
