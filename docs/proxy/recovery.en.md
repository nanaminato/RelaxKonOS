# Proxy network recovery

On startup, RelaxKonOS evaluates any durable TUN recovery marker before allowing another activation. `Emergency disable TUN` restores the captured network state independently of runtime uninstall and leaves the marker in place if restoration fails.

When a direct Mihomo reload loses its acknowledgement, the Component Center runtime recovery panel shows the original operation ID. An operator with management permission and host elevation can retry. The retry restores the previous configuration on disk only if the protected active file still matches the candidate digest. The operation remains pending because Mihomo may still be running the configuration it loaded. Emergency TUN disable and system proxy recovery continue to use their own durable records.

Use a second management connection on a disposable VM for the first platform validation. See the skipped-test register before attempting Windows or Ubuntu recovery drills.
