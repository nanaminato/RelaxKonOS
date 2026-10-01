package app.relaxkonos.mobile.ui.manage.processes

import app.relaxkonos.mobile.core.net.RemoteProcess

/** A new query can retain a selection only when the server proves the same process instance. */
internal fun refreshedProcessSelection(selected: RemoteProcess?, items: List<RemoteProcess>): RemoteProcess? =
    selected?.takeIf { it.startTime != null }?.let { target -> items.firstOrNull { it.pid == target.pid && it.startTime == target.startTime } }
