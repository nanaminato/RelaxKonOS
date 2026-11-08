#!/bin/bash
# Bind over systemctl only inside the private test mount and systemd namespaces.
set -euo pipefail
fault=/run/relaxkonos-mihomo-start-fault
if [[ $# != 2 || $1 != start || $2 != relaxkonos-mihomo.service || ! -f $fault/armed ]]; then
    exec "$fault/systemctl" "$@"
fi
mv -- "$fault/armed" "$fault/consumed"
"$fault/systemctl" "$@"
touch "$fault/started"
for ((attempt=0; attempt<200; attempt++)); do
    if [[ -f $fault/release ]]; then exit 0; fi
    sleep 0.05
done
echo 'Isolated Mihomo candidate-start barrier was not released' >&2
exit 1
