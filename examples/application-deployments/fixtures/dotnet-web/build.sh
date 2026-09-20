#!/usr/bin/env sh
# Builds fixtures/dotnet-web/dist/relaxkonos-ad-dotnet-web.zip from a framework-dependent publish.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/../.." && pwd)
dotnet_bin=${DOTNET:-dotnet}
python_bin=${PYTHON:-python3}

rm -rf "$here/dist" "$here/.publish"
mkdir -p "$here/dist" "$here/.publish"

# Framework-dependent and without a RID on purpose: the published runtimeconfig.json then carries
# tfm/frameworks only, which is exactly what the DotNetPublish template validates. A RID-specific
# publish would additionally be required to target linux-*.
"$dotnet_bin" publish "$here/src/DotNetWebDemo/DotNetWebDemo.csproj" \
  -c Release --nologo -o "$here/.publish"

"$python_bin" "$root/scripts/pack.py" "$here/.publish" \
  "$here/dist/relaxkonos-ad-dotnet-web.zip" relaxkonos-ad-dotnet-web

rm -rf "$here/.publish"
echo "built $here/dist/relaxkonos-ad-dotnet-web.zip"
