# --- request -----------------------------------------------------------------------------------
# The Linux host uses its standard Python 3 runtime for JSON and safe ZIP handling.
# No RID-specific client executable is uploaded or required.
deployment_python() {
  python3 - "$@" <<'PY'
import errno, hashlib, json, os, re, shutil, stat, sys, urllib.request, zipfile
from pathlib import Path
import datetime, ssl, subprocess, uuid, tempfile, time

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
            'certificateMode','selfSignedIdentities','confirmed','language','releaseCatalogBaseUri','installRoot','dataRoot','configRoot','stateRoot','cacheRoot','fileRoots','administratorFileAccess','administratorFileRoots','rootFileAccess','rootFileRoots','dockerAccess','allowUnsupportedSystem','addFirewallRule','removeComponents'}
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
            if key in ('confirmed','dockerAccess','allowUnsupportedSystem','addFirewallRule'): valid = type(item) is bool
            elif key in ('fileRoots','administratorFileRoots','rootFileRoots'): valid = item is None or type(item) is list and len(item) <= 128 and all(type(p) is str and p.startswith('/') and not any(ord(c)<32 for c in p) for p in item)
            elif key == 'serverPort': valid = item is None or type(item) is int
            elif key in ('source','network','retention'): valid = type(item) is str
            else: valid = item is None or type(item) is str
            if not valid or isinstance(item,str) and any(ord(c)<32 for c in item): raise ValueError('option type')
    return value

class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, url):
        if not url.startswith('https://'): raise ValueError('HTTPS required')
        return super().redirect_request(req, fp, code, msg, headers, url)

def download(url, path, limit, request_path=None):
    if not isinstance(url,str) or not url.startswith('https://'): raise ValueError('HTTPS required')
    opener = urllib.request.build_opener(HttpsRedirect)
    with opener.open(url, timeout=60) as source, open(path,'xb') as target:
        total = 0
        length = source.headers.get('Content-Length')
        length = int(length) if length and length.isdigit() and 0 <= int(length) <= limit else None
        last_report = 0
        def report(force=False, active=True):
            nonlocal last_report
            now = time.monotonic()
            if request_path and (force or now - last_report >= 0.25):
                progress_path = Path(request_path).parent / 'transfer.json'
                temporary = progress_path.with_suffix('.tmp')
                try:
                    temporary.write_text(json.dumps({'operationId': request(request_path)['operationId'], 'bytes': total, 'total': length, 'active': active}), encoding='utf-8')
                    os.replace(temporary, progress_path)
                except OSError:
                    pass  # Advisory progress must never abort the package download.
                last_report = now
        report(True)
        while chunk := source.read(1024*1024):
            total += len(chunk)
            if total > limit: raise ValueError('download too large')
            target.write(chunk)
            report()
        report(True, False)

def extract(source, runtime, kind, destination, archive, request_path=None):
    options = (request(request_path).get('options') or {}) if request_path else {}
    if source == 'directUrl':
        archive = str(destination) + '.zip'
        download(options['packageUri'], archive, 8*1024**3, request_path)
        with open(archive,'rb') as stream: digest = stream_digest(stream)
        if digest != options['packageDigest'].lower(): raise ValueError('release checksum mismatch')
    if source == 'officialStable':
        descriptor_path = str(destination) + '.json'
        suffix = ('user-server/' if kind == 'user-server' else '') + runtime + '.json'
        download((options.get('releaseCatalogBaseUri') or 'https://downloads.relaxkon.com/relaxkonos/stable/latest').rstrip('/') + '/' + suffix, descriptor_path, 1024*1024)
        descriptor = load(Path(descriptor_path).read_text())
        if descriptor.get('schemaVersion') != 1 or descriptor.get('runtime') != runtime or descriptor.get('packageKind') != kind or not re.fullmatch('[0-9a-fA-F]{64}',descriptor.get('sha256','')): raise ValueError('descriptor')
        archive = str(destination) + '.zip'
        download(descriptor['url'],archive,8*1024**3,request_path)
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
        required = ['payload/linux/server/RelaxKonOS.Server','payload/linux/guardian/RelaxKonOS.Guardian.Agent','deployment/verify-release-inventory.py']
        if kind == 'server': required += ['payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper','deployment/bootstrap/install-relaxkonos.sh','deployment/bootstrap/uninstall-relaxkonos.sh','deployment/linux/install-relaxkonos-services.sh']
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
    elif action == 'recover': recovery_state(Path(args[0]), Path(args[1])) if args else recovery_state()
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
      schemaVersion|operationId|kind|options|source|network|retention|mode|version|packageUri|stagedPackageName|packageDigest|remotePackagePath|expectedInstallationId|serverPort|fileAccess|certificateMode|selfSignedIdentities|confirmed|language|releaseCatalogBaseUri|installRoot|dataRoot|configRoot|stateRoot|cacheRoot|fileRoots|administratorFileAccess|administratorFileRoots|rootFileAccess|rootFileRoots|dockerAccess|allowUnsupportedSystem|addFirewallRule|removeComponents) ;;
      *) launcher_fail invalid_request "unsupported request field: $key" ;;
    esac
  done <<< "$keys"
}

