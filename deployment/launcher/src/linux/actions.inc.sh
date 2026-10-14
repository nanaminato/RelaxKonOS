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

action_firewall() {
  acquire_write_lock
  preflight_install
  require_expected_installation_id
  local file; file=$(mode_install_state "$options_mode")
  installation_id=$(state_field "$file" installationId)
  apply_firewall_choice "$file"
  record_result=$(result_json "$options_mode" "$file" "$(health_probe "$options_mode")" null)
  completed_at=$(now_utc)
  emit_event completed succeeded 100 "" "防火墙操作完成"
}

apply_firewall_choice() { # authoritative installation state
  firewall_status='"notRequested"'
  [[ $options_mode != linuxUser ]] || { firewall_status='"notApplicable"'; return; }
  local url port result
  url=$(state_field "$1" listenUrl)
  case "$url" in *://127.0.0.1:*|*://localhost:*) [[ ${options_remove_firewall:-null} == true ]] || { firewall_status='"notApplicable"'; return; };; esac
  port=$(python3 -c 'import sys,urllib.parse; print(urllib.parse.urlparse(sys.argv[1]).port)' "$url")
  local -a firewall_command=(python3)
  [[ $sudo_requested != true ]] || firewall_command=(run_privileged python3)
  # sudo consumes stdin for authentication; pass the Python source as an argument.
  if ! result=$("${firewall_command[@]}" -c "$(cat <<'FIREWALL_PY'
import json, os, pathlib, re, shutil, subprocess, sys
remove=len(sys.argv)>3 and sys.argv[3]=='true'
port=int(sys.argv[1]); requested=sys.argv[2]=='true'
if not 1 <= port <= 65535: raise ValueError('Invalid server port')
def run(*args, check=True, input=None):
    return subprocess.run(args, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=check, input=input, env={**os.environ, "LC_ALL": "C"})
def active(unit):
    return run('systemctl','is-active','--quiet',unit,check=False).returncode==0
ownership=pathlib.Path('/etc/relaxkonos/firewalld-owned.json')
def owned_rules():
    if not ownership.exists(): return []
    if ownership.is_symlink() or ownership.stat().st_uid!=0 or ownership.stat().st_mode & 0o022: raise ValueError('Untrusted firewall ownership record')
    data=json.loads(ownership.read_text())
    if data.get('owner')!='relaxkonos-server': raise ValueError('Foreign firewall ownership record')
    entries=data.get('rules',[])
    for item in entries:
        if set(item)!={'zone','rule','permanent'} or not re.fullmatch(r'[A-Za-z0-9_.-]+',item['zone']) or type(item['permanent']) is not bool or not re.fullmatch(r'rule family="ipv[46]" port port="[0-9]+" protocol="tcp" accept',item['rule']): raise ValueError('Invalid firewall ownership record')
    return entries
def save_owned(entries):
    ownership.parent.mkdir(mode=0o755,exist_ok=True)
    if ownership.parent.is_symlink() or ownership.parent.stat().st_uid!=0 or ownership.parent.stat().st_mode & 0o022: raise ValueError('Untrusted firewall ownership directory')
    import tempfile
    fd,name=tempfile.mkstemp(prefix='.firewalld-',dir=ownership.parent)
    try:
        with os.fdopen(fd,'w') as stream: json.dump({'owner':'relaxkonos-server','rules':entries},stream)
        os.replace(name,ownership)
    finally:
        if os.path.exists(name): os.unlink(name)
if remove:
    entries=owned_rules()
    if entries:
        if not shutil.which('firewall-cmd'): raise RuntimeError('Managed firewalld rules cannot be removed without firewalld')
        for item in entries:
            args=['firewall-cmd',f"--zone={item['zone']}"]
            if item['permanent']: args.append('--permanent')
            if run(*args,'--query-rich-rule='+item['rule'],check=False).returncode==0: run(*args,'--remove-rich-rule='+item['rule'])
        save_owned([])
    # Delete only rules carrying our exact product marker; never delete an unlabelled port allowance.
    if shutil.which('ufw'):
        lines=run('ufw','status','numbered').stdout.splitlines()
        owned=[]
        for line in lines:
            match=re.match(r'^\[\s*(\d+)\] .*# RelaxKonOS\s*$',line.strip())
            if match: owned.append(int(match[1]))
        for index in sorted(owned,reverse=True): run('ufw','--force','delete',str(index))
    if shutil.which('nft'):
        rules=json.loads(run('nft','-j','list','ruleset').stdout)['nftables']
        for entry in rules:
            rule=entry.get('rule',{})
            if re.fullmatch(r'RelaxKonOS TCP [0-9]+',rule.get('comment','')):
                run('nft','delete','rule',rule['family'],rule['table'],rule['chain'],'handle',str(rule['handle']))
        managed=pathlib.Path('/etc/relaxkonos/firewall.nft')
        if managed.exists():
            lines=managed.read_text().splitlines()
            remaining=[line for line in lines if not re.fullmatch(r'insert rule [A-Za-z0-9_.-]+ [A-Za-z0-9_.-]+ [A-Za-z0-9_.-]+ tcp dport [0-9]+ accept comment "RelaxKonOS TCP [0-9]+"',line)]
            managed.write_text('\n'.join(remaining)+'\n')
    if shutil.which('iptables'):
        import shlex
        for line in run('iptables','-S','INPUT').stdout.splitlines():
            args=shlex.split(line)
            if '--comment' in args and re.fullmatch(r'RelaxKonOS TCP [0-9]+',args[args.index('--comment')+1]):
                run('iptables','-D',*args[1:])
        if shutil.which('netfilter-persistent'): run('netfilter-persistent','save')
    # Firewalld's historical bare-port allowances lack ownership markers and are deliberately retained.
    print('rulesRemoved');sys.exit(0)
backend=None
if shutil.which('ufw') and re.search(r'^Status: active$',run('ufw','status').stdout,re.M): backend='ufw'
elif shutil.which('firewall-cmd') and run('firewall-cmd','--state',check=False).returncode==0: backend='firewalld'
elif shutil.which('nft'):
    rules=json.loads(run('nft','-j','list','ruleset').stdout)['nftables']
    chains=[o['chain'] for o in rules if 'chain' in o and o['chain'].get('hook')=='input' and o['chain'].get('family') in ('inet','ip','ip6')]
    if chains and (active('nftables') or any(c.get('policy') == 'drop' for c in chains) or any('rule' in o for o in rules)): backend='nftables'
if backend is None and shutil.which('iptables'):
    policy=run('iptables','-S','INPUT').stdout
    if '-P INPUT DROP' in policy or '-P INPUT REJECT' in policy or '-A INPUT ' in policy: backend='iptables'
if backend is None:
    print('disabled'); print('The host firewall is disabled; no firewall rule was added.',file=sys.stderr);sys.exit(0)
if not requested: print('notRequested');sys.exit(0)
if backend=='ufw': run('ufw','allow',f'{port}/tcp','comment','RelaxKonOS')
elif backend=='firewalld':
    zones=[line.strip() for line in run('firewall-cmd','--get-active-zones').stdout.splitlines() if line and not line[0].isspace()]
    if not zones: zones=[run('firewall-cmd','--get-default-zone').stdout.strip()]
    entries=owned_rules()
    for zone in zones:
        if not re.fullmatch(r'[A-Za-z0-9_.-]+',zone): raise ValueError('Invalid firewall zone')
        for family in ('ipv4','ipv6'):
            rule=f'rule family="{family}" port port="{port}" protocol="tcp" accept'
            for permanent in (True,False):
                args=['firewall-cmd',f'--zone={zone}']
                if permanent: args.append('--permanent')
                if run(*args,'--query-rich-rule='+rule,check=False).returncode==0: continue
                item={'zone':zone,'rule':rule,'permanent':permanent}
                if item not in entries:
                    entries.append(item);save_owned(entries)
                run(*args,'--add-rich-rule='+rule)
elif backend=='nftables':
    if not active('nftables') or not pathlib.Path('/etc/nftables.conf').is_file():
        raise RuntimeError('Active nftables rules have no managed persistent nftables service')
    tag=f'RelaxKonOS TCP {port}'
    commands=[]
    for c in chains:
        family,table,chain=c['family'],c['table'],c['name']
        if not all(re.fullmatch(r'[A-Za-z0-9_.-]+',x) for x in (family,table,chain)): raise ValueError('Unsupported nftables chain name')
        command=f'insert rule {family} {table} {chain} tcp dport {port} accept comment "{tag}"'
        commands.append(command)
    directory=pathlib.Path('/etc/relaxkonos');directory.mkdir(mode=0o755,exist_ok=True)
    managed=directory/'firewall.nft'
    prior=managed.read_text() if managed.exists() else ''
    for command in commands:
        if command not in prior.splitlines(): prior+=command+'\n'
    config=pathlib.Path('/etc/nftables.conf')
    include='include "/etc/relaxkonos/firewall.nft"'
    configText=config.read_text()
    if include not in configText.splitlines(): configText+='\n'+include+'\n'
    # Check the complete boot configuration before applying any new rule.
    import tempfile
    oldManaged=managed.read_bytes() if managed.exists() else None
    managed.write_text(prior)
    try:
        with tempfile.NamedTemporaryFile(mode='w',suffix='.nft') as temp:
            temp.write(configText);temp.flush();run('nft','--check','--file',temp.name)
    except Exception:
        if oldManaged is None: managed.unlink()
        else: managed.write_bytes(oldManaged)
        raise
    for c,command in zip(chains,commands):
        existing=run('nft','-j','list','chain',c['family'],c['table'],c['name']).stdout
        if not any(o.get('rule',{}).get('comment')==tag for o in json.loads(existing)['nftables']): run('nft','-f','-',input=command+'\n')
    config.write_text(configText)
elif backend=='iptables':
    if not shutil.which('netfilter-persistent'): raise RuntimeError('Active iptables rules have no netfilter-persistent save command')
    args=['INPUT','-p','tcp','--dport',str(port),'-m','comment','--comment',f'RelaxKonOS TCP {port}','-j','ACCEPT']
    if run('iptables','-C',*args,check=False).returncode: run('iptables','-I',*args)
    run('netfilter-persistent','save')
print('ruleAdded')
FIREWALL_PY
  )" "$port" "$options_add_firewall" "${options_remove_firewall:-false}" 2>>"$(diagnostics_path)"); then
    launcher_fail firewall_rule_failed 'The server is installed, but the requested firewall rule could not be added; inspect the host firewall configuration.'
  fi
  case "$result" in disabled|ruleAdded|notRequested|rulesRemoved) firewall_status=$(json_string_or_null "$result");; *) launcher_fail firewall_rule_failed 'Invalid firewall result';; esac
}

