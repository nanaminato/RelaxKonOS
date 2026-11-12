# Explorer: This PC

Desktop Explorer displays **This PC** as a card overview. Local drives and
server-filtered Linux data mounts appear under Devices and drives. Authorized
cloud connections have one card per connection ID. Matching names or providers
do not merge accounts. Cloud cards open the existing account-scoped browser;
they never become local paths.

Cards show reported capacity. Usage bars require known, consistent total and
available space. The cloud connection contract does not yet expose provider
quota, so cloud cards explicitly show capacity unavailable, never an estimate.

The nonfunctional Network navigation placeholder is removed, without removing
UNC support, cloud protocols, or network services. File pickers retain their
existing selection UI; normal folders retain their selected view mode.

Linux system-mode enumeration includes root and real local block-backed data
mounts, filters virtual/container/system mounts and duplicate bind identities,
and applies session browsing access checks. User execution mode remains limited
to its home root. Network filesystems and loop images are not advertised as local
data disks. Hardware hotplug and live-provider acceptance remain manual checks.
