package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/**
 * Wire contract of the built-in Proxy Server application (`relaxkonos.proxy-server`).
 *
 * The management routes are host-global. The only anonymous surface is the subscription download
 * route, and this client never calls it: a subscription link is handed to a proxy client, not opened
 * here. Secrets (the VLESS uuid, the Trojan/Hysteria2 password, the subscription token) travel once
 * and are never persisted by this client.
 */
enum class ProxyServerProtocol(val wire: String) {
    VlessReality("vlessReality"), VlessTls("vlessTls"), VlessWebSocket("vlessWebSocket"),
    Trojan("trojan"), Hysteria2("hysteria2");

    val isVless get() = this == VlessReality || this == VlessTls || this == VlessWebSocket
    val isUdp get() = this == Hysteria2
    val requiresCertificate get() = this != VlessReality
}

/** Which secret shape a credential carries. Never converted between protocols. */
enum class ProxyServerCredentialKind(val wire: String) { Uuid("uuid"), Password("password") }

enum class ProxyServerRuntimeAction(val wire: String) {
    Start("start"), Stop("stop"), Restart("restart"), Enable("enable"), Disable("disable")
}

/** One inbound service: what the runtime binds, and what a client connects to. */
data class ProxyServerInbound(
    val id: String,
    val name: String,
    val protocol: ProxyServerProtocol,
    val listenAddress: String,
    val listenPort: Int,
    val publicHost: String,
    val publicPort: Int,
    val serverName: String,
    val certificateId: String?,
    val handshakeHost: String,
    val handshakePort: Int,
    val webSocketPath: String,
    val enabled: Boolean,
)

data class ProxyServerConfiguration(val revision: Long, val inbounds: List<ProxyServerInbound>, val subscriptionBaseUrl: String?)

data class ProxyServerPrincipal(val id: String, val displayName: String, val userId: String?, val credentialCount: Int)

/** Metadata only. The uuid or password stays behind the export route. */
data class ProxyServerCredential(
    val id: String,
    val principalId: String,
    val name: String,
    val inboundIds: List<String>,
    val enabled: Boolean,
    val expiresAtMillis: Long?,
    val applied: Boolean,
)

data class ProxyServerSubscription(
    val id: String,
    val credentialId: String,
    val formats: List<String>,
    val createdAtMillis: Long?,
    val expiresAtMillis: Long?,
    val revoked: Boolean,
)

/** The raw token appears exactly once, in the issuing response. */
data class ProxyServerSubscriptionIssued(val subscription: ProxyServerSubscription, val token: String, val urls: List<String>)

data class ProxyServerValidation(val valid: Boolean, val problems: List<String>, val preview: String?)

data class ProxyServerProtocolCapability(
    val protocol: ProxyServerProtocol,
    val credentialKind: ProxyServerCredentialKind,
    val udp: Boolean,
    val requiresCertificate: Boolean,
    val verified: Boolean,
)

data class ProxyServerCapabilities(
    val supported: Boolean,
    val platform: String,
    val architecture: String,
    val kernelVersion: String,
    val protocols: List<ProxyServerProtocolCapability>,
    val statistics: Boolean,
    val logs: Boolean,
    val serviceControl: Boolean,
    val bootAutostart: Boolean,
    val uninstall: Boolean,
    val subscriptionHosting: Boolean,
)

data class ProxyServerStatus(
    val supported: Boolean,
    val installed: Boolean,
    val running: Boolean,
    val recoveryRequired: Boolean,
    val version: String?,
    val configurationHash: String?,
    val applied: Boolean,
    val problemCode: String,
    val statisticsSupported: Boolean,
    val bootEnabled: Boolean?,
)

data class ProxyServerRelease(
    val version: String,
    val platform: String,
    val architecture: String,
    val sha256: String,
    val installed: Boolean,
    val verifiedProtocols: List<ProxyServerProtocol>,
)

data class ProxyServerSnapshot(
    val configuration: ProxyServerConfiguration,
    val principals: List<ProxyServerPrincipal>,
    val credentials: List<ProxyServerCredential>,
    val subscriptions: List<ProxyServerSubscription>,
    val status: ProxyServerStatus,
    val capabilities: ProxyServerCapabilities,
    val publishedRevision: Long?,
    val previousRevision: Long?,
)

