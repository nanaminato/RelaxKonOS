package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.json.JSONArray

/** Frozen host proposals retain the complete current wire object, including immutable artifact layout. */
class ManagedRuntimeInstallationRequest(override val confirmed: Boolean, plan: String) : InstallationRequest {
    override val service get() = when (value.getString("runtimeId")) { "mihomo" -> InstallationService.Mihomo; "sing-box" -> InstallationService.SingBox; else -> error("Unsupported runtime") }
    val plan: String = JSONObject(plan).toString()
    private val value get() = JSONObject(plan)
    val key: String get() = value.getJSONObject("request").getString("idempotencyKey")
    val kind: InstallationKind get() = when (value.getJSONObject("request").getString("change")) {
        "install" -> InstallationKind.Install
        "update" -> InstallationKind.Upgrade
        "repair" -> InstallationKind.Repair
        "rollback" -> InstallationKind.Rollback
        "uninstall" -> InstallationKind.Uninstall
        else -> error("Unsupported runtime action")
    }
    init {
        require(value.getString("runtimeId") in setOf("mihomo", "sing-box"))
        require(key.length in 8..128 && key.isNotBlank())
        val request = value.getJSONObject("request")
        if (kind == InstallationKind.Uninstall) require(value.isNull("release") && request.isNull("releaseId"))
        else {
            val release = value.getJSONObject("release")
            require(release.getString("runtimeId") == value.getString("runtimeId") && release.getString("releaseId") == request.getString("releaseId"))
        }
    }
}

object ManagedRuntimeRoutes {
    const val ROOT = "/api/v1.0/managed-runtimes"
    const val SOURCES = "$ROOT/sources"
    const val INSTALLATION = "$ROOT/mihomo/installation"
    const val PLAN = "$ROOT/mihomo/plan"
    const val RESOURCES = "$ROOT/mihomo/resources"
    const val RECOVERY = "$ROOT/mihomo/recovery"
    fun installation(runtime: String): String { require(runtime in setOf("mihomo", "sing-box")); return "$ROOT/$runtime/installation" }
    fun plan(runtime: String): String { require(runtime in setOf("mihomo", "sing-box")); return "$ROOT/$runtime/plan" }
    fun recovery(runtime: String): String { require(runtime in setOf("mihomo", "sing-box")); return "$ROOT/$runtime/recovery" }
    fun catalog(source: String, refresh: Boolean, prerelease: Boolean): String {
        require(source.matches(Regex("[A-Za-z0-9_.-]{1,128}")))
        return "$SOURCES/$source/${if (refresh) "refresh" else "catalog"}?includePrerelease=$prerelease"
    }
}

data class ManagedRuntimeSource(val id: String, val publisher: String, val enabled: Boolean, val trust: String,
    val trustRevision: Long, val address: String, val fingerprint: String) {
    val trusted get() = enabled && trust == "confirmed"
}
data class ManagedRuntimeRelease(val id: String, val version: String, val trust: String, val supported: Boolean,
    val channel: String, val target: String, val evidence: String)
data class ManagedRuntimeCatalog(val source: ManagedRuntimeSource, val revision: Long, val trustRevision: Long, val fresh: Boolean,
    val problem: String?, val releases: List<ManagedRuntimeRelease>) {
    val trusted get() = source.trusted && trustRevision == source.trustRevision
    val usable get() = fresh && problem == null && trusted
}
class ManagedRuntimeInstallation(val wire: String) {
    private val value get() = JSONObject(wire)
    init {
        val snapshot = JSONObject(wire)
        require(snapshot.has("commitWitness"))
        require(snapshot.isNull("commitWitness") || snapshot.optJSONObject("commitWitness") != null)
    }
    /** Historical host evidence is retained verbatim; it never authorizes an installation or recovery. */
    val commitWitness: String? get() = value.optJSONObject("commitWitness")?.toString()
    private val installation get() = value.getJSONObject("installation")
    val ownership get() = installation.getString("ownership")
    val recoveryRequired get() = installation.getBoolean("recoveryRequired") || !value.isNull("pendingOperationId")
    val activeRelease: String? get() = installation.optNullableString("activeReleaseId")
    val version: String? get() = value.optNullableString("measuredVersion")
    val problem: String? get() = value.optNullableString("problemCode")
    val storedReleases: Set<String> get() {
        val releases = value.optJSONArray("installedReleases") ?: JSONArray()
        return (0 until releases.length()).map { releases.getJSONObject(it).getString("releaseId") }.toSet()
    }
    fun eligible(kind: InstallationKind, release: String): Boolean = when (kind) {
        InstallationKind.Repair -> release == activeRelease
        InstallationKind.Rollback -> release != activeRelease && release in storedReleases
        InstallationKind.Upgrade -> release != activeRelease
        InstallationKind.Install -> true
        InstallationKind.Uninstall -> false
    }
    fun request(kind: InstallationKind, catalog: ManagedRuntimeCatalog?, release: String?, key: String,
        prerelease: Boolean, keepData: Boolean): JsonBody {
        require(ownership in setOf("absent", "managed") && !recoveryRequired && problem == null)
        require((kind == InstallationKind.Install) == (ownership == "absent"))
        return JsonBody().string("installationId", installation.getString("installationId"))
            .string("hostId", installation.getString("hostId"))
            .string("change", when (kind) { InstallationKind.Upgrade -> "update"; else -> kind.wire })
            .raw("expectedStateRevision", installation.getLong("stateRevision").toString())
            .raw("expectedCatalogRevision", (catalog?.revision ?: 0).toString())
            .raw("expectedTrustRevision", (catalog?.trustRevision ?: 0).toString())
            .raw("releaseId", if (kind == InstallationKind.Uninstall) "null" else JSONObject.quote(requireNotNull(release)))
            .string("idempotencyKey", key).bool("allowPrerelease", prerelease).bool("keepData", keepData)
    }
}

