package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import org.json.JSONArray
import org.json.JSONObject

/** Read-only AD05 projections. Site ownership and certificate material remain on the server. */
data class WebServer(
    val id: String,
    val type: String,
    val managementMode: String,
    val version: String?,
    val canRead: Boolean,
    val canTestConfiguration: Boolean,
)

data class WebServerStatus(val instanceId: String, val runtimeState: String, val problemCode: String)
data class WebServerConfigTest(val valid: Boolean, val problemCode: String)
data class WebServerBinding(val domain: String, val port: Int)
data class WebServerRoute(val path: String, val upstream: String)

data class WebServerSite(
    val id: String,
    val serverId: String,
    val name: String,
    val bindings: List<WebServerBinding>,
    val routes: List<WebServerRoute>,
    val certificateId: String?,
    val httpsEnabled: Boolean,
    val updatedAtMillis: Long?,
)

data class ManagedCertificate(
    val id: String,
    val primaryDomain: String,
    val subjectAlternativeNames: List<String>,
    val status: String,
    val challengeType: String,
    val notAfterMillis: Long?,
)

/** A request is explicit about every externally visible side effect; DNS credentials never cross this boundary. */
data class WebsitePublishRequest(
    val applicationId: String,
    val webServerId: String,
    val domain: String,
    val certificateId: String? = null,
    val contactEmail: String? = null,
    val acceptedTerms: Boolean = false,
    val publicReachabilityConfirmed: Boolean = false,
    val confirmed: Boolean = false,
)

data class WebsitePublicationCheck(
    val name: String,
    val observer: String,
    val state: String,
    val problemCode: String,
    val observedAtMillis: Long?,
)

/** Durable host record: success means configuration completed, while checks retain their own evidence. */
data class WebsitePublicationOperation(
    val operationId: String,
    val applicationId: String,
    val webServerId: String,
    val domain: String,
    val siteId: String?,
    val certificateId: String?,
    val certificateOperationId: String?,
    val state: String,
    val stage: String,
    val problemCode: String,
    val recoveryProblemCode: String?,
    val checks: List<WebsitePublicationCheck>,
    val createdAtMillis: Long?,
    val startedAtMillis: Long?,
    val completedAtMillis: Long?,
)

/** Mirrors Protocol route ownership; dynamic segments are encoded as one path segment. */
object WebPublishingRoutes {
    private const val WEBSERVERS = "/api/v1.0/webservers"
    const val CERTIFICATES = "/api/v1.0/certificates"
    private const val PUBLICATIONS = "/api/v1.0/website-publications"
    fun publish(): String = PUBLICATIONS
    fun publicationHistory(applicationId: String): String = "$PUBLICATIONS/applications/${segment(applicationId)}"
    fun publication(operationId: String): String = "$PUBLICATIONS/operations/${segment(operationId)}"
    fun servers(): String = WEBSERVERS
    fun status(id: String): String = "${server(id)}/status"
    fun testConfiguration(id: String): String = "${server(id)}/config/test"
    fun sites(id: String): String = "${server(id)}/sites"
    private fun server(id: String): String = "$WEBSERVERS/${segment(id)}"
    private fun segment(value: String): String {
        require(value.isNotBlank()) { "Path segment is required." }
        return URLEncoder.encode(value, StandardCharsets.UTF_8.name())
    }
}

/** Strict wire readers: a malformed host response is not mistaken for a healthy publication. */
internal object WebPublishingWire {
    fun servers(payload: String): List<WebServer> = JSONArray(payload).objects { json ->
        val capabilities = json.getJSONObject("capabilities")
        WebServer(json.getString("id"), json.getString("type"), json.getString("managementMode"),
            json.nullableText("version"), capabilities.getBoolean("canRead"), capabilities.getBoolean("canTestConfiguration"))
    }

    fun status(payload: String): WebServerStatus = JSONObject(payload).let { json ->
        WebServerStatus(json.getString("instanceId"), json.getString("runtimeState"), json.getString("problemCode"))
    }

    fun configTest(payload: String): WebServerConfigTest = JSONObject(payload).let { json ->
        WebServerConfigTest(json.getBoolean("valid"), json.getString("problemCode"))
    }

    fun sites(payload: String): List<WebServerSite> = JSONArray(payload).objects { json ->
        WebServerSite(
            json.getString("id"), json.getString("serverId"), json.getString("name"),
            json.getJSONArray("bindings").objects { WebServerBinding(it.getString("domain"), it.getInt("port")) },
            json.getJSONArray("routes").objects { WebServerRoute(it.getString("path"), it.getString("upstream")) },
            json.nullableText("certificateId"), json.getBoolean("httpsEnabled"),
            json.nullableInstant("updatedAt"),
        )
    }

    fun certificates(payload: String): List<ManagedCertificate> = JSONArray(payload).objects { json ->
        ManagedCertificate(json.getString("id"), json.getString("primaryDomain"),
            json.getJSONArray("subjectAlternativeNames").strings(), json.getString("status"),
            json.getString("challengeType"), json.nullableInstant("notAfter"))
    }

    fun publication(payload: String): WebsitePublicationOperation = JSONObject(payload).publication()
    fun publicationHistory(payload: String): List<WebsitePublicationOperation> = JSONArray(payload).objects { it.publication() }

    private fun JSONObject.publication(): WebsitePublicationOperation = WebsitePublicationOperation(
        getString("operationId"), getString("applicationId"), getString("webServerId"), getString("domain"),
        nullableText("siteId"), nullableText("certificateId"), nullableText("certificateOperationId"),
        getString("state"), getString("stage"), getString("problemCode"), nullableText("recoveryProblemCode"),
        getJSONArray("checks").objects { check -> WebsitePublicationCheck(
            check.getString("name"), check.getString("observer"), check.getString("state"),
            check.getString("problemCode"), check.nullableInstant("observedAt"),
        ) },
        nullableInstant("createdAt"), nullableInstant("startedAt"), nullableInstant("completedAt"),
    )

    private fun JSONObject.nullableText(key: String): String? = if (isNull(key)) null else getString(key).takeIf { it.isNotBlank() }
    private fun JSONObject.nullableInstant(key: String): Long? = nullableText(key)?.let(IsoInstant::toEpochMillis)
    private fun <T> JSONArray.objects(parse: (JSONObject) -> T): List<T> = (0 until length()).map { parse(getJSONObject(it)) }
    private fun JSONArray.strings(): List<String> = (0 until length()).map(::getString)
}
