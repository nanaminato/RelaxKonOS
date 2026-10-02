#!/usr/bin/env bash
# RelaxKonOS remote deployment launcher (Linux).
#
# This is the only thing a client executes over SSH. It accepts a fixed action set and a
# structured request; it never accepts an arbitrary command, script path, service name or
# delete path. The package and this launcher are uploaded into one private staging directory,
# and package paths are derived from its own location. The journal has a fixed account-wide path
# so reconnects and two separately staged clients see the same operation records and write lock.
#
# It validates the request, takes a per-installation write lock, keeps a persistent operation
# record and event stream, invokes the existing deployment engine, and prints machine-readable
# JSON Lines on stdout. Engine stdout/stderr is captured as a restricted diagnostic attachment.
set -euo pipefail

protocol_version=1
sudo_requested=false
sudo_password=
run_privileged() {
  # Authenticate via stdin; the engine never inherits password input, including NOPASSWD policies.
  sudo -S -k -p '' -- bash -c 'exec "$@" </dev/null' bash "$@" <<< "$sudo_password"
}
authenticate_sudo() {
  [[ $sudo_requested == true ]] || return 0
  [[ $options_mode == linuxSystem || $operation_kind == probe ]] || launcher_fail invalid_request "sudo is only supported for Linux System Mode"
  if ! command -v sudo >/dev/null || ! run_privileged true >/dev/null 2>&1; then
    launcher_fail elevation_required "sudo 验证失败：请检查 sudo 密码以及当前账号的 sudo 权限。"
  fi
}
script_raw=${BASH_SOURCE[0]}
staging_root=$(cd -- "$(dirname -- "$script_raw")" && pwd -P)
request_path=$staging_root/request.json
package_root=$staging_root/package
if (( EUID == 0 )); then
  journal_root=/var/lib/relaxkonos-deployment
else
  journal_root=${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos-deployment
fi
operations_root=$journal_root/operations
lock_path=$journal_root/deploy.lock

# --- journal -----------------------------------------------------------------------------------
# stdout carries only JSON Lines; every human-readable message goes to stderr so a client can
# parse the stream without filtering engine noise.
launcher_stdout() { printf '%s\n' "$1"; }
launcher_note() { printf '%s\n' "$*" >&2; }
launcher_fail() { # problemCode safeMessage exitCode persist
  local code=$1 message=$2 code_exit=${3:-2} persist=${4:-true}
  emit_rejection "$code" "$message" "$persist"
  exit "$code_exit"
}

json_escape() { local s=$1; s=${s//\\/\\\\}; s=${s//\"/\\\"}; s=${s//$'\n'/ }; printf '%s' "$s"; }
json_string_or_null() { [[ -n ${1:-} ]] && printf '"%s"' "$(json_escape "$1")" || printf 'null'; }
json_or_null() { [[ -n ${1:-} ]] && printf '%s' "$1" || printf 'null'; }
json_number_or_null() { [[ -n ${1:-} ]] && printf '%s' "$1" || printf 'null'; }
now_utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }

operation_id=
operation_kind=
installation_id=
sequence=0
record_state=queued
record_phase=queued
record_progress=
record_problem=
record_message=
record_cancellable=false
record_result=
record_snapshot=
record_probe=
started_at=
completed_at=
journal_ready=false

ensure_journal() {
  [[ ! -L $staging_root ]] || launcher_fail invalid_request "staging directory must not be a symbolic link"
  [[ -d $staging_root && -O $staging_root ]] || launcher_fail invalid_request "staging directory must be owned by the invoking account"
  local mode
  mode=$(stat -c %a -- "$staging_root")
  (( mode % 100 / 10 == 0 && mode % 10 == 0 )) || launcher_fail invalid_request "staging directory must not be group- or world-writable"
  [[ ! -L $journal_root ]] || launcher_fail invalid_request "deployment journal must not be a symbolic link"
  (umask 077; mkdir -p -- "$operations_root")
  [[ -O $journal_root && -O $operations_root && ! -L $operations_root ]] \
    || launcher_fail invalid_request "deployment journal must be owned by the invoking account"
  chmod 700 -- "$journal_root" "$operations_root"
  journal_ready=true
}
events_path() { printf '%s/%s.jsonl' "$operations_root" "$operation_id"; }
record_path() { printf '%s/%s.json' "$operations_root" "$operation_id"; }
digest_path() { printf '%s/%s.digest' "$operations_root" "$operation_id"; }
diagnostics_path() { printf '%s/%s.log' "$operations_root" "$operation_id"; }

write_record() {
  local temporary; temporary="$(record_path).new"
  umask 077
  printf '{"schemaVersion":%s,"operationId":"%s","installationId":%s,"kind":"%s","phase":"%s","state":"%s","sequence":%s,"timestampUtc":"%s","progress":%s,"problemCode":%s,"safeMessage":%s,"cancellable":%s,"startedAtUtc":%s,"completedAtUtc":%s,"result":%s,"snapshot":%s,"probe":%s}\n' \
    "$protocol_version" "$operation_id" "$(json_string_or_null "$installation_id")" "$operation_kind" \
    "$record_phase" "$record_state" "$sequence" "$(now_utc)" "$(json_number_or_null "$record_progress")" \
    "$(json_string_or_null "$record_problem")" "$(json_string_or_null "$record_message")" \
    "$record_cancellable" "$(json_string_or_null "$started_at")" "$(json_string_or_null "$completed_at")" \
    "$(json_or_null "$record_result")" "$(json_or_null "$record_snapshot")" "$(json_or_null "$record_probe")" > "$temporary"
  chmod 600 "$temporary"; mv -f -- "$temporary" "$(record_path)"
}

# Cancellable phases mirror ServerDeploymentLifecycle: only the safe points before the critical
# section may be cancelled, so a client can rely on the flag rather than guessing.
phase_is_cancellable() {
  case "$1" in queued|validatingRequest|acquiringLock|preflight|staging|transferring|verifyingPackage|snapshotting) printf 'true';; *) printf 'false';; esac
}
# Phase transitions are monotonic by ordinal, so a client that reconnects can trust the record
# even when it missed intermediate events.
emit_event() { # phase state progress problemCode safeMessage
  local phase=$1 state=$2 progress=$3 problem=$4 message=$5
  [[ -z $problem || $problem == server-deployment.* ]] || problem=server-deployment.$problem
  sequence=$((sequence + 1))
  record_phase=$phase; record_state=$state; record_progress=$progress; record_problem=$problem; record_message=$message
  record_cancellable=$(phase_is_cancellable "$phase")
  case "$state" in succeeded|failed|cancelled|interrupted) record_cancellable=false;; esac
  local line
  line=$(printf '{"schemaVersion":%s,"operationId":"%s","installationId":%s,"kind":"%s","phase":"%s","state":"%s","sequence":%s,"timestampUtc":"%s","progress":%s,"problemCode":%s,"safeMessage":%s}' \
    "$protocol_version" "$operation_id" "$(json_string_or_null "$installation_id")" "$operation_kind" \
    "$phase" "$state" "$sequence" "$(now_utc)" "$(json_number_or_null "$progress")" \
    "$(json_string_or_null "$problem")" "$(json_string_or_null "$message")")
  printf '%s\n' "$line" >> "$(events_path)"
  launcher_stdout "$line"
  write_record
}
emit_rejection() { # problemCode safeMessage persist
  local problem=$1
  [[ $problem == server-deployment.* ]] || problem=server-deployment.$problem
  record_phase=failed; record_state=failed; record_problem=$problem; record_message=$2; record_cancellable=false
  completed_at=$(now_utc)
  if [[ ${3:-true} == true && $journal_ready == true && $operation_id =~ ^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$ ]]; then
    write_record
  fi
  launcher_stdout "$(record_json_only)"
}
record_json_only() {
  printf '{"schemaVersion":%s,"operationId":"%s","installationId":%s,"kind":"%s","phase":"%s","state":"%s","sequence":%s,"timestampUtc":"%s","progress":%s,"problemCode":%s,"safeMessage":%s,"cancellable":%s,"startedAtUtc":%s,"completedAtUtc":%s,"result":%s,"snapshot":%s,"probe":%s}' \
    "$protocol_version" "$operation_id" "$(json_string_or_null "$installation_id")" "$operation_kind" \
    "$record_phase" "$record_state" "$sequence" "$(now_utc)" "$(json_number_or_null "$record_progress")" \
    "$(json_string_or_null "$record_problem")" "$(json_string_or_null "$record_message")" \
    "$record_cancellable" "$(json_string_or_null "$started_at")" "$(json_string_or_null "$completed_at")" \
    "$(json_or_null "$record_result")" "$(json_or_null "$record_snapshot")" "$(json_or_null "$record_probe")"
}

