# RelaxKonOS Proxy Manager — skipped platform tests

Updated: 2026-08-31

The current implementation has only been compiled on the active development
host.  The tests below are deliberately **not** run here: they require a
dedicated, privileged, disposable Windows or Ubuntu host with an isolated
management connection.  They must be completed individually before Proxy
Manager V1 is released.

| ID | Platform | Test to run | Required verification | Why skipped now |
|---|---|---|---|---|
| PM-G5-WIN-01 | Windows / Windows Server | Managed Mihomo child-process start, stop, restart, update and uninstall | Server-owned process tree is cleaned up on shutdown; protected paths and restart behavior work; no Defender or firewall changes | No isolated, privileged Windows test host or approved Mihomo runtime is available. |
| PM-G5-WIN-02 | Windows / Windows Server | TUN enable, disable and emergency restore | Current RelaxKonOS session, listener, gateway, LAN, SSH/RDP routes and DNS remain reachable; recovery restores the original network state | This changes host routing/DNS and must not be first exercised on the development host. |
| PM-G5-WIN-03 | Windows / Windows Server | Crash and reboot recovery during TUN activation | Durable marker is discovered and restores a safe route/DNS state after Mihomo, Server and OS restart | Requires a disposable VM and a second management client. |
| PM-G5-UBU-01 | Ubuntu / Ubuntu Server | Managed Mihomo systemd lifecycle | Service installation, startup, update/rollback and headless operation | No Ubuntu/systemd host is available in this run. |
| PM-G5-UBU-02 | Ubuntu / Ubuntu Server | `/dev/net/tun` enable, disable and emergency restore | Egress interface, route/DNS snapshot, system bypass and management connectivity stay valid | Requires `/dev/net/tun`, root-level service operations and an isolated management path. |
| PM-G5-UBU-03 | Ubuntu / Ubuntu Server | Crash and reboot recovery during TUN activation | Marker-driven recovery works after Mihomo, Server and OS restart without a desktop session | Requires a disposable Ubuntu Server VM and a second management client. |

## Execution order

Run the automated Server suite first, then execute the platform cases in the
listed order on disposable VMs.  Do not enable TUN on a production host for
initial verification.  Record the environment, Mihomo artifact hash, RelaxKonOS
revision, result and any problem code beside each case when it is executed.

The in-process coverage in `RelaxKonOS.Server.Tests` exercises the fail-closed
TUN transaction, marker persistence, rollback and emergency-disable paths
using a fake network platform.  It does not replace the cases above.

## Deferred non-platform verification

| ID | Test to run | Why skipped now |
|---|---|---|
| PM-G6-G8-API-01 | Run `RelaxKonOS.Server.Tests` after API host fixture coverage for Proxy authorization, idempotency, operation recovery and audit output is provisioned. | This implementation pass is compile-only; no Server process or test suite was started. |
