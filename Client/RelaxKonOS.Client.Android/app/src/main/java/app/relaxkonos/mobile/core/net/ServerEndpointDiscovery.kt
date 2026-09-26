package app.relaxkonos.mobile.core.net

import java.net.HttpURLConnection
import java.net.URI
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

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

        candidates.firstOrNull(::isLoginEndpointAvailable)
            ?.let(EndpointDiscoveryResult::Found)
            ?: EndpointDiscoveryResult.Unavailable
    }

    private fun isLoginEndpointAvailable(serverUrl: String): Boolean = try {
        val connection = (URI(serverUrl + LOGIN_ROUTE).toURL().openConnection() as HttpURLConnection)
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
        require(uri.scheme == "http" || uri.scheme == "https")
        require(!uri.host.isNullOrBlank())
        require(uri.userInfo.isNullOrEmpty())
        require(uri.query.isNullOrEmpty() && uri.fragment.isNullOrEmpty())
        require(uri.path.isNullOrEmpty() || uri.path == "/")
        "${uri.scheme}://${uri.rawAuthority}".trimEnd('/')
    }.getOrNull()
}