# --- request -----------------------------------------------------------------------------------
# The Linux host uses its standard Python 3 runtime for JSON and safe ZIP handling.
# No RID-specific client executable is uploaded or required.
deployment_python() {
  python3 - "$@" <<'PY'
import errno, hashlib, json, os, re, shutil, stat, sys, urllib.request, zipfile
from pathlib import Path
import datetime, ssl, subprocess, uuid, tempfile

def recovery_trusted(path):
    # Every ancestor must prevent non-root replacement, including symlink targets.
    resolved = path.resolve(strict=True)
    for item in [resolved, *resolved.parents]:
        info = item.stat()
        if info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('Untrusted installation path: ' + str(item))
    return resolved

def recovery_state(root=Path("/opt/relaxkonos"), data=Path("/var/lib/relaxkonos"), config=Path("/etc/relaxkonos")):
    """Reconstruct only a verified, live installation at the fixed system paths."""
    if os.geteuid() != 0: raise ValueError('Recovery requires root or authenticated sudo')
    trusted = recovery_trusted
    state = data/'install-state.json'
    if state.exists() or state.is_symlink(): raise ValueError('An installation record already exists; use repair')
    trusted(root)
    if (root/'current').lstat().st_uid != 0: raise ValueError('Current version link is not root-owned')
    current = trusted(root/'current')
    if current.parent != root/'versions' or not re.fullmatch(r'[0-9A-Za-z][0-9A-Za-z.+_-]{0,127}', current.name):
        raise ValueError('Current version does not point to a managed version directory')
    def show(unit, field):
        return subprocess.check_output(['systemctl','show',unit,'--property='+field,'--value'],text=True).strip()
    for unit, folder, executable, user in (
        ('relaxkonos-server.service','server','RelaxKonOS.Server','relaxkonos-server'),
        ('relaxkonos-guardian.service','guardian','RelaxKonOS.Guardian.Agent','')):
        binary = trusted(current/folder/executable)
        with binary.open('rb') as binary_stream: magic = binary_stream.read(4)
        if magic != b'\x7fELF': raise ValueError('Invalid deployed executable: ' + executable)
        trusted(Path(show(unit,'FragmentPath')))
        if show(unit,'ActiveState') != 'active' or show(unit,'User') not in ({user} if user else {'','root'}):
            raise ValueError('Service is inactive or has an unexpected account: ' + unit)
        if ('path='+str(binary)+' ;') not in show(unit,'ExecStart'):
            raise ValueError('Unexpected service executable: ' + unit)
    trusted(current/'privileged-helper/RelaxKonOS.PrivilegedHelper')
    if Path(show('relaxkonos-server.service','WorkingDirectory')).resolve() != current/'server':
        raise ValueError('Unexpected server working directory')
    environment = dict(line.split('=',1) for line in trusted(config/'server.env').read_text().splitlines() if line and not line.startswith('#') and '=' in line)
    if environment.get('Storage__DatabasePath') != str(data/'server/relaxkonos.db'):
        raise ValueError('Server database is outside the managed data directory')
    if not (data/'server/relaxkonos.db').is_file() or (current/'server/data').resolve() != data/'server':
        raise ValueError('Managed database or server data link is missing')
    listen = re.findall(r'(?:^|\s)ASPNETCORE_URLS=(https?://(?:127\.0\.0\.1|0\.0\.0\.0):[0-9]+)(?:\s|$)',show('relaxkonos-server.service','Environment'))
    if len(listen) != 1: raise ValueError('Unsupported or ambiguous server listening address')
    url = listen[0]; scheme, port = url.split('://')[0], int(url.rsplit(':',1)[1])
    if not 1 <= port <= 65535: raise ValueError('Invalid server port')
    certificate = 'none'
    if scheme == 'https':
        certificate_path = Path(environment.get('Kestrel__Certificates__Default__Path',''))
        if certificate_path != data/'server/certificates/bootstrap.pfx': raise ValueError('Unexpected TLS certificate path')
        # The server data directory is deliberately owned by the service account.
        # Validate the fixed TLS files themselves rather than demanding root ownership
        # of that writable parent, which would reject every normal installation.
        for item in (certificate_path.parent, certificate_path):
            info = item.lstat()
            if item.is_symlink() or info.st_uid != 0 or info.st_mode & 0o022:
                raise ValueError('Unexpected TLS file ownership or permissions')
        if not environment.get('Kestrel__Certificates__Default__Password'): raise ValueError('TLS credential is missing')
        certificate = 'custom' # Preserve the actual installed certificate; never rotate it during recovery.
    with urllib.request.urlopen(f'{scheme}://127.0.0.1:{port}/healthz',timeout=15,context=ssl._create_unverified_context()) as response:
        if response.status != 200: raise ValueError('Server health check failed')
    def access(suffix):
        lines = trusted(config/('privileged-helper-roots'+suffix)).read_text().splitlines()
        if not lines or any(not line.startswith('/') for line in lines): raise ValueError('Invalid file access policy')
        if lines == ['/']: return 'full'
        expected = ['/etc/relaxkonos',str(data)] + (['/root'] if suffix == '-root' else [])
        return 'restricted' if lines == expected else 'whitelist'
    docker = config/'docker-access-user'
    if docker.exists() and trusted(docker).read_text().strip() != 'relaxkonos-server': raise ValueError('Unexpected Docker access account')
    value = dict(schemaVersion=2,installed=True,mode='linuxSystem',installationId="rki-"+uuid.uuid4().hex,version=current.name,
                 previousVersion=None,installedAtUtc=datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
                 installRoot=str(root),dataRoot=str(data),networkProfile='lan' if '0.0.0.0' in url else 'local',listenUrl=url,
                 certificateMode=certificate,fileAccess=access(''),administratorFileAccess=access('-administrator'),
                 rootFileAccess=access('-root'),dockerAccess=docker.exists())
    trusted(data)
    # Publish atomically without overwriting a record created by another operation.
    descriptor, temporary = tempfile.mkstemp(prefix='.recovery-',dir=data)
    try:
        with os.fdopen(descriptor,'w') as output:
            json.dump(value,output); output.flush(); os.fsync(output.fileno())
        os.link(temporary,state)
    finally: os.unlink(temporary)
    print('Verified installation recovered: '+current.name)

def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise ValueError('duplicate JSON field')
        result[key] = value
    return result

def load(text): return json.loads(text, object_pairs_hook=unique)

def stream_digest(stream):
    digest = hashlib.sha256()
    while chunk := stream.read(1024*1024): digest.update(chunk)
    return digest.hexdigest()

def request(path):
    raw = Path(path).read_bytes()
    if not 0 < len(raw) <= 65536 or b'\n' in raw or b'\r' in raw: raise ValueError('request size/line')
    value = load(raw)
    root = {'schemaVersion', 'operationId', 'kind', 'options'}
    keys = {'source','network','retention','mode','version','packageUri','stagedPackageName',
            'packageDigest','remotePackagePath','expectedInstallationId','serverPort','fileAccess',
            'certificateMode','selfSignedIdentities','confirmed'}
    if type(value) is not dict or set(value) - root: raise ValueError('request fields')
    if type(value.get('schemaVersion')) is not int or value['schemaVersion'] != 1: raise ValueError('schema')
    if not isinstance(value.get('operationId'), str) or not re.fullmatch(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}',value['operationId']): raise ValueError('id')
    if value.get('kind') not in {'probe','install','upgrade','repair','uninstall','status','rollback'}: raise ValueError('kind')
    options = value.get('options')
    if options is not None:
        if type(options) is not dict or set(options) - keys: raise ValueError('options fields')
        for key in ('source','network'):
            if type(options.get(key)) is not str: raise ValueError('required string')
        for key, item in options.items():
            if key == 'confirmed': valid = type(item) is bool
            elif key == 'serverPort': valid = item is None or type(item) is int
            elif key in ('source','network','retention'): valid = type(item) is str
            else: valid = item is None or type(item) is str
            if not valid or isinstance(item,str) and any(ord(c)<32 for c in item): raise ValueError('option type')
    return value

class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, url):
        if not url.startswith('https://'): raise ValueError('HTTPS required')
        return super().redirect_request(req, fp, code, msg, headers, url)

