#!/usr/bin/env bash
# F3 in CI: learning on a converted model (float32 file, as prepare-opus.sh writes it).
#  1. reference: loss and gradient of a random adapter on pairs of invented sentences equal those of PyTorch
#  2. learning on the int8 model: invented sentences → their beam translations (as if the owner had corrected the
#     greedy ones); after learning, greedy must give the corrected translations more often and the loss must fall
#  3. no harm: chrF on FLORES-200 devtest (only measured, never learned from) at most $MAX_CHRF_DROP below the model
# Usage: tools/reference/learning.sh <model-dir> <model.iris> <source FLORES code> <target FLORES code>
set -euo pipefail
dir=$1
model=$2
source_code=$3
target_code=$4
iris="dotnet src/Iris.Cli/bin/Release/net10.0/iris.dll"
py=${IRIS_VENV:-/tmp/venv}/bin/python
PAIRS=${PAIRS:-160}
EPOCHS=${EPOCHS:-2}
MAX_CHRF_DROP=${MAX_CHRF_DROP:-0.5}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
work=$(mktemp -d)
status=0

note() { # one annotation with several lines
  local level=$1 title text
  title=$(printf '%s' "$2" | sed 's/%/%25/g; s/:/%3A/g; s/,/%2C/g')
  shift 2
  text=$(printf '%s\n' "$@" | sed 's/%/%25/g' | awk 'NR > 1 { printf "%%0A" } { printf "%s", $0 }')
  echo "::$level title=$title::$text"
}

# 1. the reference
$py tools/reference/marian_reference.py learning "$dir" "$work/reference-adapter.iris" "$work/learning.json" --count 12
if $iris check-learning "$model" "$work/reference-adapter.iris" "$work/learning.json" | tee "$work/check.txt"; then
  note notice "F3 reference: PASSED" "$(head -2 "$work/check.txt" | tail -1)"
else
  note error "F3 reference: FAILED" "$(tail -n +2 "$work/check.txt" | head -12)"
  status=1
fi

# 2. learning on the int8 model
int8=$work/model-int8.iris
$iris quantize "$model" "$int8" > /dev/null
python3 -c "import sys; sys.path.insert(0, 'tools/reference'); import sentences; print('\n'.join(sentences.sentences($PAIRS)))" > "$work/source.txt"
$iris translate "$int8" --beams 4 < "$work/source.txt" > "$work/corrected.txt" 2> /dev/null
$iris translate "$int8" --greedy < "$work/source.txt" > "$work/greedy-before.txt" 2> /dev/null
paste "$work/source.txt" "$work/corrected.txt" > "$work/pairs.tsv"
$iris learn "$int8" "$work/pairs.tsv" "$work/adapter.iris" --epochs "$EPOCHS" | tee "$work/learn.txt"
$iris translate "$int8" --greedy --adapter "$work/adapter.iris" < "$work/source.txt" > "$work/greedy-after.txt" 2> /dev/null
same() { paste -d '\t' "$1" "$2" | awk -F '\t' '$1 == $2 { n++ } END { print n + 0 }'; }
before=$(same "$work/greedy-before.txt" "$work/corrected.txt")
after=$(same "$work/greedy-after.txt" "$work/corrected.txt")
learned=$(tail -1 "$work/learn.txt")
loss_before=$(sed -nE 's/^loss per token ([0-9.]+) → ([0-9.]+).*/\1/p' "$work/learn.txt")
loss_after=$(sed -nE 's/^loss per token ([0-9.]+) → ([0-9.]+).*/\2/p' "$work/learn.txt")
learning_ok=$(awk -v a="$after" -v b="$before" -v la="$loss_after" -v lb="$loss_before" 'BEGIN { print (a > b && la < 0.8 * lb) ? 1 : 0 }')
lines=("corrected translations given by greedy: $before → $after of $PAIRS" "$learned")
if [ "$learning_ok" = 1 ]; then note notice "F3 learning: PASSED" "${lines[@]}"; else note error "F3 learning: FAILED" "${lines[@]}"; status=1; fi

# 3. no harm on FLORES-200 devtest
bash tools/reference/flores.sh "$source_code" "$target_code" > /dev/null
base=$($iris score "$int8" "/tmp/flores/$source_code.devtest" "/tmp/flores/$target_code.devtest" --greedy | tail -1)
adapted=$($iris score "$int8" "/tmp/flores/$source_code.devtest" "/tmp/flores/$target_code.devtest" --greedy --adapter "$work/adapter.iris" | tail -1)
chrf() { sed -nE 's/^chrF2 ([0-9.]+).*/\1/p' <<< "$1"; }
drop_ok=$(awk -v a="$(chrf "$adapted")" -v b="$(chrf "$base")" -v m="$MAX_CHRF_DROP" 'BEGIN { print (a >= b - m) ? 1 : 0 }')
lines=("model: $base" "with the adapter: $adapted" "allowed drop: $MAX_CHRF_DROP")
if [ "$drop_ok" = 1 ]; then note notice "F3 no harm (FLORES-200): PASSED" "${lines[@]}"; else note error "F3 no harm (FLORES-200): FAILED" "${lines[@]}"; status=1; fi

{
  echo "### F3 – learning"
  echo
  echo '```'
  cat "$work/check.txt" "$work/learn.txt"
  echo "corrected translations given by greedy: $before → $after of $PAIRS"
  echo "FLORES-200 devtest: $base"
  echo "with the adapter:   $adapted"
  echo '```'
} >> "$summary"
exit $status
