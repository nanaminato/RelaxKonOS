# TUN safety

TUN is a host-wide operation, not an ordinary UI toggle. RelaxKonOS writes a recovery marker before a network change, captures the management-route plan and refuses activation when the platform cannot establish a safe management path.

The mandatory system bypass covers loopback, RelaxKonOS listeners, the active management session, default gateway, LAN, SSH and RDP. Platform verification remains required before enabling TUN on a production host.