def download(url, path, limit):
    if not isinstance(url,str) or not url.startswith('https://'): raise ValueError('HTTPS required')
    opener = urllib.request.build_opener(HttpsRedirect)
    with opener.open(url, timeout=60) as source, open(path,'xb') as target:
        total = 0
        while chunk := source.read(1024*1024):
            total += len(chunk)
            if total > limit: raise ValueError('download too large')
            target.write(chunk)

def extract(source, runtime, kind, destination, archive):
    if source == 'officialStable':
        descriptor_path = str(destination) + '.json'
        suffix = ('user-server/' if kind == 'user-server' else '') + runtime + '.json'
        download('https://downloads.relaxkon.com/relaxkonos/stable/latest/' + suffix, descriptor_path, 1024*1024)
        descriptor = load(Path(descriptor_path).read_text())
        if descriptor.get('schemaVersion') != 1 or descriptor.get('runtime') != runtime or descriptor.get('packageKind') != kind or not re.fullmatch('[0-9a-fA-F]{64}',descriptor.get('sha256','')): raise ValueError('descriptor')
        archive = str(destination) + '.zip'
        download(descriptor['url'],archive,8*1024**3)
        with open(archive,'rb') as stream: digest = stream_digest(stream)
        if digest != descriptor['sha256'].lower(): raise ValueError('official checksum mismatch')
    path = Path(archive)
    if not path.is_file() or path.is_symlink(): raise ValueError('ZIP file missing or unsafe')
    destination = Path(destination)
    if destination.exists() or destination.is_symlink(): raise ValueError('destination exists')
    with zipfile.ZipFile(path) as package:
        entries = package.infolist()
        if len(entries) > 20000 or sum(e.file_size for e in entries) > 8*1024**3: raise ValueError('package limits')
        if shutil.disk_usage(destination.parent).free < sum(e.file_size for e in entries) + 64*1024**2:
            raise OSError(errno.ENOSPC, 'Insufficient space for extracted package')
        seen = set()
        for entry in entries:
            name = entry.filename.rstrip('/')
            if not re.fullmatch(r'[A-Za-z0-9._/+\-]+',name) or any(p in ('','.', '..') for p in name.split('/')) or name.startswith('/') or name.casefold() in seen: raise ValueError('unsafe/duplicate ZIP path')
            seen.add(name.casefold())
            mode = (entry.external_attr >> 16) & 0o170000
            if mode not in (0, stat.S_IFREG,stat.S_IFDIR): raise ValueError('unsupported ZIP entry')
        manifest_entry = package.getinfo('manifest.json')
        if manifest_entry.file_size > 1024*1024: raise ValueError('manifest too large')
        manifest = load(package.read(manifest_entry))
        if manifest.get('schemaVersion') != 1 or manifest.get('runtime') != runtime or manifest.get('packageKind') != kind or not re.fullmatch(r'[0-9A-Za-z][0-9A-Za-z._-]{0,63}',manifest.get('version','')): raise ValueError('package kind/runtime/version')
        if source == 'officialStable' and manifest['version'] != descriptor.get('version'): raise ValueError('official version mismatch')
        required = ['payload/linux/server/RelaxKonOS.Server','payload/linux/guardian/RelaxKonOS.Guardian.Agent']
        if kind == 'server': required += ['payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper','deployment/bootstrap/install-relaxkonos.sh','deployment/linux/install-relaxkonos-services.sh']
        else: required += ['deployment/user/relaxkon']
        files = {e.filename:e for e in entries if not e.is_dir()}
        if any(name not in files for name in required): raise ValueError('incomplete package')
        if source == 'officialStable':
            listed = manifest.get('files',[])
            if len({f['path'] for f in listed}) != len(listed) or set(files) != {'manifest.json'} | {f['path'] for f in listed}: raise ValueError('file inventory')
            for item in listed:
                entry = files[item['path']]
                with package.open(entry) as stream: digest = stream_digest(stream)
                if entry.file_size != item['length'] or digest != item['sha256'].lower(): raise ValueError('file checksum')
        destination.mkdir(mode=0o700)
        try:
            for name,entry in files.items():
                target = destination / name
                target.parent.mkdir(parents=True,exist_ok=True)
                with package.open(entry) as src, target.open('xb') as dst: shutil.copyfileobj(src,dst)
                target.chmod(0o700 if name.endswith('.sh') or name in required else 0o600)
        except BaseException:
            shutil.rmtree(destination, ignore_errors=True)
            raise

