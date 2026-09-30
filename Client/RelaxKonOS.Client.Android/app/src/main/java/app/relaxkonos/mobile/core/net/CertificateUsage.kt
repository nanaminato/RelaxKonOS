package app.relaxkonos.mobile.core.net

import java.net.IDN
import java.net.InetAddress

enum class CertificateUsageProblem { MetadataUnavailable, UnusableStatus, NotYetValid, Expired, DomainMismatch }

/** SAN coverage only. It does not prove issuer trust or that a frontend serves the material. */
object CertificateNames {
    fun normalize(value: String): String? = runCatching {
        val text = value.trim().trimEnd('.')
        require(text.isNotEmpty())
        if (text.contains(':')) {
            require(text.matches(Regex("[0-9a-fA-F:.]+")))
            return@runCatching InetAddress.getByName(text).hostAddress?.lowercase()
        }
        if (text.matches(Regex("\\d+\\.\\d+\\.\\d+\\.\\d+"))) {
            val parts = text.split('.').map { it.toInt() }; require(parts.all { it in 0..255 }); return@runCatching parts.joinToString(".")
        }
        val wildcard = text.startsWith("*.")
        val ascii = IDN.toASCII(text.removePrefix("*."), IDN.USE_STD3_ASCII_RULES).lowercase()
        require(ascii.isNotBlank() && ascii.length <= 253 && ascii.split('.').all { it.length in 1..63 })
        (if (wildcard) "*." else "") + ascii
    }.getOrNull()
    fun covers(names: List<String>, domain: String): Boolean {
        val host = normalize(domain) ?: return false
        return names.any { raw ->
            val san = normalize(raw)
            san == host || (san?.startsWith("*.") == true && !host.startsWith("*.") && !host.contains(':') &&
                !host.matches(Regex("\\d+\\.\\d+\\.\\d+\\.\\d+")) && host.substringAfter('.', "") == san.removePrefix("*."))
        }
    }
}
fun ManagedCertificate.usageProblem(domains: List<String> = emptyList(), nowMillis: Long = System.currentTimeMillis()): CertificateUsageProblem? {
    if (status !in setOf(CertificateStatus.Issued, CertificateStatus.Active)) return CertificateUsageProblem.UnusableStatus
    val start = notBeforeMillis ?: return CertificateUsageProblem.MetadataUnavailable
    val expiry = notAfterMillis ?: return CertificateUsageProblem.MetadataUnavailable
    if (nowMillis < start) return CertificateUsageProblem.NotYetValid
    if (nowMillis >= expiry) return CertificateUsageProblem.Expired
    if (domains.any { !CertificateNames.covers(subjectAlternativeNames, it) }) return CertificateUsageProblem.DomainMismatch
    return null
}
data class KestrelCertificateDeployment(val certificateId: String, val certificateExists: Boolean, val httpsConfigured: Boolean,
    val registered: Boolean, val isDefault: Boolean, val hostNames: List<String>, val fingerprintSha256: String?,
    val notBeforeMillis: Long?, val notAfterMillis: Long?, val observedAtMillis: Long) {
    fun matches(certificate: ManagedCertificate): Boolean = registered && certificateExists && certificate.id == certificateId &&
        fingerprintSha256 != null && certificate.fingerprintSha256 != null &&
        fingerprintSha256.replace(":", "").equals(certificate.fingerprintSha256.replace(":", ""), true)
}
