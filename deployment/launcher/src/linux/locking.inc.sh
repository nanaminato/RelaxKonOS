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

