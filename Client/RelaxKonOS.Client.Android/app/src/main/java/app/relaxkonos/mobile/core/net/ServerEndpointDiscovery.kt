package app.relaxkonos.mobile.core.net

import java.net.HttpURLConnection
import java.net.URI
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/** The result of checking a server address without sending any credentials. */
sealed interface EndpointDiscoveryResult {
    data class Found(val serverUrl: String) : EndpointDiscoveryResult
    data object InvalidAddress : EndpointDiscoveryResult
    data object Unavailable : EndpointDiscoveryResult
}

/**
 * Turns a login address into a verified API endpoint.
 *
 * A bare host is checked as HTTPS and then HTTP, in that order. An explicit scheme is deliberate
 * configuration (for example a local HTTP development server), so only that address is checked.
 * Probing uses OPTIONS on the POST-only login route: a 405 is expected and proves that routing is
 * available without creating an authentication attempt or affecting login-rate limiting.
 */
object ServerEndpointDiscovery {
    private const val CONNECT_TIMEOUT_MILLIS = 4_000
    private const val READ_TIMEOUT_MILLIS = 4_000
    private const val LOGIN_ROUTE = "/api/v1.0/auth/login"

    fun candidates(value: String): List<String> {
        val input = value.trim()
        if (input.isEmpty()) return emptyList()

        return if (input.contains("://")) {
            normalize(input)?.let(::listOf).orEmpty()
        } else {
            listOf("https://$input", "http://$input")
                .mapNotNull(::normalize)
                .distinct()
        }
    }

    suspend fun discover(value: String): EndpointDiscoveryResult = withContext(Dispatchers.IO) {
        val candidates = candidates(value)
        if (candidates.isEmpty()) return@withContext EndpointDiscoveryResult.InvalidAddress

        for (candidate in candidates) {
            currentCoroutineContext().ensureActive()
            val https = URI(candidate).scheme == "https"
            if (https) ServerCertificateTrust.review(candidate)?.let(ServerCertificateTrust::clear)
            val available = isLoginEndpointAvailable(candidate)
            currentCoroutineContext().ensureActive()
            if (available) return@withContext EndpointDiscoveryResult.Found(candidate)
            if (https && ServerCertificateTrust.review(candidate) != null) return@withContext EndpointDiscoveryResult.Unavailable
        }
        EndpointDiscoveryResult.Unavailable
    }

    private fun isLoginEndpointAvailable(serverUrl: String): Boolean = try {
        val connection = (URI(serverUrl + LOGIN_ROUTE).toURL().openConnection() as HttpURLConnection)
        ServerCertificateTrust.configure(connection)
        try {
            connection.requestMethod = "OPTIONS"
            connection.instanceFollowRedirects = false
            connection.connectTimeout = CONNECT_TIMEOUT_MILLIS
            connection.readTimeout = READ_TIMEOUT_MILLIS
            connection.setRequestProperty("Accept", "application/json")
            val status = connection.responseCode
            status != HttpURLConnection.HTTP_NOT_FOUND && status in 200..499
        } finally {
            connection.disconnect()
        }
    } catch (_: Exception) {
        false
    }

    private fun normalize(value: String): String? = runCatching {
        val uri = URI(value)
        require(uri.scheme?.lowercase(java.util.Locale.ROOT) in setOf("http", "https"))
        require(!uri.host.isNullOrBlank())
        require(uri.rawUserInfo == null)
        require(uri.rawQuery == null && uri.rawFragment == null)
        require(uri.path.isNullOrEmpty() || uri.path == "/")
        require(uri.port == -1 || uri.port in 1..65535)
        app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules.normalizeServerUrl(value)
    }.getOrNull()
}
