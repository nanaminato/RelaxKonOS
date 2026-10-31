#!/usr/bin/env bash
set -euo pipefail

# Root-only provisioning of the fixed launcher, never called by a module or an HTTP endpoint.
# The runtime payload must already be copied to a root-owned release directory.
[[ $EUID -eq 0 && $# -eq 3 ]] || { echo 'usage: install-relaxkonos-module-host.sh EXECUTABLE SOURCE_ROOT SERVER_USER' >&2; exit 64; }
executable=$1 source_root=$2 server_user=$3
[[ $executable =~ ^/[A-Za-z0-9._/-]+$ && $source_root =~ ^/[A-Za-z0-9._/-]+$ && $server_user =~ ^[a-z_][a-z0-9_-]*$ ]] || exit 64
[[ -f $executable && ! -L $executable && $(basename "$executable") == RelaxKonOS.ServerModuleHost ]] || exit 65
server_uid=$(id -u "$server_user")
[[ $server_uid -ne 0 ]] || { echo 'ModuleHost requires an unprivileged Server account.' >&2; exit 65; }
command -v setpriv >/dev/null
command -v pkill >/dev/null
command -v useradd >/dev/null
command -v visudo >/dev/null
python3 - "$executable" <<'PY'
import pathlib, stat, sys
executable = pathlib.Path(sys.argv[1])
for path in [*executable.parents, *executable.parent.rglob('*')]:
    value = path.lstat()
    if value.st_uid != 0 or value.st_mode & 0o022 or not (stat.S_ISDIR(value.st_mode) or stat.S_ISREG(value.st_mode)):
        raise SystemExit('ModuleHost runtime and ancestors must be root-owned, regular and not group/world writable.')
PY
install -d -o "$server_user" -g "$(id -gn "$server_user")" -m 0700 "$source_root"
install -d -o root -g root -m 0755 /etc/relaxkonos
temporary=$(mktemp /etc/relaxkonos/module-host.XXXXXX)
sudoers=$(mktemp /etc/sudoers.d/relaxkonos-module-host.XXXXXX)
trap 'rm -f -- "$temporary" "$sudoers"' EXIT
printf '{"serverUid":%s,"sourceRoot":"%s","isolationRoot":"/var/lib/relaxkonos-module-hosts"}\n' "$server_uid" "$source_root" > "$temporary"
chown root:root "$temporary"
chmod 0600 "$temporary"
printf '%s ALL=(root) NOPASSWD: %s --isolated\n' "$server_user" "$executable" > "$sudoers"
chmod 0440 "$sudoers"
visudo -cf "$sudoers"
install -o root -g root -m 0600 "$temporary" /etc/relaxkonos/module-host.json
install -o root -g root -m 0440 "$sudoers" /etc/sudoers.d/relaxkonos-module-host
echo 'Fixed ModuleHost launcher provisioned; module accounts are created on first isolated start.'
