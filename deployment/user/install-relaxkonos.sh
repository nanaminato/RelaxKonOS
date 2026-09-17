#!/usr/bin/env bash
# The user-server bundle intentionally ships this small explicit-mode entry point instead of
# the System Mode bootstrapper, which would carry privileged installation dependencies.
set -euo pipefail
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
[[ ${1:-} == --mode && ${2:-} == user ]] || {
  echo 'usage: install-relaxkonos.sh --mode user [--bundle PATH | --release-uri HTTPS_URL --release-sha256 SHA256]' >&2
  exit 64
}
shift 2
[[ $EUID -ne 0 ]] || { echo 'User Mode must not be installed as root.' >&2; exit 77; }
exec "$script_directory/relaxkon" install "$@"