try:
    action, *args = sys.argv[1:]
    if action == 'validate': request(args[0])
    elif action == 'text':
        value = request(args[0]); key = args[1]
        item = value.get(key, (value.get('options') or {}).get(key))
        print(item if isinstance(item,str) else '',end='')
    elif action == 'token':
        value = request(args[0]); key = args[1]
        print(json.dumps(value.get(key, (value.get('options') or {}).get(key)),separators=(',',':')),end='')
    elif action == 'extract': extract(*args)
    elif action == 'recover': recovery_state()
    else: raise ValueError('unsupported helper action')
except Exception as error:
    print('Deployment input rejected: ' + str(error),file=sys.stderr)
    sys.exit(73 if isinstance(error, OSError) and error.errno == errno.EDQUOT else
             74 if isinstance(error, OSError) and error.errno == errno.ENOSPC else 1)
PY
}

request_text=
read_request() {
  [[ -f $request_path && ! -L $request_path ]] || launcher_fail invalid_request "request file is missing"
  local size
  size=$(stat -c %s -- "$request_path")
  (( size > 0 && size <= 65536 )) || launcher_fail invalid_request "request file size is out of range"
  request_text=$(<"$request_path")
  [[ $request_text != *$'\n'* ]] || launcher_fail invalid_request "request must be a single line of JSON"
  [[ $request_text == \{*\} ]] || launcher_fail invalid_request "request must be a JSON object"
  command -v python3 >/dev/null || launcher_fail not_supported "Python 3 is required on the Linux server for JSON and ZIP handling"
  deployment_python validate "$request_path" >/dev/null 2>&1 \
    || launcher_fail invalid_request "request fields, types or JSON structure are invalid"
}
json_token() {
  deployment_python token "$request_path" "$1"
}
json_text() { deployment_python text "$request_path" "$1"; }
json_literal() { local token; token=$(json_token "$1"); printf '%s' "${token:-null}"; }

# A client may only send the fields of ServerDeploymentRequest/ServerDeploymentOptions. Anything
# else is rejected outright instead of being ignored, so a client cannot smuggle in a path, a
# command or a service name through an unrecognised key.
assert_request_keys() {
  local keys key
  keys=$(printf '%s' "$request_text" | grep -oE '"[A-Za-z][A-Za-z0-9]*"[[:space:]]*:' | sed -E 's/^"([^"]+)".*/\1/' | sort -u || true)
  while IFS= read -r key; do
    [[ -n $key ]] || continue
    case "$key" in
      schemaVersion|operationId|kind|options|source|network|retention|mode|version|packageUri|stagedPackageName|packageDigest|remotePackagePath|expectedInstallationId|serverPort|fileAccess|certificateMode|selfSignedIdentities|confirmed) ;;
      *) launcher_fail invalid_request "unsupported request field: $key" ;;
    esac
  done <<< "$keys"
}

request_digest() { printf '%s' "$request_text" | sha256sum | cut -d' ' -f1; }

options_source=officialStable
options_network=loopback
options_retention=retain
options_mode=
options_version=
options_package_uri=
options_staged_name=
options_package_digest=
options_remote_path=
options_expected_installation_id=
options_server_port=
options_file_access=
options_certificate_mode=
options_self_signed_identities=
options_confirmed=false

