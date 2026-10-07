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

