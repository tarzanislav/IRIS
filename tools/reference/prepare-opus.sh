#!/usr/bin/env bash
# CI only: the Python tools of the reference (pinned versions) and, with a directory, an OPUS-MT model at its pinned
# revision (licence checked on download) converted to the IRIS format: <dir>/opus-<pair>.iris. The one place of the
# model revisions; the revision is also put into GITHUB_ENV for later steps (release notice).
# Usage: tools/reference/prepare-opus.sh [dir [pair]]   (pair: en-sk – the default –, en-cs, sk-en, cs-en)
set -euo pipefail
declare -A REVISIONS=(
  [en-sk]=04d19ccd4566720bceeff93623d5cf93be659816
  [en-cs]=main
  [sk-en]=main
  [cs-en]=main
)
here=$(cd "$(dirname "$0")" && pwd)
venv=${IRIS_VENV:-/tmp/venv}
py=$venv/bin/python
export HF_HUB_DISABLE_TELEMETRY=1 DO_NOT_TRACK=1 PIP_DISABLE_PIP_VERSION_CHECK=1

if [ ! -f "$venv/.iris-tools" ]; then
  python3 -m venv "$venv" || { sudo apt-get install -y -q python3-venv && python3 -m venv "$venv"; }
  # PyTorch (CPU) from its own index; everything else from PyPI, the dependencies held by constraints.txt
  "$venv/bin/pip" install -q --index-url https://download.pytorch.org/whl/cpu -r "$here/requirements-torch.txt"
  "$venv/bin/pip" install -q -c "$here/constraints.txt" -r "$here/requirements.txt"
  "$py" -c "import torch, transformers; print('torch', torch.__version__, 'transformers', transformers.__version__)"
  touch "$venv/.iris-tools"
fi

[ $# -ge 1 ] || exit 0
dir=$1
pair=${2:-en-sk}
revision=${REVISIONS[$pair]:-}
[ -n "$revision" ] || { echo "::error::no OPUS-MT model is set up for $pair"; exit 1; }
mkdir -p "$dir"
"$py" "$here/marian_reference.py" download "Helsinki-NLP/opus-mt-$pair" "$dir/opus-$pair" --revision "$revision"
"$py" "$here/marian_reference.py" convert "$dir/opus-$pair" "$dir/opus-$pair.iris"
name=$(echo "OPUS_MT_${pair}_REVISION" | tr 'a-z-' 'A-Z_')
echo "$name=$revision" >> "${GITHUB_ENV:-/dev/null}"
