package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray

/** Unresolved public plans live in device-private, non-backed-up storage, never alongside tokens. */
class ServerModuleReferences(private val directory: File) {
    private fun scope(owner: SessionState.Active): File {
        val identity = JSONArray().put(owner.serviceId).put(owner.userName).put(owner.workspaceId).toString()
        val digest = MessageDigest.getInstance("SHA-256").digest(identity.toByteArray()).joinToString("") { "%02x".format(it) }
        return File(directory, "$digest.json")
    }
    private fun packageFile(owner: SessionState.Active) = File(directory, scope(owner).name + ".packages")
    @Synchronized fun readPackages(owner: SessionState.Active): List<ModulePackagePlan> {
        val file = packageFile(owner)
        if (!file.exists()) return emptyList()
        require(file.length() <= 524288)
        val values = JSONArray(file.readText()); require(values.length() <= 64)
        return (0 until values.length()).map { ModulePackagesWire.plan(values.getJSONObject(it).toString()) }
    }
    @Synchronized fun resolvePackage(owner: SessionState.Active, id: String) {
        val items = readPackages(owner).filterNot { it.id == id }
        val file = packageFile(owner)
        val temp = File(directory, file.name + "." + UUID.randomUUID() + ".tmp")
        try {
            val text = JSONArray().apply { items.forEach { put(org.json.JSONObject(it.raw)) } }.toString()
            java.io.FileOutputStream(temp).use { it.write(text.toByteArray()); it.fd.sync() }
            java.nio.file.Files.move(temp.toPath(), file.toPath(), java.nio.file.StandardCopyOption.ATOMIC_MOVE, java.nio.file.StandardCopyOption.REPLACE_EXISTING)
        } finally { temp.delete() }
    }
    @Synchronized fun savePackage(owner: SessionState.Active, plan: ModulePackagePlan) {
        val items = readPackages(owner)
        require(items.none { it.id == plan.id } || items.single { it.id == plan.id }.raw == plan.raw)
        if (items.any { it.id == plan.id }) return
        require(items.size < 64)
        require(directory.isDirectory || directory.mkdirs())
        val text = JSONArray().apply { (items + plan).forEach { put(org.json.JSONObject(it.raw)) } }.toString()
        require(text.toByteArray().size <= 524288)
        val target = packageFile(owner)
        val temp = File(directory, target.name + "." + UUID.randomUUID() + ".tmp")
        try {
            java.io.FileOutputStream(temp).use { it.write(text.toByteArray()); it.fd.sync() }
            java.nio.file.Files.move(temp.toPath(), target.toPath(), java.nio.file.StandardCopyOption.ATOMIC_MOVE, java.nio.file.StandardCopyOption.REPLACE_EXISTING)
        } finally { temp.delete() }
    }
    @Synchronized fun read(owner: SessionState.Active): List<ServerModulePlan> {
        val file = scope(owner)
        if (!file.exists()) return emptyList()
        val bytes = file.inputStream().use { stream ->
            val buffer = java.io.ByteArrayOutputStream()
            val chunk = ByteArray(8192)
            var count = stream.read(chunk)
            while (count >= 0) {
                require(buffer.size() + count <= 524288)
                buffer.write(chunk, 0, count); count = stream.read(chunk)
            }
            buffer.toByteArray()
        }
        val values = JSONArray(bytes.toString(Charsets.UTF_8)); require(values.length() <= 64)
        return (0 until values.length()).map { ServerModulesWire.plan(values.getJSONObject(it).toString()) }
    }
    @Synchronized fun save(owner: SessionState.Active, plan: ServerModulePlan) {
        val plans = read(owner)
        val existing = plans.singleOrNull { it.id == plan.id }
        require(existing == null || existing == plan)
        if (existing != null) return
        require(plans.size < 64)
        write(owner, plans + plan)
    }
    @Synchronized fun resolve(owner: SessionState.Active, planId: String) = write(owner, read(owner).filterNot { it.id == planId })
    private fun write(owner: SessionState.Active, plans: List<ServerModulePlan>) {
        require(directory.isDirectory || directory.mkdirs())
        val bytes = JSONArray().apply { plans.forEach { put(org.json.JSONObject(it.raw)) } }.toString().toByteArray()
        require(bytes.size <= 524288)
        val file = scope(owner)
        val temporary = File(directory, file.name + "." + UUID.randomUUID() + ".tmp")
        try {
            java.io.FileOutputStream(temporary).use { stream -> stream.write(bytes); stream.fd.sync() }
            java.nio.file.Files.move(temporary.toPath(), file.toPath(), java.nio.file.StandardCopyOption.ATOMIC_MOVE, java.nio.file.StandardCopyOption.REPLACE_EXISTING)
        } finally { temporary.delete() }
    }
}