parse_request() {
  assert_request_keys
  local schema kind operation_raw
  schema=$(json_literal schemaVersion)
  [[ $schema == "$protocol_version" ]] || launcher_fail unsupported_protocol_version "the launcher does not support protocol version ${schema}"
  operation_raw=$(json_text operationId)
  [[ $operation_raw =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || launcher_fail invalid_request "operationId must be a UUID"
  operation_id=${operation_raw,,}
  kind=$(json_text kind)
  case "$kind" in
    probe|install|upgrade|repair|uninstall|status|rollback) ;;
    '') launcher_fail unknown_action "request kind is missing" ;;
    *) launcher_fail unknown_action "unsupported action: $kind" ;;
  esac
  operation_kind=$kind

  local value
  value=$(json_text source); [[ -n $value ]] && options_source=$value
  value=$(json_text network); [[ -n $value ]] && options_network=$value
  value=$(json_text retention); [[ -n $value ]] && options_retention=$value
  options_mode=$(json_text mode)
  options_version=$(json_text version)
  options_package_uri=$(json_text packageUri)
  options_staged_name=$(json_text stagedPackageName)
  options_package_digest=$(json_text packageDigest)
  options_remote_path=$(json_text remotePackagePath)
  options_expected_installation_id=$(json_text expectedInstallationId)
  local port_raw; port_raw=$(json_literal serverPort); [[ $port_raw != null ]] && options_server_port=$port_raw
  options_file_access=$(json_text fileAccess)
  options_certificate_mode=$(json_text certificateMode)
  options_self_signed_identities=$(json_text selfSignedIdentities)
  local confirmed_raw; confirmed_raw=$(json_literal confirmed); [[ $confirmed_raw == true ]] && options_confirmed=true

  case "$options_source" in officialStable|localBundle|remoteBundle|directUrl) ;; *) launcher_fail invalid_request "unsupported package source" ;; esac
  case "$options_network" in loopback|lan) ;; *) launcher_fail invalid_request "unsupported network profile" ;; esac
  case "$options_retention" in retain|delete) ;; *) launcher_fail invalid_request "unsupported data retention policy" ;; esac
  case "${options_file_access:-unset}" in unset|restricted|full|whitelist) ;; *) launcher_fail invalid_request "unsupported file access scope" ;; esac
  case "${options_certificate_mode:-unset}" in unset|none|custom|selfSigned) ;; *) launcher_fail invalid_request "unsupported certificate mode" ;; esac
  case "${options_mode:-unset}" in unset|linuxSystem|linuxUser|windowsSystem) ;; *) launcher_fail invalid_request "unsupported installation mode" ;; esac
  if [[ -n $options_version ]]; then
    [[ $options_version =~ ^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$ && $options_version == *[0-9]* ]] || launcher_fail invalid_request "version is invalid"
  fi
  if [[ -n $options_package_digest ]]; then
    [[ $options_package_digest =~ ^[0-9a-fA-F]{64}$ ]] || launcher_fail invalid_request "packageDigest must be a SHA-256"
    options_package_digest=${options_package_digest,,}
  fi
  if [[ -n $options_staged_name ]]; then
    [[ $options_staged_name =~ ^[A-Za-z0-9._-]{1,128}\.zip$ && $options_staged_name != *..* ]] || launcher_fail path_not_allowed "stagedPackageName must be a bare zip file name"
  fi
  if [[ -n $options_expected_installation_id ]]; then
    [[ $options_expected_installation_id =~ ^rki-[0-9a-f]{32}$ ]] || launcher_fail invalid_request "expectedInstallationId is invalid"
  fi
  if [[ -n $options_server_port ]]; then
    [[ $options_server_port =~ ^[0-9]+$ ]] && (( options_server_port >= 1 && options_server_port <= 65535 )) || launcher_fail invalid_request "serverPort is out of range"
  fi
  if [[ -n $options_package_uri ]]; then
    [[ $options_package_uri =~ ^https://[^[:space:]]+$ ]] || launcher_fail invalid_request "packageUri must be an HTTPS URL"
  fi
  if [[ $options_retention == delete && $options_confirmed != true ]]; then
    launcher_fail confirmation_required "deleting data requires explicit confirmation"
  fi
  case "$operation_kind" in
    install|upgrade)
      [[ -n $options_mode ]] || launcher_fail invalid_request "installation mode is required"
      case "$options_source" in
      officialStable) ;;
      localBundle) [[ -n $options_staged_name ]] || launcher_fail invalid_request "a local ZIP name is required" ;;
      remoteBundle) [[ $options_remote_path == /* && $options_remote_path == *.zip ]] || launcher_fail invalid_request "an absolute server ZIP path is required" ;;
      *) launcher_fail invalid_request "unsupported installation source" ;;
    esac
      ;;
    repair|rollback|uninstall|status) [[ -n $options_mode ]] || launcher_fail invalid_request "installation mode is required";;
  esac
}

# --- host facts --------------------------------------------------------------------------------
user_state_root() { printf '%s/relaxkonos' "${XDG_STATE_HOME:-$HOME/.local/state}"; }
user_data_root() { printf '%s/relaxkonos' "${XDG_DATA_HOME:-$HOME/.local/share}"; }
system_data_root() { printf '/var/lib/relaxkonos'; }
system_install_root() { printf '/opt/relaxkonos'; }

state_field() { # install-state file key
  local file=$1 key=$2
  read_state "$file" | sed -nE "s/.*\"$key\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" | head -n1
}
state_flag() { # install-state file key
  local file=$1 key=$2
  read_state "$file" | sed -nE "s/.*\"$key\"[[:space:]]*:[[:space:]]*(true|false).*/\1/p" | head -n1
}
read_state() {
  if [[ -r $1 ]]; then cat -- "$1"
  elif [[ $sudo_requested == true && $1 == "$(system_data_root)/install-state.json" ]]; then
    run_privileged cat -- "$1" 2>/dev/null || true
  fi
  return 0
}
mode_install_state() {
  case "$1" in
    linuxSystem|windowsSystem) printf '%s/install-state.json' "$(system_data_root)";;
    linuxUser) printf '%s/install-state.json' "$(user_state_root)";;
  esac
}
mode_install_root() { case "$1" in linuxSystem|windowsSystem) system_install_root;; linuxUser) user_data_root;; esac; }
mode_data_root() { case "$1" in linuxSystem|windowsSystem) system_data_root;; linuxUser) user_data_root;; esac; }
mode_listen_url() {
  state_field "$(mode_install_state "$1")" listenUrl
}
mode_service_names() {
  case "$1" in
    linuxSystem) printf '["relaxkonos-server.service","relaxkonos-guardian.service"]';;
    windowsSystem) printf '["RelaxKonOSServer","RelaxKonOSGuardian","RelaxKonOSPrivilegedHelper"]';;
    *) printf '[]';;
  esac
}
# The snapshot always carries the time it was verified; an offline cache must never be shown as
# live health, so the client is required to display this timestamp.
snapshot_json() { # mode stateFile healthy installed dataRetained
  printf '{"installationId":%s,"installed":%s,"mode":"%s","version":%s,"previousVersion":%s,"installRoot":%s,"dataRoot":%s,"listenUrl":%s,"healthy":%s,"dataRetained":%s,"serviceNames":%s,"verifiedAtUtc":"%s"}' \
    "$(json_string_or_null "$(state_field "$2" installationId)")" "$4" "$1" \
    "$(json_string_or_null "$(state_field "$2" version)")" "$(json_string_or_null "$(state_field "$2" previousVersion)")" \
    "$(json_string_or_null "$(mode_install_root "$1")")" "$(json_string_or_null "$(mode_data_root "$1")")" \
    "$(json_string_or_null "$(mode_listen_url "$1")")" "$3" "$5" "$(mode_service_names "$1")" "$(now_utc)"
}
result_json() { # mode stateFile healthy dataRetained
  printf '{"installationId":%s,"mode":"%s","version":%s,"previousVersion":%s,"installRoot":%s,"dataRoot":%s,"listenUrl":%s,"healthy":%s,"dataRetained":%s,"dataCompatible":null,"serviceNames":%s,"completedAtUtc":"%s"}' \
    "$(json_string_or_null "$(state_field "$2" installationId)")" "$1" \
    "$(json_string_or_null "$(state_field "$2" version)")" "$(json_string_or_null "$(state_field "$2" previousVersion)")" \
    "$(json_string_or_null "$(mode_install_root "$1")")" "$(json_string_or_null "$(mode_data_root "$1")")" \
    "$(json_string_or_null "$(mode_listen_url "$1")")" "$3" "$4" "$(mode_service_names "$1")" "$(now_utc)"
}
health_probe() { # mode -> true|false
  local mode=$1
  command -v curl >/dev/null || { printf 'false'; return; }
  case "$mode" in
    linuxUser)
      local socket; socket="$(user_state_root)/run/server.sock"
      [[ -S $socket ]] || { printf 'false'; return; }
      curl --unix-socket "$socket" --fail --silent --max-time 4 http://localhost/ready >/dev/null 2>&1 && printf 'true' || printf 'false'
      ;;
    *)
      local url="http://127.0.0.1:${options_server_port:-5000}/healthz" arguments=(--fail --silent --max-time 4)
      local state; state="$(system_data_root)/install-state.json"
      if [[ -r $state || $sudo_requested == true ]]; then
        local listen; listen=$(state_field "$state" listenUrl)
        [[ -n $listen ]] && url="${listen%/}/healthz"
        [[ $url == https://* ]] && arguments+=(--insecure)
      fi
      curl "${arguments[@]}" "$url" >/dev/null 2>&1 && printf 'true' || printf 'false'
      ;;
  esac
}
existing_installation_state() {
  local candidate file
  for candidate in linuxSystem linuxUser; do
    file=$(mode_install_state "$candidate")
    [[ -r $file || $sudo_requested == true && $candidate == linuxSystem ]] || continue
    if [[ $(state_flag "$file" installed) == true ]]; then printf '%s' "$file"; return; fi
  done
  for candidate in linuxSystem linuxUser; do
    file=$(mode_install_state "$candidate")
    [[ -r $file ]] && { printf '%s' "$file"; return; }
  done
  # A fresh host has no state file. Absence is a successful lookup with an empty result;
  # returning the last failed test would make `set -e` abort action_probe before its receipt.
  return 0
}

probe_json() {
  local machine runtime os_id= os_version= os_supported=false
  machine=$(uname -m)
  case "$machine" in x86_64|amd64) runtime=linux-x64;; aarch64|arm64) runtime=linux-arm64;; *) runtime=;; esac
  if [[ -r /etc/os-release ]]; then
    os_id=$(sed -nE 's/^ID="?([^"]*)"?$/\1/p' /etc/os-release | head -n1)
    os_version=$(sed -nE 's/^VERSION_ID="?([^"]*)"?$/\1/p' /etc/os-release | head -n1)
  fi
  case "$os_id-$os_version" in
    debian-12|ubuntu-22.04|ubuntu-24.04|ubuntu-26.04) os_supported=true;;
  esac
  local elevated=false; [[ $EUID -eq 0 ]] && elevated=true
  local sudo_available=false
  command -v sudo >/dev/null && sudo_available=true
  local systemd_available=false
  command -v systemctl >/dev/null && [[ -d /run/systemd/system ]] && systemd_available=true
  local disk
  disk=$(df -Pk -- "$(mode_install_root "${options_mode:-linuxSystem}")" 2>/dev/null | awk 'NR==2 {print $4}' || true)
  [[ $disk =~ ^[0-9]+$ ]] && disk=$((disk * 1024)) || disk=

  local missing='[]'
  if [[ ${options_mode:-} == linuxSystem ]]; then
    local dependencies=() tool
    for tool in systemctl sudo visudo openssl curl unzip; do
      command -v "$tool" >/dev/null || dependencies+=("\"$tool\"")
    done
    ((${#dependencies[@]} == 0)) || missing="[$(IFS=,; printf '%s' "${dependencies[*]}")]"
  fi

  local existing_file installed=false
  existing_file=$(existing_installation_state)
  [[ -n $existing_file && $(state_flag "$existing_file" installed) == true ]] && installed=true
  local existing_mode=
  if [[ -n $existing_file ]]; then
    existing_mode=$(state_field "$existing_file" mode)
    # A state file that predates the mode field, or one written by a foreign layout, still has to
    # report the mode the client must use, so fall back to the root the state file was found under.
    if [[ -z $existing_mode ]]; then
      case "$existing_file" in
        "$(system_data_root)/install-state.json") existing_mode=linuxSystem;;
        "$(user_state_root)/install-state.json") existing_mode=linuxUser;;
      esac
    fi
  fi
  local port_available=null
  if [[ -n $options_server_port ]]; then
    port_available=true
    if (exec 3<>"/dev/tcp/127.0.0.1/$options_server_port") 2>/dev/null; then port_available=false; exec 3<&- 3>&- 2>/dev/null || true; fi
  fi

  printf '{"hostPlatform":"linux","architecture":"%s","runtimeIdentifier":%s,"osId":%s,"osVersion":%s,"osSupported":%s,"elevated":%s,"sudoAvailable":%s,"systemdAvailable":%s,"diskAvailableBytes":%s,"requestedPort":%s,"requestedPortAvailable":%s,"existingInstallationId":%s,"existingMode":%s,"existingVersion":%s,"existingInstalled":%s,"missingDependencies":%s,"verifiedAtUtc":"%s"}' \
    "$machine" "$(json_string_or_null "$runtime")" "$(json_string_or_null "$os_id")" "$(json_string_or_null "$os_version")" \
    "$os_supported" "$elevated" "$sudo_available" "$systemd_available" "$(json_number_or_null "$disk")" \
    "$(json_number_or_null "$options_server_port")" "$port_available" \
    "$(json_string_or_null "$(state_field "$existing_file" installationId)")" "$(json_string_or_null "$existing_mode")" \
    "$(json_string_or_null "$(state_field "$existing_file" version)")" "$installed" "$missing" "$(now_utc)"
}

