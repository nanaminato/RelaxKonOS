# TUN safety

TUN is a host-wide operation, not an ordinary UI toggle. RelaxKonOS writes a recovery marker before a network change, captures the management-route plan and refuses activation when the platform cannot establish a safe management path.

The mandatory system bypass covers loopback, RelaxKonOS listeners, the active management session, default gateway, LAN, SSH and RDP. Platform verification remains required before enabling TUN on a production host.

The enable transaction changes only the Server-owned `active.yaml`: it generates the managed Mihomo `tun` section, enables TUN, preserves loopback and private LAN ranges, and excludes the captured default gateway plus the Server-observed current management address from TUN routing. Mihomo itself, under its constrained service identity, then loads routes and DNS; the Server never constructs or executes a route command. A controller reload, management-path verification, or recovery failure re-disables TUN. The recovery marker is deleted only after the original management path is verified again.
