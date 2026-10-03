package app.relaxkonos.mobile.core.net

import android.content.Context
import android.content.SharedPreferences
import java.net.URI
import java.security.KeyStore
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

data class CertificateReview(val origin: String, val subject: String, val issuer: String,
    val fingerprint: String, val previous: String?, val validFrom: String, val validUntil: String,
    val trustScope: String? = null)

/** Application-local leaf pins; platform hostname validation remains enabled. */
object ServerCertificateTrust {
    private lateinit var preferences: SharedPreferences
    private val reviews = ConcurrentHashMap<String, CertificateReview>()
    private val tunnelScopes = ConcurrentHashMap<String, String>()
    fun bindTunnel(endpoint: String, serviceId: String) { tunnelScopes[origin(endpoint)] = serviceId }
    fun unbindTunnel(endpoint: String) { val target = origin(endpoint); tunnelScopes.remove(target); reviews.remove(target) }
    fun initialize(context: Context) {
        initialize(context.applicationContext.getSharedPreferences("server-certificate-pins", Context.MODE_PRIVATE))
    }
    internal fun initialize(store: SharedPreferences) { preferences = store; reviews.clear() }
    private fun origin(value: String): String = URI(value).let {
        URI("https", null, it.host, if (it.port < 0) 443 else it.port, null, null, null).toString()
    }
    fun review(value: String): CertificateReview? = reviews[origin(value)]
    fun clear(value: String) { reviews.remove(origin(value)) }
    fun trust(review: CertificateReview) {
        check(reviews[review.origin] == review)
        check(preferences.edit().putString(review.trustScope ?: review.origin, review.fingerprint).commit())
        reviews.remove(review.origin)
    }
    fun configure(connection: java.net.HttpURLConnection) {
        if (connection !is HttpsURLConnection) return
        connection.sslSocketFactory = tls(connection.url.toString()).first.socketFactory
    }
    fun tls(endpoint: String): Pair<SSLContext, X509TrustManager> {
        val target = origin(endpoint)
        val scope = tunnelScopes[target] ?: target
        val factory = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
        factory.init(null as KeyStore?)
        val platform = factory.trustManagers.filterIsInstance<X509TrustManager>().single()
        val manager = object : X509TrustManager {
            override fun getAcceptedIssuers() = platform.acceptedIssuers
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = platform.checkClientTrusted(chain, authType)
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                val leaf = chain.firstOrNull() ?: throw CertificateException("Missing server certificate")
                leaf.checkValidity()
                val usage = leaf.extendedKeyUsage
                if (usage != null && "1.3.6.1.5.5.7.3.1" !in usage && "2.5.29.37.0" !in usage)
                    throw CertificateException("Certificate is not valid for server authentication")
                val fingerprint = MessageDigest.getInstance("SHA-256").digest(leaf.encoded).joinToString("") { "%02X".format(it) }
                val previous = if (::preferences.isInitialized) preferences.getString(scope, null) else null
                var platformError: CertificateException? = null
                try { platform.checkServerTrusted(chain, authType) } catch (error: CertificateException) { platformError = error }
                // Only a valid, genuinely self-signed leaf may bypass missing platform trust.
                val selfSigned = chain.size == 1 && leaf.subjectX500Principal == leaf.issuerX500Principal &&
                    runCatching { leaf.verify(leaf.publicKey) }.isSuccess
                if (platformError != null && !selfSigned) throw platformError
                if (previous == fingerprint || previous == null && platformError == null) return
                reviews[target] = CertificateReview(target, leaf.subjectX500Principal.name, leaf.issuerX500Principal.name,
                    fingerprint, previous, leaf.notBefore.toString(), leaf.notAfter.toString(), scope)
                throw CertificateException("Server certificate requires explicit confirmation")
            }
        }
        return SSLContext.getInstance("TLS").apply { init(null, arrayOf(manager), null) } to manager
    }
}