class ServerModuleRepository(private val session: AuthSession, private val gateway: RelaxKonGateway,
    private val references: ServerModuleReferences) {
    private val pins = mutableMapOf<String, String>()
    private fun verify(owner: SessionState.Active) { check(session.state.value == owner) { "module-session-changed" } }
    private suspend fun <T> call(owner: SessionState.Active, action: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        val result = session.authenticated { url, token -> verify(owner); action(url, token) }
        verify(owner)
        return result
    }
    suspend fun read(owner: SessionState.Active): ApiResult<List<ServerModuleFact>> {
        val result = call(owner) { url, token -> gateway.serverModules(url, token) }
        if (result is ApiResult.Success) {
            require(result.value.isNotEmpty())
            val host = result.value.first().serviceId
            synchronized(pins) {
                check(pins[owner.sessionInstanceId]?.let { it == host } != false) { "module-server-mismatch" }
                pins.clear(); pins[owner.sessionInstanceId] = host
            }
        }
        return result
    }
    suspend fun preview(owner: SessionState.Active, facts: List<ServerModuleFact>, ids: Set<String>, enable: Boolean): ApiResult<ServerModulePlan> {
        return previewAction(owner, facts, ids, if (enable) "Enable" else "Disable")
    }
    suspend fun previewAction(owner: SessionState.Active, facts: List<ServerModuleFact>, ids: Set<String>, action: String,
        strategy: String? = null, idleGraceSeconds: Int? = null): ApiResult<ServerModulePlan> {
        verify(owner)
        require(action in setOf("Enable", "Disable", "Wake", "TrySleep", "SetResidencyPolicy", "Stop", "Restart", "Remove"))
        if (action == "SetResidencyPolicy") require(strategy in setOf("KeepLoaded", "IdleDelayed", "ReleaseOnLogout") && idleGraceSeconds != null && idleGraceSeconds in 0..604800)
        else require(strategy == null && idleGraceSeconds == null)
        require(ids.isNotEmpty() && facts.isNotEmpty() && facts.map { it.id }.containsAll(ids))
        val host = facts.first().serviceId
        check(synchronized(pins) { pins[owner.sessionInstanceId] } == host)
        require(facts.filter { it.id in ids }.all { action in it.allowedActions })
        require(facts.map { it.desiredRevision }.distinct().size == 1 && facts.all { it.serviceId == host })
        val desired = facts.filter { it.desiredEnabled }.map { it.id }.toMutableSet()
        if (action == "Enable") desired.addAll(ids) else if (action in setOf("Disable", "Remove")) desired.removeAll(ids)
        val body = JsonBody().string("serviceId", host).string("action", action)
            .raw("moduleIds", JSONArray(ids.sorted()).toString()).raw("enabledModuleIds", JSONArray(desired.sorted()).toString())
            .string("expectedRevision", facts.first().desiredRevision).string("idempotencyKey", UUID.randomUUID().toString())
        if (action == "SetResidencyPolicy") body.string("strategy", strategy).raw("idleGraceSeconds", idleGraceSeconds.toString())
        val result = call(owner) { url, token -> gateway.serverModulePlan(url, token, body) }
        if (result is ApiResult.Success) {
            require(result.value.serviceId == host)
            val expected = org.json.JSONObject(body.toByteArray().toString(Charsets.UTF_8))
            val actual = org.json.JSONObject(result.value.raw).getJSONObject("target")
            expected.keys().forEach { key -> require(expected.get(key).toString() == actual.get(key).toString()) }
            require(actual.isNull("releaseId"))
            if (action != "SetResidencyPolicy") require(actual.isNull("strategy") && actual.isNull("idleGraceSeconds"))
        }
        return result
    }
    private fun packageOwner(owner: SessionState.Active) {
        verify(owner); require(owner.controller)
        check(synchronized(pins) { pins[owner.sessionInstanceId] } != null)
    }
    suspend fun uploadPackage(owner: SessionState.Active, source: File): ApiResult<ModulePackagePlan> {
        packageOwner(owner)
        return when (val upload = call(owner) { url, token -> gateway.uploadModulePackage(url, token, source) }) {
            is ApiResult.Success -> {
                require(upload.value.bytes == source.length())
                when (val plan = call(owner) { url, token -> gateway.modulePackagePlan(url, token, upload.value.id) }) {
                    is ApiResult.Success -> { require(plan.value.sha256 == upload.value.sha256); plan }
                    else -> plan
                }
            }
            is ApiResult.Problem -> upload
            is ApiResult.Transport -> upload
        }
    }
    suspend fun store(owner: SessionState.Active, query: String, offset: Int): ApiResult<ModuleStorePage> {
        packageOwner(owner); require(offset >= 0)
        return call(owner) { url, token -> gateway.moduleStore(url, token, query, offset) }
    }
    suspend fun downloadPackage(owner: SessionState.Active, item: ModuleStoreItem): ApiResult<ModulePackagePlan> {
        packageOwner(owner); require(item.blockers.isEmpty())
        return when (val upload = call(owner) { url, token -> gateway.downloadModulePackage(url, token, item) }) {
            is ApiResult.Success -> when (val plan = call(owner) { url, token -> gateway.modulePackagePlan(url, token, upload.value.id) }) {
                is ApiResult.Success -> { require(plan.value.moduleId == item.moduleId && plan.value.version == item.version && plan.value.sha256 == upload.value.sha256); plan }
                else -> plan
            }
            is ApiResult.Problem -> upload
            is ApiResult.Transport -> upload
        }
    }
    suspend fun applyPackage(owner: SessionState.Active, plan: ModulePackagePlan): ApiResult<ModulePackageReceipt> {
        packageOwner(owner)
        require(plan.operationId == null && java.time.OffsetDateTime.parse(plan.expiresAt).toInstant().isAfter(java.time.Instant.now()))
        withContext(Dispatchers.IO) { references.savePackage(owner, plan) }
        return when (val receipt = call(owner) { url, token -> gateway.applyModulePackage(url, token, plan.id) }) {
            is ApiResult.Success -> { require(receipt.value.moduleId == plan.moduleId && receipt.value.version == plan.version); receipt }
            else -> receipt
        }
    }
    suspend fun recoverPackages(owner: SessionState.Active): List<ModulePackageRecovery> {
        packageOwner(owner)
        val recovered = mutableListOf<ModulePackageRecovery>()
        val operations = when (val result = call(owner) { url, token -> gateway.modulePackageOperations(url, token) }) {
            is ApiResult.Success -> result.value
            is ApiResult.Problem -> error(result.code)
            is ApiResult.Transport -> error("module-state-unavailable")
        }
        for (original in withContext(Dispatchers.IO) { references.readPackages(owner) }) {
            when (val result = call(owner) { url, token -> gateway.modulePackagePlanStatus(url, token, original.id) }) {
                is ApiResult.Success -> {
                    val current = result.value
                    require(current.id == original.id && current.moduleId == original.moduleId && current.version == original.version
                        && current.sha256 == original.sha256 && current.sourceId == original.sourceId && current.publisherId == original.publisherId)
                    val operation = operations.singleOrNull { it.id == current.operationId }
                    require(operation == null || operation.moduleId == current.moduleId && operation.version == current.version)
                    recovered += ModulePackageRecovery(current, operation)
                    if (operation?.state in setOf("Succeeded", "Failed") || current.operationId == null && !java.time.OffsetDateTime.parse(current.expiresAt).toInstant().isAfter(java.time.Instant.now())) withContext(Dispatchers.IO) { references.resolvePackage(owner, original.id) }
                }
                is ApiResult.Problem -> recovered += ModulePackageRecovery(original, null, result.code)
                is ApiResult.Transport -> recovered += ModulePackageRecovery(original, null, "module-state-unavailable")
            }
        }
        return recovered
    }
    suspend fun apply(owner: SessionState.Active, plan: ServerModulePlan): ApiResult<ServerModuleOperation> {
        verify(owner)
        check(synchronized(pins) { pins[owner.sessionInstanceId] } == plan.serviceId)
        require(plan.blockers.isEmpty() && java.time.OffsetDateTime.parse(plan.expiresAt).toInstant().isAfter(java.time.Instant.now()))
        withContext(Dispatchers.IO) { references.save(owner, plan) }
        val result = call(owner) { url, token -> gateway.applyServerModulePlan(url, token, plan.id) }
        if (result is ApiResult.Success) {
            require(result.value.planId == plan.id && result.value.serviceId == plan.serviceId)
            val target = org.json.JSONObject(plan.raw).getJSONObject("target").getJSONArray("enabledModuleIds")
            require(result.value.enabledIds.toSet() == (0 until target.length()).map { target.getString(it) }.toSet())
        }
        return result
    }
    suspend fun recover(owner: SessionState.Active, facts: List<ServerModuleFact>): List<ServerModulePlanStatus> {
        verify(owner)
        val pending = withContext(Dispatchers.IO) { references.read(owner) }
        val recovered = mutableListOf<ServerModulePlanStatus>()
        for (plan in pending) {
            check(facts.isNotEmpty() && facts.all { it.serviceId == plan.serviceId }) { "module-server-mismatch" }
            when (val result = call(owner) { url, token -> gateway.serverModulePlanStatus(url, token, plan.id) }) {
                is ApiResult.Success -> {
                    require(result.value.plan.id == plan.id && result.value.plan.serviceId == plan.serviceId)
                    val originalTarget = org.json.JSONObject(plan.raw).getJSONObject("target")
                    val recoveredTarget = org.json.JSONObject(result.value.plan.raw).getJSONObject("target")
                    for (field in listOf("serviceId", "action", "moduleIds", "enabledModuleIds", "expectedRevision", "idempotencyKey", "releaseId", "strategy", "idleGraceSeconds")) {
                        if (originalTarget.isNull(field)) require(recoveredTarget.isNull(field))
                        else require(originalTarget.get(field).toString() == recoveredTarget.get(field).toString())
                    }
                    recovered += result.value
                    val operation = result.value.operation
                    if (operation != null) {
                        val target = originalTarget.getJSONArray("enabledModuleIds")
                        require(operation.enabledIds.toSet() == (0 until target.length()).map { target.getString(it) }.toSet())
                    }
                    val policyMatches = plan.policy == null || plan.moduleIds.all { id -> facts.singleOrNull { it.id == id }?.effectivePolicy == plan.policy }
                    if (operation?.terminal == true && (plan.action in setOf("Wake", "TrySleep", "Stop", "Restart") || operation.state == "Failed"
                        || policyMatches && facts.filter { it.effectiveEnabled }.map { it.id }.toSet() == operation.enabledIds.toSet()))
                        withContext(Dispatchers.IO) { references.resolve(owner, plan.id) }
                }
                is ApiResult.Problem -> error(result.code)
                is ApiResult.Transport -> error("module-state-unavailable")
            }
        }
        return recovered
    }
}
