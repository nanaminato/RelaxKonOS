#!/usr/bin/env sh
# Packs fixtures/python-web/src into fixtures/python-web/dist/relaxkonos-ad-python-web.zip.
#
# Nothing is compiled here: the PythonProject template builds the image itself, installing the pinned
# requirements inside the container. The host Python is only used for deterministic packing.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/../.." && pwd)
python_bin=${PYTHON:-python3}

rm -rf "$here/dist"
mkdir -p "$here/dist"

"$python_bin" "$root/scripts/pack.py" "$here/src" \
  "$here/dist/relaxkonos-ad-python-web.zip" relaxkonos-ad-python-web

echo "built $here/dist/relaxkonos-ad-python-web.zip"
