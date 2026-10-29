#!/usr/bin/env bash
# CI only: one language pair of OPUS-MT checked the way a language pack is – licence (on download), the own core
# against the reference on 200 invented sentences (greedy ≥ 99 %, beam ≥ 95 %; sentences in another source language
# are the invented English ones translated by the English model of that language), then an int8 copy and its quality
# on FLORES-200 devtest (int8 at most 0.5 chrF below float32). Usage: tools/reference/pair.sh <pair>, e.g. sk-en
set -euo pipefail
pair=$1
src=${pair%-*}
tgt=${pair#*-}
here=$(cd "$(dirname "$0")" && pwd)
py=/tmp/venv/bin/python
iris="dotnet src/Iris.Cli/bin/Release/net10.0/iris.dll"
declare -A FLORES=([en]=eng_Latn [sk]=slk_Latn [cs]=ces_Latn)
[ -n "${FLORES[$src]:-}" ] && [ -n "${FLORES[$tgt]:-}" ] || { echo "::error::no FLORES code for $pair"; exit 1; }

bash "$here/prepare-opus.sh" /tmp "$pair"
via=()
if [ "$src" != en ]; then
  bash "$here/prepare-opus.sh" /tmp "en-$src"
  via=(--via "/tmp/opus-en-$src")
fi
"$py" "$here/marian_reference.py" reference "/tmp/opus-$pair" "/tmp/opus-$pair.json" --count 200 "${via[@]}"
bash "$here/verify.sh" "/tmp/opus-$pair.iris" "/tmp/opus-$pair.json" --min-greedy 0.99 --min-beam 0.95

$iris quantize "/tmp/opus-$pair.iris" "/tmp/opus-$pair-int8.iris"
bash "$here/flores.sh" "${FLORES[$src]}" "${FLORES[$tgt]}"
source_text=/tmp/flores/${FLORES[$src]}.devtest
reference=/tmp/flores/${FLORES[$tgt]}.devtest
f32=$($iris score "/tmp/opus-$pair.iris" "$source_text" "$reference" | grep -oP '^chrF2 \K[0-9.]+')
q8=$($iris score "/tmp/opus-$pair-int8.iris" "$source_text" "$reference" | grep -oP '^chrF2 \K[0-9.]+')
size=$(du -m "/tmp/opus-$pair-int8.iris" | cut -f1)
license=$(grep -oP '"license": "\K[^"]+' "/tmp/opus-$pair/iris-source.json")
revision=$(grep -oP '"revision": "\K[^"]+' "/tmp/opus-$pair/iris-source.json")
if awk -v a="$q8" -v b="$f32" 'BEGIN { exit !(a >= b - 0.5) }'; then
  echo "::notice title=$pair PASSED::FLORES-200 devtest chrF2 float32 $f32, int8 $q8; int8 $size MB; licence $license; revision $revision"
else
  echo "::error title=$pair FAILED::int8 chrF2 $q8 is more than 0.5 below float32 $f32"
  exit 1
fi
{
  echo "### $pair"
  echo
  echo "FLORES-200 devtest chrF2: **float32 $f32**, **int8 $q8**; int8 $size MB; licence $license; revision \`$revision\`"
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
