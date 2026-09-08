# Proxy network recovery

On startup, RelaxKonOS evaluates any durable TUN recovery marker before allowing another activation. `Emergency disable TUN` restores the captured network state independently of runtime uninstall and leaves the marker in place if restoration fails.

Use a second management connection on a disposable VM for the first platform validation. See the skipped-test register before attempting Windows or Ubuntu recovery drills.