/** Export formats this build can produce, in the order the surface offers them. */
object ProxyServerExportFormats {
    const val URI = "uri"
    const val SING_BOX = "sing-box"
    const val MIHOMO = "mihomo"
    val all = listOf(URI, SING_BOX, MIHOMO)
}

data class ProxyServerExport(val format: String, val content: String, val warning: String?)

/** Whether one format can be produced for the selected credential, and why not when it cannot. */
data class ProxyServerExportOption(val format: String, val supported: Boolean, val reason: String?)

/**
 * Stable problem codes this client branches on. The server answers with the same identifiers; anything
 * else is rendered as the code itself rather than invented as a sentence.
 */
object ProxyServerProblemCodes {
    const val REVISION_CONFLICT = "proxy_server.revision_conflict"
    const val CONFIGURATION_NOT_APPLIED = "proxy_server.configuration_not_applied"
    const val CONFIGURATION_MISSING = "proxy_server.configuration_missing"
    const val CONFIGURATION_DRIFT = "proxy_server.configuration_drift"
    const val RECOVERY_REQUIRED = "proxy_server.recovery_required"
    const val PLATFORM_UNSUPPORTED = "proxy_server.platform_unsupported"
    const val HELPER_UNAVAILABLE = "proxy_server.helper_unavailable"
    const val IDEMPOTENCY_CONFLICT = "proxy_server.idempotency_conflict"
    const val EXPORT_MULTIPLE_INBOUNDS = "proxy_server.export_multiple_inbounds"
    const val SUBSCRIPTION_LIMIT = "proxy_server.subscription_limit"
    const val PERMISSION_DENIED = "proxy_server.permission_denied"
    const val ROLLBACK_UNAVAILABLE = "proxy_server.rollback_unavailable"
}

class SaveProxyServerConfiguration(val expectedRevision: Long, val inbounds: List<ProxyServerInbound>, val subscriptionBaseUrl: String?) {
    fun body() = JsonBody().raw("expectedRevision", expectedRevision.toString())
        .raw("inbounds", JSONArray().apply { inbounds.forEach { put(ProxyServerWire.inboundJson(it)) } }.toString())
        .string("subscriptionBaseUrl", subscriptionBaseUrl?.trim()?.takeIf(String::isNotEmpty))
}

class SaveProxyServerPrincipal(val id: String?, val displayName: String, val userId: String?) {
    fun body() = JsonBody().string("id", id).string("displayName", displayName.trim())
        .string("relaxKonOSUserId", userId?.trim()?.takeIf(String::isNotEmpty))
}

class SaveProxyServerCredential(val id: String?, val principalId: String, val name: String, val inboundIds: List<String>,
    val enabled: Boolean, val expiresAtMillis: Long?) {
    fun body() = JsonBody().string("id", id).string("principalId", principalId).string("name", name.trim())
        .raw("inboundIds", JSONArray().apply { inboundIds.forEach { put(it) } }.toString())
        .bool("enabled", enabled)
        .raw("expiresAt", expiresAtMillis?.let { "\"" + IsoInstant.fromEpochMillis(it) + "\"" } ?: "null")
}

/** Rotation never returns the new secret; a lost secret is rotated again. */
class RotateProxyServerCredential(val enabled: Boolean, val expiresAtMillis: Long?, val clearExpiry: Boolean) {
    fun body() = JsonBody().bool("enabled", enabled)
        .raw("expiresAt", expiresAtMillis?.let { "\"" + IsoInstant.fromEpochMillis(it) + "\"" } ?: "null")
        .bool("clearExpiry", clearExpiry)
}

class IssueProxyServerSubscription(val credentialId: String, val formats: List<String>, val expiresAtMillis: Long?) {
    fun body() = JsonBody().string("credentialId", credentialId)
        .raw("formats", JSONArray().apply { formats.forEach { put(it) } }.toString())
        .raw("expiresAt", expiresAtMillis?.let { "\"" + IsoInstant.fromEpochMillis(it) + "\"" } ?: "null")
}

class ProxyServerExportRequest(val credentialId: String, val format: String) {
    fun body() = JsonBody().string("credentialId", credentialId).string("format", format)
}