request_digest() { printf '%s' "$request_text" | sha256sum | cut -d' ' -f1; }

options_source=officialStable
options_add_firewall=false
firewall_status=null
options_network=loopback
options_retention=retain
options_remove_components=
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
options_language=auto
options_catalog=
options_install_root=
options_data_root=
options_config_root=
options_state_root=
options_cache_root=
options_administrator_access=
options_root_access=
options_docker=false
options_allow_unsupported=false

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
  options_remove_components=$(json_text removeComponents)
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
  options_language=$(json_text language); options_language=${options_language:-auto}
  options_catalog=$(json_text releaseCatalogBaseUri)
  options_install_root=$(json_text installRoot)
  options_data_root=$(json_text dataRoot)
  options_config_root=$(json_text configRoot)
  options_state_root=$(json_text stateRoot)
  options_cache_root=$(json_text cacheRoot)
  options_administrator_access=$(json_text administratorFileAccess)
  options_root_access=$(json_text rootFileAccess)
  [[ $(json_literal addFirewallRule) != true ]] || options_add_firewall=true
  if [[ $options_add_firewall == true && $kind != install && $kind != upgrade && $kind != repair ]]; then launcher_fail invalid_request "Firewall changes require install, upgrade or repair"; fi
  [[ $(json_literal dockerAccess) != true ]] || options_docker=true
  [[ $(json_literal allowUnsupportedSystem) != true ]] || options_allow_unsupported=true
  case "$options_language" in auto|zh-CN|en-US|ja-JP) ;; *) launcher_fail invalid_request "unsupported language" ;; esac
  [[ -z $options_catalog || $options_catalog =~ ^https://[^[:space:]]+$ ]] || launcher_fail invalid_request "HTTPS catalog required"
  local path scope
  for path in "$options_install_root" "$options_data_root" "$options_config_root" "$options_state_root" "$options_cache_root"; do
    [[ -z $path || $path == /* && $path != / && $path != *$'\n'* ]] || launcher_fail invalid_request "absolute non-root directories required"
  done
  for scope in "$options_administrator_access" "$options_root_access"; do
    case "${scope:-unset}" in unset|restricted|full|whitelist) ;; *) launcher_fail invalid_request "unsupported file access scope" ;; esac
  done
  local confirmed_raw; confirmed_raw=$(json_literal confirmed); [[ $confirmed_raw == true ]] && options_confirmed=true

  case "$options_source" in officialStable|localBundle|remoteBundle|directUrl) ;; *) launcher_fail invalid_request "unsupported package source" ;; esac
  case "$options_network" in loopback|lan) ;; *) launcher_fail invalid_request "unsupported network profile" ;; esac
  if [[ -n $options_remove_components ]]; then
    [[ $options_confirmed == true ]] || launcher_fail invalid_request "component removal requires confirmation"
    IFS=, read -ra selection <<< "$options_remove_components"
    declare -A seen_components=()
    for component in "${selection[@]}"; do
      case "$component" in smb|nginx|frp|mihomo) ;; *) launcher_fail invalid_request "invalid component selection" ;; esac
      [[ ! ${seen_components[$component]+present} ]] || launcher_fail invalid_request "duplicate component"
      seen_components[$component]=1
    done
    [[ $options_remove_components != *, && $operation_kind == uninstall ]] || launcher_fail invalid_request "invalid component selection"
    [[ $options_retention != delete || ${#selection[@]} == 4 ]] || launcher_fail invalid_request "retained components require retained data"
    [[ $options_mode != linuxUser ]] || launcher_fail invalid_request "user mode does not own system components"
  fi
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
      if [[ $options_mode == linuxUser ]]; then
        [[ $options_network == loopback && ${options_certificate_mode:-none} == none && $options_docker == false &&
           -z $options_install_root && ${options_file_access:-restricted} == restricted &&
           -z $options_administrator_access && -z $options_root_access ]] || launcher_fail invalid_request "system options are unavailable in User Mode"
      fi
      case "$options_source" in
      officialStable) ;;
      directUrl) [[ -n $options_package_uri && -n $options_package_digest ]] || launcher_fail invalid_request "HTTPS URL and SHA-256 required" ;;
      localBundle) [[ -n $options_staged_name ]] || launcher_fail invalid_request "a local ZIP name is required" ;;
      remoteBundle) [[ $options_remote_path == /* && $options_remote_path == *.zip ]] || launcher_fail invalid_request "an absolute server ZIP path is required" ;;
      *) launcher_fail invalid_request "unsupported installation source" ;;
    esac
      ;;
    repair|rollback|uninstall|status) [[ -n $options_mode ]] || launcher_fail invalid_request "installation mode is required";;
  esac
}