object ManagedRuntimeWire {
    private fun source(value: JSONObject, runtime: String): ManagedRuntimeSource {
        require(value.getString("runtimeId") == runtime)
        val identity = value.getJSONObject("identity")
        return ManagedRuntimeSource(identity.getString("sourceId"), identity.getString("publisher"),
            identity.getBoolean("enabled"), identity.getString("trust"), identity.getLong("trustRevision"),
            value.getString("baseUri"), value.getString("confirmationFingerprint"))
    }
    fun sources(payload: String, runtime: String = "mihomo"): List<ManagedRuntimeSource> = JSONArray(payload).let { array ->
        (0 until array.length()).map { array.getJSONObject(it) }.filter { it.getString("runtimeId") == runtime }.map { source(it, runtime) }
    }
    fun catalog(payload: String, runtime: String = "mihomo"): ManagedRuntimeCatalog = JSONObject(payload).let { value ->
        val source = source(value.getJSONObject("source"), runtime)
        val catalog = if (value.isNull("catalog")) null else value.getJSONObject("catalog")
        if (catalog != null) require(catalog.getString("sourceId") == source.id)
        val releases = catalog?.getJSONArray("releases") ?: JSONArray()
        ManagedRuntimeCatalog(source, catalog?.getLong("revision") ?: 0, catalog?.getLong("trustRevision") ?: 0,
            value.getBoolean("fresh") && catalog?.getBoolean("signatureVerified") == true, value.optNullableString("problemCode"),
            (0 until releases.length()).map { releases.getJSONObject(it) }.map {
                require(it.getString("runtimeId") == runtime && it.getString("sourceId") == source.id)
                val target = it.getJSONObject("target")
                ManagedRuntimeRelease(it.getString("releaseId"), it.getString("originalVersion"), it.getString("trust"),
                    it.getBoolean("configurationSupported"), it.getString("channel"),
                    "${target.getString("os")} / ${target.getString("architecture")} / ${target.getString("serviceHost")}", it.getString("evidence"))
            })
    }
}

data class ManagedRuntimeResource(val name: String, val state: String, val length: Long?, val sha256: String?, val problem: String?)
data class ManagedRuntimeResources(val revision: String?, val files: List<ManagedRuntimeResource>, val problem: String?) {
    val canPrepare get() = revision != null && problem == null
    val verified get() = canPrepare && files.isNotEmpty() && files.all { it.state == "verified" }
}
object ManagedRuntimeResourceWire {
    private val names = setOf("geoip.metadb", "geoip.dat", "geosite.dat", "country.mmdb", "GeoLite2-ASN.mmdb")
    fun snapshot(payload: String): ManagedRuntimeResources {
        val value = JSONObject(payload)
        val revision = value.optNullableString("revisionId")?.let(InstallationRoutes::canonicalId)
        val problem = value.optNullableString("problemCode")
        val array = value.getJSONArray("files")
        val files = (0 until array.length()).map { index ->
            val file = array.getJSONObject(index); val state = file.getString("state")
            require(state in setOf("missing", "verified", "invalid", "unknown"))
            ManagedRuntimeResource(file.getString("name").also { require(it in names) }, state,
                if (file.isNull("length")) null else file.getLong("length").also { require(it in 1..134217728L) },
                file.optNullableString("sha256")?.also { require(it.matches(Regex("[A-Fa-f0-9]{64}"))) }, file.optNullableString("problemCode"))
                .also { require(if (state == "verified") it.length != null && it.sha256 != null && it.problem == null
                    else it.length == null && it.sha256 == null && (if (state == "missing") it.problem == null else it.problem?.startsWith("runtime.") == true)) }
        }
        require(if (problem == null) revision != null && files.map { it.name }.toSet() == names && files.size == names.size else files.isEmpty())
        return ManagedRuntimeResources(revision, files, problem)
    }
    fun matchesReport(payload: String, observed: ManagedRuntimeResources): Boolean {
        val report = JSONObject(payload); val files = report.getJSONArray("files")
        if (!observed.verified || report.getString("revisionId") != observed.revision || files.length() != names.size) return false
        return (0 until files.length()).all { i -> val file = files.getJSONObject(i)
            observed.files.singleOrNull { it.name == file.getString("name") }?.let {
                it.length == file.getLong("length") && it.sha256.equals(file.getString("sha256"), ignoreCase = true)
            } == true } && (0 until files.length()).map { files.getJSONObject(it).getString("name") }.toSet() == names
    }
}
