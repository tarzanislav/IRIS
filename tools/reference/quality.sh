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
status=0

$iris quantize "$model" "$int8" | tee /tmp/quantize.txt
echo "::notice title=int8 model::$(cut -c1-200 /tmp/quantize.txt)"

# speed
$iris bench "$model" --runs 3 | tee /tmp/bench-f32.txt
if $iris bench "$int8" --runs 3 --max-seconds "$MAX_SECONDS" | tee /tmp/bench-int8.txt; then gate=PASSED; else gate=FAILED; status=1; fi
for kind in f32 int8; do
  grep -E '^(greedy|beam)' "/tmp/bench-$kind.txt" | sed "s/^/::notice title=speed $kind ($(nproc) processors)::/"
done
level=$([ "$gate" = PASSED ] && echo notice || echo error)
echo "::$level title=gate F2 speed $gate::int8 title + introduction ≤ $MAX_SECONDS s (greedy and beam)"
grep '^  ' /tmp/bench-int8.txt | head -4 | sed 's/^  /::notice title=bench translation (int8)::/'

# quality
mkdir -p /tmp/flores
curl -fsSL "$FLORES_URL" -o /tmp/flores/flores.tar.gz
echo "::notice title=FLORES-200::$FLORES_URL SHA-256 $(sha256sum /tmp/flores/flores.tar.gz | cut -c1-64)"
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
echo "::$level title=gate F2 quality $quality::FLORES-200 devtest en→sk chrF2: float32 $f32, int8 $q8 (allowed drop $MAX_CHRF_DROP); identical translations $same/$total"
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