# --- lock and idempotency ----------------------------------------------------------------------
# One write operation per account at a time. The lock and records live outside ephemeral staging
# and survive uninstall, so another client and a reconnect observe the same state.
acquire_write_lock() {
  command -v flock >/dev/null || launcher_fail not_supported "flock is required for safe deployment operations"
  exec 8>"$lock_path"
  chmod 600 -- "$lock_path" 2>/dev/null || true
  flock -n 8 || launcher_fail write_lock_held "another deployment operation is already running on this host"
}
check_idempotency() {
  local digest_file; digest_file=$(digest_path)
  [[ -f $digest_file ]] || return 0
  local existing_digest current_digest
  existing_digest=$(<"$digest_file")
  current_digest=$(request_digest)
  [[ $existing_digest == "$current_digest" ]] || launcher_fail idempotency_conflict "operationId was already used for a different request" 2 false
  # Same operation, same request: replay the recorded outcome instead of acting twice.
  [[ -f $(record_path) ]] || launcher_fail recovery_unknown "the earlier operation has no durable receipt; inspect host state before retrying"
  cat -- "$(record_path)"
  exit 0
}
persist_digest() { umask 077; request_digest > "$(digest_path)"; chmod 600 -- "$(digest_path)"; }

# --- engine ------------------------------------------------------------------------------------
# The launcher maps a fixed action onto the existing deployment engine. It never passes a caller
# supplied path, service name or command; only the package directory it staged itself.
require_package() {
  local archive= architecture kind
  case "$(uname -m)" in
    x86_64) architecture=linux-x64;;
    aarch64) architecture=linux-arm64;;
    *) launcher_fail package_runtime_mismatch "this Linux architecture is unsupported";;
  esac
  case "$options_mode" in linuxUser) kind=user-server;; *) kind=server;; esac
  case "$options_source" in
    localBundle) archive=$staging_root/$options_staged_name;;
    remoteBundle) archive=$options_remote_path;;
  esac
  package_root=$staging_root/package-$operation_id
  local extract_status=0
  deployment_python extract "$options_source" "$architecture" "$kind" "$package_root" "$archive" >>"$(diagnostics_path)" 2>&1 || extract_status=$?
  case "$extract_status" in
    0) ;;
    73) launcher_fail disk_quota_exceeded "服务器当前 SSH 用户的存储配额已耗尽，请清理本应用的安装暂存文件或调整配额后重试。";;
    74) launcher_fail disk_space_insufficient "服务器安装暂存分区空间不足，请释放空间后重试。";;
    *) launcher_fail package_manifest_invalid "the release could not be downloaded, checked or safely extracted";;
  esac
}

cleanup_staged_payload() {
  # Keep account-wide receipts and logs; remove only payloads from this validated staging directory.
  [[ $staging_root =~ ^/tmp/relaxkonos-deploy\.[A-Za-z0-9]{8,32}$ && -O $staging_root && ! -L $staging_root ]] || return 0
  rm -rf -- "$staging_root/package-$operation_id"
  rm -f -- "$staging_root/package-$operation_id.zip" "$staging_root/package-$operation_id.json" \
    "$staging_root/certificate.pfx" "$staging_root/certificate-password.txt"
  if [[ $options_source == localBundle && $options_staged_name == server.zip ]]; then
    rm -f -- "$staging_root/server.zip"
  fi
}

user_engine_path() {
  [[ -x $package_root/deployment/user/relaxkon ]] && { printf '%s/deployment/user/relaxkon' "$package_root"; return; }
  local installed; installed="$(user_data_root)/server/current/user/relaxkon"
  [[ -x $installed ]] && { printf '%s' "$installed"; return; }
  launcher_fail not_supported "no User Mode deployment engine is available on this host"
}
system_engine_is_executable() {
  # System installation scripts may be root-readable only. Resolve them with the
  # same authenticated privileges that run_engine will use, not the SSH user's.
  if [[ $sudo_requested == true && $options_mode == linuxSystem && $EUID -ne 0 ]]; then
    run_privileged test -f "$1" && run_privileged test -x "$1"
  else
    [[ -f $1 && -x $1 ]]
  fi
}
system_engine_path() {
  [[ -x $package_root/deployment/bootstrap/install-relaxkonos.sh ]] && { printf '%s/deployment/bootstrap/install-relaxkonos.sh' "$package_root"; return; }
  # The engine publishes its own deployment scripts beside the installation, so repair and rollback
  # work over SSH without re-uploading a package.
  local installed="$(system_install_root)/current/deployment/bootstrap/install-relaxkonos.sh"
  system_engine_is_executable "$installed" && { printf '%s' "$installed"; return; }
  launcher_fail not_supported "no System Mode deployment engine is available on this host"
}
system_uninstall_engine_path() {
  [[ -x $package_root/deployment/bootstrap/uninstall-relaxkonos.sh ]] && { printf '%s/deployment/bootstrap/uninstall-relaxkonos.sh' "$package_root"; return; }
  local installed="$(system_install_root)/current/deployment/bootstrap/uninstall-relaxkonos.sh"
  system_engine_is_executable "$installed" && { printf '%s' "$installed"; return; }
  launcher_fail not_supported "no System Mode uninstall engine is available on this host"
}

