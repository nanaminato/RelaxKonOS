#!/usr/bin/env bash
set -euo pipefail
repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
# Exercise the real installer function without modifying host accounts or policy.
eval "$(sed -n '/^install_docker_access_policy() {$/,/^}$/p' "$repository/deployment/linux/install-relaxkonos-services.sh" | tr -d '\r')"
SERVICE_USER=relaxkonos
DOCKER_ACCESS=true
group_exists=false
calls=()
mktemp() { printf '/dev/null\n'; }
chown() { :; }
chmod() { :; }
mv() { :; }
rm() { calls+=(remove-policy); }
getent() { [[ "$*" == 'group docker' && "$group_exists" == true ]]; }
groupadd() { [[ "$*" == '--system docker' ]]; calls+=(create-group); group_exists=true; }
usermod() {
  [[ "$group_exists" == true && "$*" == '--append --groups docker relaxkonos' ]]
  calls+=(grant-access)
}
install_docker_access_policy
[[ "${calls[*]}" == 'create-group grant-access' ]]
calls=()
install_docker_access_policy
[[ "${calls[*]}" == 'grant-access' ]]
calls=()
DOCKER_ACCESS=false
install_docker_access_policy
[[ "${calls[*]}" == 'remove-policy' ]]
printf 'PASS: Docker authorization prepares membership before installation, reuses the group, and requires explicit opt-in.\n'
