package app.relaxkonos.mobile.ui.manage.smb

import app.relaxkonos.mobile.core.net.*

data class SmbDraft(val id: String? = null, val name: String = "", val path: String = "", val description: String = "",
    val readOnly: Boolean = false, val enabled: Boolean = true, val guestAllowed: Boolean = false,
    val permissions: List<SmbPermission> = emptyList()) {
    fun request() = SmbShareRequest(name.trim(), path.trim(), description.trim().takeIf(String::isNotEmpty), readOnly, enabled, guestAllowed,
        permissions.map { it.copy(principal = it.principal.trim()) })
    companion object {
        fun from(share: SmbShare, windows: Boolean) = SmbDraft(share.id, share.name, share.path, share.description.orEmpty(), share.readOnly, share.enabled,
            share.guestAllowed, share.permissions.filterNot { windows && share.guestAllowed && it.principal in setOf("S-1-5-7", "S-1-5-32-546") })
    }
}
