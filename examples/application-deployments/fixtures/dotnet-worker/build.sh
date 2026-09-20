#!/usr/bin/env sh
# Builds fixtures/dotnet-worker/dist/relaxkonos-ad-dotnet-worker.zip from a framework-dependent publish.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/../.." && pwd)
dotnet_bin=${DOTNET:-dotnet}
python_bin=${PYTHON:-python3}

rm -rf "$here/dist" "$here/.publish"
mkdir -p "$here/dist" "$here/.publish"

# Microsoft.NET.Sdk (not Sdk.Web), so the published runtimeconfig.json has no
# Microsoft.AspNetCore.App entry: this archive must be rejected for workloadKind=Web.
"$dotnet_bin" publish "$here/src/DotNetWorkerDemo/DotNetWorkerDemo.csproj" \
  -c Release --nologo -o "$here/.publish"

"$python_bin" "$root/scripts/pack.py" "$here/.publish" \
  "$here/dist/relaxkonos-ad-dotnet-worker.zip" relaxkonos-ad-dotnet-worker

rm -rf "$here/.publish"
echo "built $here/dist/relaxkonos-ad-dotnet-worker.zip"
