package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import org.json.JSONArray
import org.json.JSONObject

/** Current WebServerContracts projection. Runtime paths and configuration stay on the host. */
data class WebServer(
    val id: String,
    val type: String,
    val managementMode: String,
    val version: String?,
    val canRead: Boolean,
    val canTestConfiguration: Boolean,
    val providerId: String,
    val executablePath: String,
    val configurationPath: String?,
    val detectedAtMillis: Long,
    val canReload: Boolean,
    val canStart: Boolean,
    val canStop: Boolean,
    val canRestart: Boolean,
    val canUninstall: Boolean,
)

data class WebServerStatus(val instanceId: String, val runtimeState: String, val problemCode: String)
data class WebServerConfigTest(val valid: Boolean, val problemCode: String)
data class WebServerBinding(val domain: String, val port: Int)
data class WebServerRoute(val path: String, val upstream: String, val disableBuffering: Boolean)

data class WebServerSite(
    val id: String,
    val serverId: String,
    val name: String,
    val bindings: List<WebServerBinding>,
    val routes: List<WebServerRoute>,
    val certificateId: String?,
    val httpsEnabled: Boolean,
    val updatedAtMillis: Long?,
    /** Preserve server timestamp precision for atomic edit/delete checks. */
    val updatedAt: String,
    val rootPath: String?,
    val spaFallback: Boolean,
    val redirectHttpToHttps: Boolean,
    val ipv6Enabled: Boolean,
    val certificatePath: String?,
    val privateKeyPath: String?,
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
    private const val PUBLICATIONS = "/api/v1.0/website-publications"
    fun publish(): String = PUBLICATIONS
    fun publicationHistory(applicationId: String): String = "$PUBLICATIONS/applications/${segment(applicationId)}"
    fun publication(operationId: String): String = "$PUBLICATIONS/operations/${segment(operationId)}"
    fun servers(): String = WEBSERVERS
    fun status(id: String): String = "${server(id)}/status"
    fun testConfiguration(id: String): String = "${server(id)}/config/test"
    fun site(id: String, siteId: String): String = "${sites(id)}/${segment(siteId)}"
    fun sites(id: String): String = "${server(id)}/sites"
    fun discover(): String = "$WEBSERVERS/discover"
    fun candidates(): String = "$WEBSERVERS/integration-candidates"
    fun integrate(id: String): String = "${candidates()}/${segment(id)}/integrate"
    fun catalog(): String = "$WEBSERVERS/managed/catalog"
    fun installDownload(version: String): String = "$WEBSERVERS/managed/download?version=" + java.net.URLEncoder.encode(version, "UTF-8")
    fun lifecycle(id: String, action: WebServerAction): String = "${server(id)}/lifecycle/${action.route}"
    fun operation(id: String): String = "$WEBSERVERS/operations/${InstallationRoutes.canonicalId(id)}"
    fun cancel(id: String): String = "${operation(id)}/cancel"
    private fun server(id: String): String = "$WEBSERVERS/${segment(id)}"
    private fun segment(value: String): String {
        require(value.isNotBlank()) { "Path segment is required." }
        return URLEncoder.encode(value, StandardCharsets.UTF_8.name())
    }
}

/** Strict wire readers: a malformed host response is not mistaken for a healthy publication. */
internal object WebPublishingWire {
    fun servers(payload: String): List<WebServer> = JSONArray(payload).objects { json ->
        require(json.getString("type") == "nginx" && json.getString("managementMode") in setOf("integrated", "managed"))
        val capabilities = json.getJSONObject("capabilities")
        WebServer(json.getString("id"), json.getString("type"), json.getString("managementMode"),
            json.nullableText("version"), capabilities.getBoolean("canRead"), capabilities.getBoolean("canTestConfiguration"),
            json.getString("providerId"), json.getString("executablePath"), json.nullableText("configurationPath"),
            requireNotNull(json.nullableInstant("detectedAt")), capabilities.getBoolean("canReload"),
            capabilities.getBoolean("canStart"), capabilities.getBoolean("canStop"),
            capabilities.getBoolean("canRestart"), capabilities.getBoolean("canUninstall"))
    }

    fun candidates(payload: String): List<WebServerCandidate> = JSONArray(payload).objects { json ->
        require(json.getString("type") == "nginx")
        WebServerCandidate(json.getString("id"), json.getString("providerId"), json.getString("executablePath"),
            json.nullableText("configurationPath"), json.nullableText("version"), requireNotNull(json.nullableInstant("detectedAt")))
    }

    fun catalog(payload: String): WebServerInstallCatalog = JSONObject(payload).let { json ->
        WebServerInstallCatalog(json.nullableText("mainlineVersion"), json.nullableText("stableVersion"),
            json.getJSONArray("versions").strings(), json.getString("problemCode"))
    }
    fun installDownload(payload: String): WebServerInstallDownload = JSONObject(payload).let {
        WebServerInstallDownload(it.getString("version"), it.getString("url"))
    }

    fun operation(payload: String): WebServerOperation = JSONObject(payload).let { json ->
        WebServerOperation(InstallationRoutes.canonicalId(json.getString("operationId")), json.getString("instanceId"),
            json.getString("kind"), WebServerOperationState.entries.single { it.wire == json.getString("state") },
            json.getString("stage"), json.getString("problemCode"), json.nullableText("snapshotId"),
            json.nullableInstant("startedAt"), json.nullableInstant("completedAt"))
    }

    fun status(payload: String): WebServerStatus = JSONObject(payload).let { json ->
        WebServerStatus(json.getString("instanceId"), json.getString("runtimeState"), json.getString("problemCode"))
    }

    fun configTest(payload: String): WebServerConfigTest = JSONObject(payload).let { json ->
        WebServerConfigTest(json.getBoolean("valid"), json.getString("problemCode"))
    }

    fun sites(payload: String): List<WebServerSite> = JSONArray(payload).objects { it.site() }
    fun site(payload: String): WebServerSite = JSONObject(payload).site()
    private fun JSONObject.site(): WebServerSite = WebServerSite(
        getString("id"), getString("serverId"), getString("name"),
        getJSONArray("bindings").objects { WebServerBinding(it.getString("domain"), it.getInt("port")) },
        getJSONArray("routes").objects { WebServerRoute(it.getString("path"), it.getString("upstream"), it.getBoolean("disableBuffering")) },
        nullableText("certificateId"), getBoolean("httpsEnabled"), requireNotNull(nullableInstant("updatedAt")),
        getString("updatedAt"), nullableText("rootPath"), getBoolean("spaFallback"), getBoolean("redirectHttpToHttps"),
        getBoolean("ipv6Enabled"), nullableText("certificatePath"), nullableText("privateKeyPath"),
    )

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
