#!/usr/bin/env bash
# Runs `iris verify` in CI and turns its result into annotations (the summary, the first mismatches), which can be
# read without the logs. Usage: tools/reference/verify.sh <model.iris> <reference.json> [iris verify options]
set -uo pipefail
iris="dotnet src/Iris.Cli/bin/Release/net10.0/iris.dll"
name=$(basename "$1" .iris)
$iris verify "$@" | tee "/tmp/verify-$name.txt"
status=${PIPESTATUS[0]}
grep '^mismatch:' "/tmp/verify-$name.txt" | head -8 | sed "s/^mismatch: /::warning title=$name mismatch::/"
summary=$(grep '^summary:' "/tmp/verify-$name.txt" | cut -c10-)
if [ "$status" -eq 0 ]; then
  echo "::notice title=$name verify PASSED::$summary"
else
  echo "::error title=$name verify FAILED::$summary"
fi
{
  echo "### $name"
  echo
  echo "$summary"
  echo
  grep '^result:' "/tmp/verify-$name.txt"
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
exit "$status"
