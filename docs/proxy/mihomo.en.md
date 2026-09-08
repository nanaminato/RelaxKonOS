# Mihomo runtime

Managed Mihomo installations are selected only from the fixed Server manifest and verified before activation. Releases use immutable version directories with active/previous rollback state. External runtimes are detection-only unless an administrator explicitly chooses a RelaxKonOS-managed instance.

The controller binds locally and its secret remains in the Proxy-scoped protected store.