object ProxyServerRoutes {
    const val ROOT = "/api/v1.0/proxy-server"
    const val CAPABILITIES = "$ROOT/capabilities"
    const val STATUS = "$ROOT/status"
    const val RUNTIME_RELEASES = "$ROOT/runtime/releases"
    const val RUNTIME_INSTALL = "$ROOT/runtime/install"
    const val RUNTIME_UPGRADE = "$ROOT/runtime/upgrade"
    const val RUNTIME_UNINSTALL = "$ROOT/runtime/uninstall"
    const val CONFIGURATION = "$ROOT/configuration"
    const val VALIDATE = "$CONFIGURATION/validate"
    const val APPLY = "$CONFIGURATION/apply"
    const val ROLLBACK = "$CONFIGURATION/rollback"
    const val RECOVERY = "$ROOT/recovery"
    const val PRINCIPALS = "$ROOT/principals"
    const val CREDENTIALS = "$ROOT/credentials"
    const val SUBSCRIPTIONS = "$ROOT/subscriptions"

    fun runtimeAction(action: ProxyServerRuntimeAction) = "$ROOT/runtime/actions/${action.wire}"
    fun principal(id: String) = "$PRINCIPALS/${InstallationRoutes.canonicalId(id)}"
    fun credential(id: String) = "$CREDENTIALS/${InstallationRoutes.canonicalId(id)}"
    fun rotate(id: String) = "${credential(id)}/rotate"
    fun exportFormats(id: String) = "${credential(id)}/export-formats"
    fun export(id: String) = "${credential(id)}/export"
    fun subscription(id: String) = "$SUBSCRIPTIONS/${InstallationRoutes.canonicalId(id)}"
    fun operation(id: String) = "$ROOT/operations/${InstallationRoutes.canonicalId(id)}"
}

object ProxyServerWire {
    private fun JSONObject.text(key: String): String? = if (isNull(key)) null else getString(key)
    private fun id(j: JSONObject, key: String) = InstallationRoutes.canonicalId(j.getString(key))
    private fun ids(j: JSONObject, key: String): List<String> = j.getJSONArray(key).let { a ->
        require(a.length() <= 512); List(a.length()) { InstallationRoutes.canonicalId(a.getString(it)) }
    }
    private fun <T> array(payload: String, limit: Int = 10000, parse: (JSONObject) -> T) = JSONArray(payload).let { a ->
        require(a.length() <= limit); List(a.length()) { parse(a.getJSONObject(it)) }
    }

    fun protocol(value: String) = ProxyServerProtocol.entries.single { it.wire == value }

    fun inbound(payload: String) = inbound(JSONObject(payload))
    internal fun inbound(j: JSONObject) = ProxyServerInbound(
        id(j, "id"), j.getString("name"), protocol(j.getString("protocol")), j.getString("listenAddress"),
        j.getInt("listenPort").also { require(it in 1024..65535) }, j.getString("publicHost"),
        j.getInt("publicPort").also { require(it in 1..65535) }, j.getString("serverName"),
        j.text("certificateId")?.let(InstallationRoutes::canonicalId), j.optString("handshakeHost", ""),
        j.optInt("handshakePort", 443), j.optString("webSocketPath", "/proxy"), j.getBoolean("enabled"))

    /** Request shape of one inbound. The server validates it again; this only keeps the wire well formed. */
    internal fun inboundJson(inbound: ProxyServerInbound) = JSONObject()
        .put("id", inbound.id).put("name", inbound.name).put("protocol", inbound.protocol.wire)
        .put("listenAddress", inbound.listenAddress).put("listenPort", inbound.listenPort)
        .put("publicHost", inbound.publicHost).put("publicPort", inbound.publicPort)
        .put("serverName", inbound.serverName)
        .put("handshakeHost", inbound.handshakeHost).put("handshakePort", inbound.handshakePort)
        .put("webSocketPath", inbound.webSocketPath).put("enabled", inbound.enabled)
        // An absent certificate is omitted rather than sent as null: the server reads a missing field as
        // "this preset does not use a certificate", which is what a REALITY inbound means.
        .also { json -> inbound.certificateId?.let { json.put("certificateId", it) } }

    fun configuration(payload: String) = configuration(JSONObject(payload))
    internal fun configuration(j: JSONObject) = ProxyServerConfiguration(
        j.getLong("revision").also { require(it >= 0) },
        array(j.getJSONArray("inbounds").toString()) { inbound(it) },
        j.text("subscriptionBaseUrl"))

    fun principal(payload: String) = principal(JSONObject(payload))
    internal fun principal(j: JSONObject) = ProxyServerPrincipal(
        id(j, "id"), j.getString("displayName"), j.text("relaxKonOSUserId"), j.getInt("credentialCount"))

    fun principals(payload: String) = array(payload) { principal(it) }

