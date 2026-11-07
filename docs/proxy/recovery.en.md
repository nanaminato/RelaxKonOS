# Proxy network recovery

On startup, RelaxKonOS evaluates any durable TUN recovery marker before allowing another activation. `Emergency disable TUN` restores the captured network state independently of runtime uninstall and leaves the marker in place if restoration fails.

When a direct Mihomo reload loses its acknowledgement, the Component Center runtime recovery panel shows the original operation ID. An operator with management permission and host elevation can retry. When the live result is unknown, the retry restores the previous configuration on disk only if the protected active file still matches the candidate digest. The operation remains pending because Mihomo may still be running the configuration it loaded.

If profile publication recorded the controller acknowledgement, startup recovery or a named retry verifies the candidate file and finishes the original profile commit. A subsequent retry can read the saved receipt. Changes to the candidate file or profile identity keep recovery pending. Emergency TUN disable and system proxy recovery continue to use their own durable records.

A queued refresh of an active subscription records the direct reload acknowledgement under the original refresh ID. After startup recovery commits the profile, both the refresh receipt and its original idempotency key refer to the same refresh. Recovery uses the converted configuration and the original subscription timestamp without downloading the subscription or reloading the controller again. If Server exits before the controller acknowledgement, the next lookup or submission uses the matching publication checkpoint to save the original entry as interrupted and awaiting publication recovery (`publication_pending`), with no completion time. The original key returns that entry and the write gate blocks new refreshes.

If a direct settings update recorded successful replies from both the runtime and the host, and the host proxy policy stays the same, recovery verifies the candidate configuration and settings file and finishes the local commit. An unknown result or an independent file edit leaves the original operation pending.

After the runtime acknowledges a direct host policy change on Linux, recovery can check the Helper receipt and current proxy values by the original operation ID. When both checks pass and the candidate file still matches, recovery finishes the local settings commit. If the guard must independently maintain the persisted policy, it leaves the original operation pending so the two effects remain distinguishable.

If the pending change moved the proxy listener, Mihomo may still serve the candidate port after the guard restores the saved proxy address. New connections to the old port may be refused while existing connections continue forwarding. The recovery panel keeps the original operation ID and pending status.

For direct Linux settings that the guard has taken over, an operator with management permission and host elevation can confirm **Restore saved proxy policy**. The application uses a separate recovery ID and verifies the saved listener and host proxy effect. Completion records the original operation as interrupted and links the recovery ID. TUN safety duties, file drift, or an unknown compensation reload leave recovery pending.

Use a second management connection on a disposable VM for the first platform validation. See the skipped-test register before attempting Windows or Ubuntu recovery drills.
