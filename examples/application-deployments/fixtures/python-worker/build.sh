#!/usr/bin/env sh
# Packs fixtures/python-worker/src into fixtures/python-worker/dist/relaxkonos-ad-python-worker.zip.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/../.." && pwd)
python_bin=${PYTHON:-python3}

rm -rf "$here/dist"
mkdir -p "$here/dist"

"$python_bin" "$root/scripts/pack.py" "$here/src" \
  "$here/dist/relaxkonos-ad-python-worker.zip" relaxkonos-ad-python-worker

echo "built $here/dist/relaxkonos-ad-python-worker.zip"