action_install_like() {
  emit_event validatingRequest running 5 "" "正在校验请求"
  emit_event acquiringLock running "" "" "正在获取独占操作锁"
  acquire_write_lock
  emit_event preflight running "" "" "正在执行权限与宿主预检"
  preflight_install
  require_expected_installation_id
  # Install and upgrade require a package; repair consumes one only when explicitly supplied.
  case "$operation_kind" in
    repair)
      if [[ -n $options_remote_path || -n $options_staged_name ]]; then require_package; fi
      ;;
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
      local user_env=(env "RELAXKONOS_PORT=${options_server_port:-$(state_field "$(mode_install_state linuxUser)" listenUrl | sed -nE 's/.*:([0-9]+)$/\1/p')}" "RELAXKONOS_USER_DATA_ROOT=$(user_data_root)" "RELAXKONOS_USER_STATE_ROOT=$(user_state_root)" "RELAXKONOS_USER_CONFIG_ROOT=$(user_config_root)" "RELAXKONOS_USER_CACHE_ROOT=$(user_cache_root)")
      [[ "$options_guardian_enabled" == null ]] || user_env+=("RELAXKONOS_GUARDIAN_ENABLED=$options_guardian_enabled")
      case "$operation_kind" in
        install) run_engine "${user_env[@]}" bash "$engine" install --port "${options_server_port:-5000}" --bundle "$package_root" "${package_check_args[@]}" || status=$? ;;
        upgrade) run_engine "${user_env[@]}" bash "$engine" upgrade --bundle "$package_root" "${package_check_args[@]}" || status=$? ;;
        configure) run_engine "${user_env[@]}" bash "$engine" configure --port "$options_server_port" --network "$options_network" || status=$? ;;
        repair) local user_repair_args=(); [[ -z $package_root || ! -d $package_root ]] || user_repair_args+=(--bundle "$package_root"); run_engine "${user_env[@]}" bash "$engine" repair "${user_repair_args[@]}" || status=$? ;;
        rollback) run_engine "${user_env[@]}" bash "$engine" rollback || status=$? ;;
      esac
      ;;
    linuxSystem)
      if [[ $operation_kind == repair && $(state_flag "$(mode_install_state "$options_mode")" installed) != true ]]; then
        run_engine bash "$staging_root/relaxkonos-deploy.sh" --recover-state "$(system_install_root)" "$(system_data_root)" || status=$?
      elif [[ $operation_kind == rollback ]]; then
        engine=$(system_engine_path)
        run_engine bash "$engine" --mode system --action rollback --non-interactive --install-root "$(system_install_root)" --data-root "$(system_data_root)" || status=$?
      else
        engine=$(system_engine_path)
        arguments=(bash "$engine" --mode system --non-interactive --language "$options_language" --install-root "$(system_install_root)" --data-root "$(system_data_root)")
        [[ "$options_auto_start" == null ]] || arguments+=(--auto-start "$options_auto_start")
        [[ "$options_guardian_enabled" == null ]] || arguments+=(--guardian-enabled "$options_guardian_enabled")
        [[ $options_allow_unsupported != true ]] || arguments+=(--allow-unsupported-system)
        # Upgrade and repair continue an existing installation; only install and upgrade consume the
        # staged package, so repair and rollback still work when no package was uploaded.
        case "$operation_kind" in
          install|upgrade)
            arguments+=(--bundle "$package_root" --action "$operation_kind" "${package_check_args[@]}")
            case "$options_network" in lan) arguments+=(--network lan);; *) arguments+=(--network local);; esac
            [[ -n $options_server_port ]] && arguments+=(--server-port "$options_server_port")
            if [[ -n $options_file_access ]]; then
              arguments+=(--file-access "$options_file_access")
            fi
            [[ -z $options_administrator_access ]] || arguments+=(--administrator-file-access "$options_administrator_access")
            [[ -z $options_root_access ]] || arguments+=(--root-file-access "$options_root_access")
            [[ $options_docker != true ]] || arguments+=(--docker-access)
            local roots_key flag scope
            for roots_key in fileRoots administratorFileRoots rootFileRoots; do
              case "$roots_key" in fileRoots) flag=--file-roots;; administratorFileRoots) flag=--administrator-file-roots;; rootFileRoots) flag=--root-file-roots;; esac
              write_roots_file "$roots_key"
              [[ ! -f $staging_root/$roots_key.txt ]] || arguments+=("$flag" "$staging_root/$roots_key.txt")
            done
            if [[ $options_certificate_mode == none ]]; then arguments+=(--certificate-mode none); fi
            if [[ $options_certificate_mode == custom ]]; then
              [[ -f $staging_root/certificate.pfx && -f $staging_root/certificate-password.txt ]] || launcher_fail invalid_request "custom certificate files are unavailable"
              arguments+=(--certificate-mode custom --certificate-path "$staging_root/certificate.pfx" --certificate-password-file "$staging_root/certificate-password.txt")
            fi
            if [[ $options_certificate_mode == selfSigned ]]; then
              [[ -n $options_self_signed_identities ]] || launcher_fail invalid_request "self-signed certificate names are required"
              arguments+=(--certificate-mode self-signed --self-signed-identities "$options_self_signed_identities")
            fi
            ;;
          configure)
            [[ -z $options_file_access ]] || arguments+=(--file-access "$options_file_access")
            write_roots_file fileRoots
            [[ ! -f $staging_root/fileRoots.txt ]] || arguments+=(--file-roots "$staging_root/fileRoots.txt")
            arguments+=(--action configure --server-port "$options_server_port")
            case "$options_network" in lan) arguments+=(--network lan);; *) arguments+=(--network local);; esac
            ;;
          repair)
            arguments+=(--action repair)
            [[ -z $package_root || ! -d $package_root ]] || arguments+=(--bundle "$package_root" --restore-payload)
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

  save_managed_roots
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
  apply_firewall_choice "$file"
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
      local user_env=(env "RELAXKONOS_USER_DATA_ROOT=$(user_data_root)" "RELAXKONOS_USER_STATE_ROOT=$(user_state_root)" "RELAXKONOS_USER_CONFIG_ROOT=$(user_config_root)" "RELAXKONOS_USER_CACHE_ROOT=$(user_cache_root)")
      if [[ $options_retention == delete ]]; then
        run_engine "${user_env[@]}" bash "$engine" uninstall --delete-data --confirm-delete-data || status=$?
        retained=false
      else
        run_engine "${user_env[@]}" bash "$engine" uninstall --retain-data || status=$?
      fi
      ;;
    linuxSystem)
      engine=$(system_uninstall_engine_path)
      local component_args=()
      [[ -z $options_remove_components ]] || component_args=(--remove-components "$options_remove_components")
      if [[ $options_retention == delete ]]; then
        run_engine bash "$engine" --install-root "$(system_install_root)" --data-root "$(system_data_root)" --non-interactive "${component_args[@]}" --remove-data || status=$?
        retained=false
      else
        run_engine bash "$engine" --install-root "$(system_install_root)" --data-root "$(system_data_root)" --non-interactive "${component_args[@]}" || status=$?
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