    fun credential(payload: String) = credential(JSONObject(payload))
    internal fun credential(j: JSONObject) = ProxyServerCredential(
        id(j, "id"), id(j, "principalId"), j.getString("name"), ids(j, "inboundIds"), j.getBoolean("enabled"),
        j.text("expiresAt")?.let(IsoInstant::requireEpochMillis), j.getBoolean("applied"))

    fun credentials(payload: String) = array(payload) { credential(it) }

    fun subscription(payload: String) = subscription(JSONObject(payload))
    internal fun subscription(j: JSONObject) = ProxyServerSubscription(
        id(j, "id"), id(j, "credentialId"),
        j.getJSONArray("formats").let { a -> require(a.length() <= 8); List(a.length()) { a.getString(it) } },
        j.text("createdAt")?.let(IsoInstant::requireEpochMillis),
        j.text("expiresAt")?.let(IsoInstant::requireEpochMillis), j.getBoolean("revoked"))

    fun subscriptions(payload: String) = array(payload) { subscription(it) }

    fun issued(payload: String) = JSONObject(payload).let { j ->
        ProxyServerSubscriptionIssued(subscription(j.getJSONObject("subscription")), j.getString("token"),
            j.getJSONArray("urls").let { a -> require(a.length() <= 8); List(a.length()) { a.getString(it) } })
    }

    fun validation(payload: String) = JSONObject(payload).let { j ->
        ProxyServerValidation(j.getBoolean("valid"),
            j.getJSONArray("problems").let { a -> require(a.length() <= 256); List(a.length()) { a.getString(it) } },
            j.text("preview"))
    }

    fun capabilities(payload: String) = JSONObject(payload).let { j ->
        ProxyServerCapabilities(j.getBoolean("supported"), j.getString("platform"), j.getString("architecture"),
            j.getString("kernelVersion"),
            array(j.getJSONArray("protocols").toString(), 8) { p ->
                ProxyServerProtocolCapability(protocol(p.getString("protocol")),
                    ProxyServerCredentialKind.entries.single { it.wire == p.getString("credentialKind") },
                    p.getBoolean("udp"), p.getBoolean("requiresCertificate"), p.getBoolean("verified"))
            },
            j.getBoolean("statistics"), j.getBoolean("logs"), j.getBoolean("serviceControl"),
            j.getBoolean("bootAutostart"), j.getBoolean("uninstall"), j.getBoolean("subscriptionHosting"))
    }

    fun status(payload: String) = status(JSONObject(payload))
    internal fun status(j: JSONObject) = ProxyServerStatus(
        j.getBoolean("supported"), j.getBoolean("installed"), j.getBoolean("running"), j.getBoolean("recoveryRequired"),
        j.text("version"), j.text("configurationHash"), j.getBoolean("applied"), j.optString("problemCode", ""),
        j.optBoolean("statisticsSupported", false), if (j.isNull("bootEnabled")) null else j.optBoolean("bootEnabled"))

    fun snapshot(payload: String) = JSONObject(payload).let { j ->
        ProxyServerSnapshot(configuration(j.getJSONObject("configuration")), principals(j.getJSONArray("principals").toString()),
            credentials(j.getJSONArray("credentials").toString()), subscriptions(j.getJSONArray("subscriptions").toString()),
            status(j.getJSONObject("status")), capabilities(j.getJSONObject("capabilities").toString()),
            if (j.isNull("publishedRevision")) null else j.getLong("publishedRevision"),
            if (j.isNull("previousRevision")) null else j.getLong("previousRevision"))
    }

    fun release(payload: String) = release(JSONObject(payload))
    internal fun release(j: JSONObject) = ProxyServerRelease(
        j.getString("version"), j.getString("platform"), j.getString("architecture"), j.getString("sha256"),
        j.getBoolean("installed"),
        j.getJSONArray("verifiedProtocols").let { a -> require(a.length() <= 8); List(a.length()) { protocol(a.getString(it)) } })

    fun releases(payload: String) = array(payload, 64) { release(it) }

    fun exportFormats(payload: String) = array(payload, 8) { j ->
        ProxyServerExportOption(j.getString("format").also { require(it in ProxyServerExportFormats.all) },
            j.getBoolean("supported"), j.text("reason"))
    }

    fun export(payload: String) = JSONObject(payload).let { j ->
        ProxyServerExport(j.getString("format"), j.getString("content"), j.text("warning"))
    }
}
