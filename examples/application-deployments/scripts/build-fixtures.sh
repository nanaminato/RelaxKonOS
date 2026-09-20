#!/usr/bin/env sh
# Rebuilds every application-deployment fixture, then re-checks them offline.
#
# Requires: a JDK (for the JAR fixture), the .NET SDK (for the publish fixtures), and Python 3 (for
# deterministic packing and verification). Override the tool names with JAVA/JAVAC/JAR, DOTNET and
# PYTHON when they are not on PATH.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/.." && pwd)
python_bin=${PYTHON:-python3}

for fixture in java-http dotnet-web dotnet-worker python-web python-worker; do
  sh "$root/fixtures/$fixture/build.sh"
done

"$python_bin" "$here/build-negative-fixtures.py" "$root/fixtures/negative"

echo
echo "== offline verification =="
"$python_bin" "$here/verify-fixtures.py"
