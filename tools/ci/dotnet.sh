#!/usr/bin/env bash
# CI: runs a dotnet command and turns its compiler, XAML and format errors into annotations (readable without the
# logs; GitHub shows at most 10 per step, so they are joined into one). Usage: tools/ci/dotnet.sh build Iris.slnx -c Release
set -uo pipefail
log=$(mktemp)
dotnet "$@" 2>&1 | tee "$log"
status=${PIPESTATUS[0]}
if [ "$status" -ne 0 ]; then
  errors=$(grep -E "(error|warning) [A-Z]+[0-9]+|error WHITESPACE|error [A-Z]+:" "$log" | sed -E 's#^.*/(src|tests|tools)/#\1/#; s/ \[[^]]*\]$//' | sort -u | head -40)
  text=$(printf '%s\n' "$errors" | sed 's/%/%25/g' | awk 'NR > 1 { printf "%%0A" } { printf "%s", $0 }')
  echo "::error title=dotnet $1 failed::${text:-see the log}"
fi
exit "$status"
