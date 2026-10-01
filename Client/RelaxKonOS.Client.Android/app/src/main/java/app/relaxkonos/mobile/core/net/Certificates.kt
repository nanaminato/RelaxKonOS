package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

enum class CertificateStatus(val wire: String) { Pending("pending"), Validating("validating"), Issued("issued"), Active("active"), Renewing("renewing"), Failed("failed"), Expired("expired"), Revoked("revoked") }
enum class CertificateChallenge(val wire: String) { DirectHttp01("directHttp01"), WebRootHttp01("webRootHttp01"), Dns01("dns01") }
enum class CertificateKey(val wire: String) { EcdsaP256("ecdsaP256"), Rsa2048("rsa2048") }
enum class CertificateKind(val wire: String) { Acme("acme"), SelfSigned("selfSigned") }
enum class CertificateOperationState(val wire: String) {
    Queued("queued"), Running("running"), Succeeded("succeeded"), Failed("failed"), Cancelled("cancelled");
    val active get() = this == Queued || this == Running
}
enum class CertificateAction(val kind: String, val suffix: String) {
    Issue("issue", ""), SelfSigned("create-self-signed", "/self-signed"), Renew("renew", "/renew"), Revoke("revoke", "/revoke"), Delete("delete", ""), DeployKestrel("deploy-kestrel", "/deployments/kestrel")
}
data class ManagedCertificate(val id: String, val primaryDomain: String, val subjectAlternativeNames: List<String>,
    val status: CertificateStatus, val challengeType: CertificateChallenge, val notAfterMillis: Long?,
    val issuer: String?, val serialNumber: String?, val thumbprint: String?, val notBeforeMillis: Long?,
    val keyAlgorithm: CertificateKey, val renewalWindowStartMillis: Long?, val renewalWindowEndMillis: Long?,
    val lastRenewalAtMillis: Long?, val lastRenewalProblemCode: String?, val createdAtMillis: Long, val updatedAtMillis: Long,
    val kind: CertificateKind, val fingerprintSha256: String?)
data class CertificateOperation(val operationId: String, val certificateId: String?, val kind: CertificateAction,
    val state: CertificateOperationState, val stage: String, val problemCode: String, val startedAtMillis: Long?, val completedAtMillis: Long?)
data class CertificateDomainPreflight(val domain: String, val ipv4: List<String>, val ipv6: List<String>, val problemCode: String)
data class CertificatePreflight(val canProceed: Boolean, val port80Available: Boolean?, val requiresAdministrator: Boolean,
    val domains: List<CertificateDomainPreflight>, val problemCode: String, val requiresPublicReachabilityConfirmation: Boolean)

/** Only metadata crosses the wire. Private keys, PEM and DNS provider secrets are never mobile request fields. */
data class CertificateRequest(val domains: List<String>, val challenge: CertificateChallenge, val email: String,
    val acceptedTerms: Boolean, val key: CertificateKey, val publicReachabilityConfirmed: Boolean) {
    fun body() = JsonBody().raw("domains", JSONArray(domains).toString()).string("challengeType", challenge.wire)
        .string("contactEmail", email).bool("acceptedTerms", acceptedTerms).string("keyAlgorithm", key.wire)
        .bool("publicReachabilityConfirmed", publicReachabilityConfirmed)
}
data class SelfSignedCertificateRequest(val domains: List<String>, val key: CertificateKey, val validityDays: Int) {
    fun body() = JsonBody().raw("domains", JSONArray(domains).toString()).string("keyAlgorithm", key.wire).raw("validityDays", validityDays.toString())
}
object CertificateRoutes {
    const val ROOT = "/api/v1.0/certificates"
    const val PREFLIGHT = "$ROOT/preflight"
    fun certificate(id: String) = "$ROOT/${InstallationRoutes.canonicalId(id)}"
    fun mutation(action: CertificateAction, id: String?) = when (action) {
        CertificateAction.Issue, CertificateAction.SelfSigned -> ROOT + action.suffix
        else -> certificate(requireNotNull(id)) + action.suffix
    }
    fun kestrel(id: String) = mutation(CertificateAction.DeployKestrel, id)
    fun operation(id: String) = "$ROOT/operations/${InstallationRoutes.canonicalId(id)}"
    fun cancel(id: String) = operation(id) + "/cancel"
}
object CertificateWire {
    private inline fun <reified T : Enum<T>> enum(value: String, wire: (T) -> String): T = enumValues<T>().single { wire(it) == value }
    private fun JSONObject.id(name: String) = InstallationRoutes.canonicalId(getString(name)).also { require(it != "00000000-0000-0000-0000-000000000000") }
    private fun JSONObject.time(name: String): Long? = if (isNull(name)) null else IsoInstant.requireEpochMillis(getString(name))
    private fun JSONObject.text(name: String): String? = if (isNull(name)) null else getString(name)
    private fun JSONArray.strings() = List(length()) { getString(it) }
    fun list(payload: String): List<ManagedCertificate> = JSONArray(payload).let { json -> List(json.length()) { record(json.getJSONObject(it)) }.also { records -> require(records.map { it.id }.distinct().size == records.size) } }
    fun certificate(payload: String) = record(JSONObject(payload))
    private fun record(j: JSONObject) = with(j) {
        ManagedCertificate(id("id"), getString("primaryDomain"), getJSONArray("subjectAlternativeNames").strings(),
            enum(getString("status"), CertificateStatus::wire), enum(getString("challengeType"), CertificateChallenge::wire), time("notAfter"),
            text("issuer"), text("serialNumber"), text("thumbprint"), time("notBefore"), enum(getString("keyAlgorithm"), CertificateKey::wire),
            time("renewalWindowStart"), time("renewalWindowEnd"), time("lastRenewalAt"), text("lastRenewalProblemCode"),
            requireNotNull(time("createdAt")), requireNotNull(time("updatedAt")), enum(getString("kind"), CertificateKind::wire), text("fingerprintSha256"))
    }
    fun operation(payload: String) = with(JSONObject(payload)) {
        CertificateOperation(id("operationId"), if (isNull("certificateId")) null else id("certificateId"),
            CertificateAction.entries.single { it.kind == getString("kind") }, enum(getString("state"), CertificateOperationState::wire),
            getString("stage"), getString("problemCode"), time("startedAt"), time("completedAt"))
    }
    fun kestrel(payload: String) = with(JSONObject(payload)) {
        KestrelCertificateDeployment(id("certificateId"), getBoolean("certificateExists"), getBoolean("httpsConfigured"),
            getBoolean("registered"), getBoolean("isDefault"), getJSONArray("hostNames").strings(), text("fingerprintSha256"),
            time("notBefore"), time("notAfter"), requireNotNull(time("observedAt"))).also {
                require(!it.isDefault || it.registered)
                require(!it.registered || (it.fingerprintSha256 != null && it.notBeforeMillis != null && it.notAfterMillis != null))
            }
    }
    fun preflight(payload: String) = with(JSONObject(payload)) {
        val items = getJSONArray("domains")
        CertificatePreflight(getBoolean("canProceed"), if (isNull("port80Available")) null else getBoolean("port80Available"), getBoolean("requiresAdministrator"),
            List(items.length()) { with(items.getJSONObject(it)) { CertificateDomainPreflight(getString("domain"), getJSONArray("ipv4Addresses").strings(), getJSONArray("ipv6Addresses").strings(), getString("problemCode")) } },
            getString("problemCode"), getBoolean("requiresPublicReachabilityConfirmation"))
    }
}
