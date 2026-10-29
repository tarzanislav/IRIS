#!/usr/bin/env bash
# F2 in CI: speed and quality of the own core on a converted model.
#  1. int8 copy of the model (iris quantize)
#  2. speed: title + introduction of an article – gate: int8, greedy and beam, at most $MAX_SECONDS s each
#  3. quality: chrF on FLORES-200 devtest (CC BY-SA 4.0, only measured here, never stored or learned from)
#     – gate: chrF of int8 at most $MAX_CHRF_DROP below float32
# Results go to annotations and the step summary. Usage: tools/reference/quality.sh <model.iris>
set -euo pipefail
model=$1
int8=${model%.iris}-int8.iris
iris="dotnet src/Iris.Cli/bin/Release/net10.0/iris.dll"
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
MAX_SECONDS=${MAX_SECONDS:-1}
MAX_CHRF_DROP=${MAX_CHRF_DROP:-0.5}
FLORES_URL=https://dl.fbaipublicfiles.com/nllb/flores200_dataset.tar.gz
FLORES_SHA256=b8b0b76783024b85797e5cc75064eb83fc5288b41e9654dabc7be6ae944011f6
status=0

# one annotation with several lines (GitHub shows at most 10 annotations of a kind per step)
note() {
  local level=$1 title
  title=$(printf '%s' "$2" | sed 's/%/%25/g; s/:/%3A/g; s/,/%2C/g')
  shift 2
  local text
  text=$(printf '%s\n' "$@" | sed 's/%/%25/g' | awk 'NR > 1 { printf "%%0A" } { printf "%s", $0 }')
  echo "::$level title=$title::$text"
}

$iris quantize "$model" "$int8" | tee /tmp/quantize.txt
note notice "int8 model" "$(cut -c1-200 /tmp/quantize.txt)"

# speed
$iris bench "$model" --runs 3 | tee /tmp/bench-f32.txt
if $iris bench "$int8" --runs 3 --max-seconds "$MAX_SECONDS" | tee /tmp/bench-int8.txt; then gate=PASSED; else gate=FAILED; status=1; fi
level=$([ "$gate" = PASSED ] && echo notice || echo error)
mapfile -t f32lines < <(grep -E '^(greedy|beam)' /tmp/bench-f32.txt | sed 's/^/float32 /')
mapfile -t int8lines < <(grep -E '^(greedy|beam)' /tmp/bench-int8.txt | sed 's/^/int8 /')
note "$level" "gate F2 speed $gate ($(nproc) processors, ≤ $MAX_SECONDS s)" "${f32lines[@]}" "${int8lines[@]}"
mapfile -t translated < <(grep '^  ' /tmp/bench-int8.txt | head -4 | sed 's/^  //')
note notice "bench translation (int8)" "${translated[@]}"

# glossary: do the placeholders of names and titles survive the real model? (invented names, measured only)
printf '%s\n' "Harbor Lights" "The Night Desk" "Mara Velin" "Tomas Brek" "Silver Meridian" \
  $'The Glass Orchard\tSklenený sad' $'Kestrel Bay\tZáliv Kestrel' > /tmp/glossary.tsv
printf '%s\n' "Harbor Lights returns for a third season with a new detective." \
  "The crime drama The Night Desk will end after five seasons." \
  "Mara Velin plays the lead in Silver Meridian, which opens on Friday." \
  "Critics loved The Glass Orchard, but the audience stayed away." \
  "Tomas Brek joins the cast of Kestrel Bay as a young doctor." \
  "Is Silver Meridian worth watching on a quiet evening?" \
  "After the screening, Mara Velin and Tomas Brek answered questions for almost an hour." \
  "The second season of Harbor Lights will have eight episodes." \
  "Why did the detective leave Kestrel Bay in the first episode?" \
  "The Night Desk and Harbor Lights were the most watched series of the year." > /tmp/glossary.en
$iris translate "$int8" --glossary /tmp/glossary.tsv < /tmp/glossary.en > /tmp/glossary.sk 2> /tmp/glossary.log
used=$(grep -c '^  glossary:' /tmp/glossary.log || true)
lost=$(grep -c 'did not survive' /tmp/glossary.log || true)
mapfile -t pairs < <(paste -d '\t' /tmp/glossary.en /tmp/glossary.sk | sed 's/\t/ → /')
note notice "glossary placeholders: kept in $used of $(wc -l < /tmp/glossary.en) sentences, lost in $lost" "${pairs[@]}"

# quality
mkdir -p /tmp/flores
curl -fsSL "$FLORES_URL" -o /tmp/flores/flores.tar.gz
flores_sha=$(sha256sum /tmp/flores/flores.tar.gz | cut -c1-64)
if [ "$flores_sha" != "$FLORES_SHA256" ]; then
  note error "FLORES-200 changed" "SHA-256 $flores_sha, expected $FLORES_SHA256 – the test set is not the one measured so far"
  exit 1
fi
tar -xzf /tmp/flores/flores.tar.gz -C /tmp/flores --wildcards '*/devtest/eng_Latn.devtest' '*/devtest/slk_Latn.devtest'
src=$(find /tmp/flores -name eng_Latn.devtest)
ref=$(find /tmp/flores -name slk_Latn.devtest)
$iris score "$model" "$src" "$ref" --out /tmp/flores/f32.sk | tee /tmp/score-f32.txt
$iris score "$int8" "$src" "$ref" --out /tmp/flores/int8.sk | tee /tmp/score-int8.txt
f32=$(grep -oP '^chrF2 \K[0-9.]+' /tmp/score-f32.txt)
q8=$(grep -oP '^chrF2 \K[0-9.]+' /tmp/score-int8.txt)
same=$(paste -d '\t' /tmp/flores/f32.sk /tmp/flores/int8.sk | awk -F'\t' '$1 == $2' | wc -l)
total=$(wc -l < /tmp/flores/f32.sk)
if awk -v a="$q8" -v b="$f32" -v d="$MAX_CHRF_DROP" 'BEGIN { exit !(a >= b - d) }'; then quality=PASSED; else quality=FAILED; status=1; fi
level=$([ "$quality" = PASSED ] && echo notice || echo error)
note "$level" "gate F2 quality $quality" "FLORES-200 devtest en→sk chrF2: float32 $f32, int8 $q8 (allowed drop $MAX_CHRF_DROP)" \
  "identical translations float32 / int8: $same of $total" "FLORES-200: $FLORES_URL, SHA-256 $flores_sha"
{
  echo "### F2 – speed and quality"
  echo
  echo '```'
  cat /tmp/bench-f32.txt /tmp/bench-int8.txt
  echo '```'
  echo
  echo "FLORES-200 devtest en→sk chrF2: **float32 $f32**, **int8 $q8**; identical translations $same/$total"
} >> "$summary"
exit $status