run_engine() { # command...
  local diagnostics; diagnostics=$(diagnostics_path)
  umask 077
  launcher_note "running deployment engine: $1"
  set +e
  if [[ $sudo_requested == true && $options_mode == linuxSystem && $EUID -ne 0 ]]; then
    run_privileged "$@" > "$diagnostics" 2>&1
  else
    "$@" > "$diagnostics" 2>&1
  fi
  local status=$?
  set -e
  printf '\nDeployment engine exit status: %s\n' "$status" >> "$diagnostics"
  chmod 600 -- "$diagnostics" 2>/dev/null || true
  return $status
}

# --- preflight ---------------------------------------------------------------------------------
require_expected_installation_id() {
  [[ -n $options_expected_installation_id ]] || return 0
  local file actual
  file=$(mode_install_state "$options_mode")
  actual=$(state_field "$file" installationId)
  [[ -n $actual ]] || launcher_fail not_installed "the host has no managed installation to act on"
  [[ $actual == "$options_expected_installation_id" ]] || launcher_fail installation_id_mismatch "the host installation id does not match the request"
}
preflight_install() {
  local file installed
  file=$(mode_install_state "$options_mode")
  installed=$(state_flag "$file" installed)
  case "$operation_kind" in
    install) [[ $installed != true ]] || launcher_fail already_installed "RelaxKonOS is already installed on this host; use upgrade or repair";;
    repair) [[ $installed == true || $options_mode == linuxSystem ]] || launcher_fail not_installed "RelaxKonOS is not installed on this host";;
    upgrade|rollback|uninstall) [[ $installed == true ]] || launcher_fail not_installed "RelaxKonOS is not installed on this host";;
  esac
  case "$options_mode" in
    linuxSystem)
      [[ $EUID -eq 0 || $sudo_requested == true ]] || launcher_fail elevation_required "System Mode requires root or authenticated sudo access"
      [[ $(sed -nE 's/^ID="?([^"]*)"?$/\1/p' /etc/os-release 2>/dev/null | head -n1) =~ ^(debian|ubuntu)$ ]] || launcher_fail os_unsupported "this Linux distribution is not supported for System Mode"
      ;;
    linuxUser) [[ $EUID -ne 0 ]] || launcher_fail elevation_required "User Mode must not run as root";;
    *) launcher_fail not_supported "the Windows System Mode engine is not available on a Linux host";;
  esac
  [[ -n $options_server_port ]] || return 0
  if (exec 3<>"/dev/tcp/127.0.0.1/$options_server_port") 2>/dev/null; then
    exec 3<&- 3>&- 2>/dev/null || true
    if [[ $operation_kind == install || $operation_kind == upgrade ]]; then launcher_fail port_unavailable "the requested port is already in use"; fi
  fi
}

# --- actions -----------------------------------------------------------------------------------
action_probe() {
  emit_event validatingRequest running 5 "" "正在校验请求"
  local probe; probe=$(probe_json)
  installation_id=$(existing_installation_state)
  installation_id=$([[ -n $installation_id ]] && state_field "$installation_id" installationId || printf '')
  record_probe=$probe
  emit_event preflight running "" "" "正在读取宿主能力"
  started_at=$(now_utc); completed_at=$started_at
  emit_event completed succeeded 100 "" "宿主预检完成"
}

action_status() {
  emit_event validatingRequest running 5 "" "正在校验请求"
  local file installed healthy
  file=$(mode_install_state "$options_mode")
  installed=$(state_flag "$file" installed); [[ -n $installed ]] || installed=false
  installation_id=$(state_field "$file" installationId)
  emit_event preflight running "" "" "正在核验服务状态"
  healthy=$(health_probe "$options_mode")
  [[ $installed == true ]] || healthy=false
  record_snapshot=$(snapshot_json "$options_mode" "$file" "$healthy" "$installed" null)
  record_result=$(result_json "$options_mode" "$file" "$healthy" null)
  started_at=$(now_utc); completed_at=$started_at
  emit_event completed succeeded 100 "" "状态查询完成"
}

