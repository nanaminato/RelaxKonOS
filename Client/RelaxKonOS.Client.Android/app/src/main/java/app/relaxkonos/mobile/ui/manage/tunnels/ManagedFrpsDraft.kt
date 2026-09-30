package app.relaxkonos.mobile.ui.manage.tunnels

import app.relaxkonos.mobile.core.net.*

/** Only safe fields survive editor state. Replacement credentials belong to the open dialog. */
internal data class ManagedFrpsDraft(val revision: Long = 0, val bindAddress: String = "0.0.0.0", val bindPort: String = "7000",
    val allowPorts: String = "", val httpPort: String = "", val httpsPort: String = "", val forceTls: Boolean = true,
    val tokenConfigured: Boolean = false, val dashboardEnabled: Boolean = false, val dashboardAddress: String = "127.0.0.1",
    val dashboardPort: String = "7500", val dashboardUser: String = "", val dashboardPasswordConfigured: Boolean = false) {
    fun request(token: CharArray, password: CharArray): ManagedFrpsRequest? {
        val bind = literalAddress(bindAddress) ?: return null
        val bindPort = TunnelInputs.port(bindPort) ?: return null
        val ranges = portRanges(allowPorts) ?: return null
        val http = httpPort.takeIf(String::isNotBlank)?.let { TunnelInputs.port(it) ?: return null }
        val https = httpsPort.takeIf(String::isNotBlank)?.let { TunnelInputs.port(it) ?: return null }
        val dashboard = literalAddress(dashboardAddress) ?: return null
        val dashboardPort = dashboardPort.takeIf(String::isNotBlank)?.let { TunnelInputs.port(it) ?: return null }
        val ports = listOfNotNull(bindPort, http, https, dashboardPort.takeIf { dashboardEnabled })
        if (ports.distinct().size != ports.size || !validSecret(token) || !validSecret(password)) return null
        if (!tokenConfigured && token.all(Char::isWhitespace)) return null
        if (dashboardUser.length > 128 || dashboardUser.any(Char::isISOControl)) return null
        if (dashboardEnabled && (dashboardPort == null || dashboardUser.isBlank() || !dashboardPasswordConfigured && password.all(Char::isWhitespace))) return null
        return ManagedFrpsRequest(true, bind, bindPort, ranges, http, https, forceTls, token.takeUnless { it.all(Char::isWhitespace) }, dashboardEnabled,
            dashboard, dashboardPort, dashboardUser.trim().takeIf(String::isNotEmpty), password.takeUnless { it.all(Char::isWhitespace) }, revision)
    }
    companion object {
        fun from(v: ManagedFrps) = ManagedFrpsDraft(v.revision, v.bindAddress, v.bindPort.toString(),
            v.allowPorts.joinToString(", ") { if (it.start == it.end) it.start.toString() else "${it.start}-${it.end}" },
            v.httpPort?.toString().orEmpty(), v.httpsPort?.toString().orEmpty(), v.forceTls, v.tokenConfigured,
            v.dashboardEnabled, v.dashboardAddress, v.dashboardPort?.toString().orEmpty(), v.dashboardUser.orEmpty(), v.dashboardPasswordConfigured)
        fun literalAddress(value: String): String? {
            val text = value.trim()
            if (!text.contains(':') && !text.matches(Regex("\\d+\\.\\d+\\.\\d+\\.\\d+"))) return null
            return CertificateNames.normalize(text)
        }
        fun validSecret(value: CharArray) = value.size <= 4096 && value.none(Char::isISOControl)
        fun portRanges(value: String): List<TunnelPortRange>? {
            val entries = value.split(',').map(String::trim)
            if (entries.size !in 1..64 || entries.any(String::isBlank)) return null
            return entries.map { entry ->
                val parts = entry.split('-').map(String::trim)
                if (parts.size !in 1..2) return null
                val start = TunnelInputs.port(parts[0]) ?: return null
                val end = if (parts.size == 1) start else TunnelInputs.port(parts[1]) ?: return null
                if (end < start) return null
                TunnelPortRange(start, end)
            }
        }
    }
}
