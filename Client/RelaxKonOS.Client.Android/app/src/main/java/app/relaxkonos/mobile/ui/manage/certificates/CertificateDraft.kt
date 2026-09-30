package app.relaxkonos.mobile.ui.manage.certificates

import app.relaxkonos.mobile.core.net.*
import java.net.IDN

internal data class CertificateDraft(val selfSigned: Boolean = false, val domainsText: String = "", val email: String = "",
    val challenge: CertificateChallenge = CertificateChallenge.DirectHttp01, val key: CertificateKey = CertificateKey.EcdsaP256,
    val acceptedTerms: Boolean = false, val reachable: Boolean = false, val validityDays: String = "365") {
    fun domains(): List<String>? = runCatching {
        val raw = domainsText.split(Regex("[,\\s]+" )).filter(String::isNotBlank)
        require(raw.size in 1..100)
        raw.map { value ->
            require(value.length <= 253 && value.none(Char::isISOControl))
            val wildcard = value.startsWith("*.")
            require(!wildcard || selfSigned || challenge == CertificateChallenge.Dns01)
            val name = value.removePrefix("*.").trimEnd('.')
            val ip = name.contains(':') || name.matches(Regex("\\d+\\.\\d+\\.\\d+\\.\\d+"))
            if (ip) {
                require(selfSigned && !wildcard)
                if (name.contains(':')) {
                    require(name.matches(Regex("[0-9a-fA-F:.]+")))
                    require(java.net.InetAddress.getByName(name) is java.net.Inet6Address)
                } else require(name.split('.').all { part -> val value = part.toIntOrNull(); value != null && value in 0..255 })
                value
            } else {
                val ascii = IDN.toASCII(name, IDN.USE_STD3_ASCII_RULES).lowercase()
                require(ascii.isNotBlank() && ascii.length <= 253 && ascii.split('.').all { it.length in 1..63 })
                (if (wildcard) "*." else "") + ascii
            }
        }.distinct().sorted()
    }.getOrNull()
    fun body(): JsonBody? {
        val domains = domains() ?: return null
        if (selfSigned) {
            val days = validityDays.toIntOrNull()?.takeIf { it in 1..825 } ?: return null
            return SelfSignedCertificateRequest(domains, key, days).body()
        }
        if (!acceptedTerms || !reachable || challenge == CertificateChallenge.Dns01 ||
            !email.trim().matches(Regex("[^\\s@]+@[^\\s@]+"))) return null
        return CertificateRequest(domains, challenge, email.trim(), acceptedTerms, key, reachable).body()
    }
    val action get() = if (selfSigned) CertificateAction.SelfSigned else CertificateAction.Issue
}