action_install_like() {
  emit_event validatingRequest running 5 "" "正在校验请求"
  emit_event acquiringLock running "" "" "正在获取独占操作锁"
  acquire_write_lock
  emit_event preflight running "" "" "正在执行权限与宿主预检"
  preflight_install
  require_expected_installation_id
  # Only install and upgrade consume a staged package; repair and rollback replay the payload the
  # engine already published on the host.
  case "$operation_kind" in
    install|upgrade)
      emit_event verifyingPackage running "" "" "正在校验暂存包"
      require_package
      ;;
  esac
  emit_event activating running "" "" "正在执行部署动作"

  local status=0 engine arguments
  local package_check_args=()
  [[ $options_source == officialStable ]] || package_check_args+=(--skip-file-checks)
  case "$options_mode" in
    linuxUser)
      engine=$(user_engine_path)
      case "$operation_kind" in
        install) run_engine bash "$engine" install --bundle "$package_root" "${package_check_args[@]}" || status=$? ;;
        upgrade) run_engine bash "$engine" upgrade --bundle "$package_root" "${package_check_args[@]}" || status=$? ;;
        repair) run_engine bash "$engine" repair || status=$? ;;
        rollback) run_engine bash "$engine" rollback || status=$? ;;
      esac
      ;;
    linuxSystem)
      if [[ $operation_kind == repair && $(state_flag "$(mode_install_state "$options_mode")" installed) != true ]]; then
        run_engine bash "$staging_root/relaxkonos-deploy.sh" --recover-state || status=$?
      elif [[ $operation_kind == rollback ]]; then
        engine=$(system_engine_path)
        run_engine bash "$engine" --mode system --action rollback --non-interactive || status=$?
      else
        engine=$(system_engine_path)
        arguments=(bash "$engine" --mode system --non-interactive)
        # Upgrade and repair continue an existing installation; only install and upgrade consume the
        # staged package, so repair and rollback still work when no package was uploaded.
        case "$operation_kind" in
          install|upgrade)
            arguments+=(--bundle "$package_root" --action "$operation_kind" "${package_check_args[@]}")
            case "$options_network" in lan) arguments+=(--network lan);; *) arguments+=(--network local);; esac
            [[ -n $options_server_port ]] && arguments+=(--server-port "$options_server_port")
            if [[ -n $options_file_access ]]; then
              arguments+=(--file-access "$options_file_access" --administrator-file-access "$options_file_access" --root-file-access "$options_file_access")
            fi
            if [[ $options_certificate_mode == custom ]]; then
              [[ -f $staging_root/certificate.pfx && -f $staging_root/certificate-password.txt ]] || launcher_fail invalid_request "custom certificate files are unavailable"
              arguments+=(--certificate-mode custom --certificate-path "$staging_root/certificate.pfx" --certificate-password-file "$staging_root/certificate-password.txt")
            fi
            if [[ $options_certificate_mode == selfSigned ]]; then
              [[ -n $options_self_signed_identities ]] || launcher_fail invalid_request "self-signed certificate names are required"
              arguments+=(--certificate-mode self-signed --self-signed-identities "$options_self_signed_identities")
            fi
            ;;
          repair)
            arguments+=(--action repair)
            if [[ $options_certificate_mode == selfSigned ]]; then
              [[ -n $options_self_signed_identities ]] || launcher_fail invalid_request "self-signed certificate names are required"
              grep -Fq '"$CERTIFICATE_MODE_SET" == true && "$CERTIFICATE_MODE" == self-signed' "$engine" || launcher_fail not_supported "installed deployment scripts cannot regenerate certificates during repair; upgrade the server first"
              arguments+=(--certificate-mode self-signed --self-signed-identities "$options_self_signed_identities")
            fi
            ;;
        esac
        run_engine "${arguments[@]}" || status=$?
      fi
      ;;
  esac

  if ((status != 0)); then
    completed_at=$(now_utc)
    emit_event failed failed "" failed "部署动作未成功，请查看操作记录"
    exit 1
  fi

  local file installed healthy
  file=$(mode_install_state "$options_mode")
  installed=$(state_flag "$file" installed); [[ -n $installed ]] || installed=false
  installation_id=$(state_field "$file" installationId)
  emit_event healthChecking running "" "" "正在核验 loopback 健康"
  healthy=$(health_probe "$options_mode")
  if [[ $installed != true || $healthy != true ]]; then
    completed_at=$(now_utc)
    emit_event failed failed "" health_check_failed "服务未通过健康核验"
    exit 1
  fi
  record_snapshot=$(snapshot_json "$options_mode" "$file" "$healthy" "$installed" null)
  record_result=$(result_json "$options_mode" "$file" "$healthy" null)
  emit_event finalizing running "" "" "正在整理部署结果"
  completed_at=$(now_utc)
  emit_event completed succeeded 100 "" "部署动作完成"
}

action_uninstall() {
  emit_event validatingRequest running 5 "" "正在校验请求"
  emit_event acquiringLock running "" "" "正在获取独占操作锁"
  acquire_write_lock
  emit_event preflight running "" "" "正在执行权限与宿主预检"
  preflight_install
  require_expected_installation_id
  emit_event snapshotting running "" "" "正在确认可保留的数据范围"
  emit_event stopping running "" "" "正在停止服务"

  local status=0 engine retained=true
  case "$options_mode" in
    linuxUser)
      engine=$(user_engine_path)
      if [[ $options_retention == delete ]]; then
        run_engine bash "$engine" uninstall --delete-data --confirm-delete-data || status=$?
        retained=false
      else
        run_engine bash "$engine" uninstall --retain-data || status=$?
      fi
      ;;
    linuxSystem)
      engine=$(system_uninstall_engine_path)
      if [[ $options_retention == delete ]]; then
        run_engine bash "$engine" --non-interactive --remove-data || status=$?
        retained=false
      else
        run_engine bash "$engine" --non-interactive || status=$?
      fi
      ;;
  esac
  if ((status != 0)); then
    completed_at=$(now_utc)
    emit_event failed failed "" uninstall_incomplete "卸载动作未完成，安装状态未被静默清除"
    exit 1
  fi

  local file installed
  file=$(mode_install_state "$options_mode")
  installed=$(state_flag "$file" installed); [[ -n $installed ]] || installed=false
  if [[ $installed == true ]]; then
    completed_at=$(now_utc)
    emit_event failed failed "" uninstall_incomplete "卸载后仍检测到受管安装"
    exit 1
  fi
  # With retained data the receipt stays on the host so a reinstall can reuse the installation id.
  installation_id=$(state_field "$file" installationId)
  emit_event finalizing running "" "" "正在整理卸载结果"
  completed_at=$(now_utc)
  record_result=$(result_json "$options_mode" "$file" false "$retained")
  emit_event completed succeeded 100 "" "卸载完成"
}

# --- entry -------------------------------------------------------------------------------------
case "${1:-}" in
  --clear-operation)
    operation_id=${2:-}
    [[ $operation_id =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || exit 64
    operation_id=${operation_id,,}
    ensure_journal
    exec 8>"$lock_path"
    flock -n 8 || exit 75
    [[ -f $(record_path) && ! -L $(record_path) ]] || exit 66
    if [[ -f $(record_path) ]]; then
      python3 - "$(record_path)" "$operation_id" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as f:
    receipt = json.load(f)
if receipt.get('operationId') != sys.argv[2] or receipt.get('state') not in ('succeeded', 'failed', 'cancelled', 'interrupted'):
    sys.exit(65)
PY
      [[ $? == 0 ]] || exit 65
    fi
    # Keep the request digest to prevent a cleared operation from executing again.
    rm -f -- "$(diagnostics_path)" "$(events_path)" "$(record_path)"
    exit $?
    ;;
  --recover-state)
    deployment_python recover
    exit $?
    ;;
  --diagnostics)
    operation_id=${2:-}
    [[ $operation_id =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || exit 64
    operation_id=${operation_id,,}
    ensure_journal
    [[ -f $(record_path) ]] || exit 66
    [[ -f $(diagnostics_path) && ! -L $(diagnostics_path) ]] || exit 0
    head -c 65536 -- "$(diagnostics_path)"
    exit 0
    ;;
  --query)
    operation_id=${2:-}
    [[ $operation_id =~ ^[0-9a-fA-F-]{36}$ ]] || { launcher_note "usage: $0 --query OPERATION_ID"; exit 64; }
    operation_id=${operation_id,,}
    ensure_journal
    [[ -f $(record_path) ]] || { launcher_note "operation not found: $operation_id"; exit 66; }
    cat -- "$(record_path)"
    exit 0
    ;;
  --list)
    ensure_journal
    [[ -d $operations_root ]] || exit 0
    find "$operations_root" -maxdepth 1 -name '*.json' -printf '%T@ %f\n' 2>/dev/null | sort -rn | head -n 20 | cut -d' ' -f2- || true
    exit 0
    ;;
  ''|--run) ;;
  --run-with-sudo)
    sudo_requested=true
    IFS= read -r sudo_password || true
    ;;
  *) launcher_note "usage: $0 [--run|--query OPERATION_ID|--list]"; exit 64;;
esac

ensure_journal
read_request
parse_request
trap cleanup_staged_payload EXIT
check_idempotency
started_at=$(now_utc)
persist_digest
authenticate_sudo
case "$operation_kind" in
  probe) action_probe;;
  status) action_status;;
  install|upgrade|repair|rollback) action_install_like;;
  uninstall) action_uninstall;;
esac
exit 0
