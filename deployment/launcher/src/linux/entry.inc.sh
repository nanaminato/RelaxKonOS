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
    deployment_python recover "${2:-/opt/relaxkonos}" "${3:-/var/lib/relaxkonos}"
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
