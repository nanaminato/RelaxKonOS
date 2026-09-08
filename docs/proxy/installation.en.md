# Proxy installation

1. Grant the Proxy application permissions appropriate to the operator.
2. Install a manifest-verified Managed runtime, or validate an existing External runtime.
3. Create and activate a profile, then apply a validated configuration.
4. Start the managed runtime with TUN disabled and confirm controller health. On Windows,
   `RelaxKonOS.Server` owns the Mihomo child process; on Linux, systemd owns `mihomo.service`.
5. Enable TUN only after the platform checklist in `RelaxKonOS.ProxyManager.SkippedTests.en.md` has been passed.

An unavailable privileged boundary or platform capability is a safe failure; RelaxKonOS will not request a password or run substituted shell text.
