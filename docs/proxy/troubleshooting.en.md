# Proxy troubleshooting

`proxy.privileged_operation_unavailable` means the constrained platform service action was not available; do not work around it with manual command injection through RelaxKonOS. `proxy.recovery_required` means a previous TUN transaction needs recovery before retrying.

For a failed configuration apply, retain the last working configuration and inspect only sanitized Server diagnostics. For a network issue, use Emergency Disable TUN, then complete the relevant disposable-VM recovery test before retrying.
