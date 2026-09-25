package app.relaxkonos.mobile.servercenter

import java.util.UUID

/** Serialization and strict receipt parsing for the launcher wire contract. */
internal object ServerDeploymentWire {
    const val MAXIMUM_REQUEST_BYTES = 64 * 1024
    const val MAXIMUM_RECORD_BYTES = 1024 * 1024

    fun writeRequest(request: ServerDeploymentRequest): ByteArray {
        require(request.schemaVersion == ServerDeploymentProtocol.VERSION) { "Unsupported deployment protocol." }
        require(isUuid(request.operationId)) { "operationId must be a UUID." }
        val options = request.options?.let(::writeOptions) ?: "null"
        val json = buildString {
            append("{\"schemaVersion\":").append(request.schemaVersion)
            append(",\"operationId\":").append(ServerCenterJson.quote(request.operationId.lowercase()))
            append(",\"kind\":").append(ServerCenterJson.quote(request.kind.wireName()))
            append(",\"options\":").append(options).append('}')
        }
        val bytes = json.toByteArray(Charsets.UTF_8)
        require(bytes.size <= MAXIMUM_REQUEST_BYTES && '\\' !in json && '\n' !in json && '\r' !in json) {
            "Deployment request is too large or not a single line."
        }
        return bytes
    }

    fun readOperation(json: String, expectedOperationId: String): ServerDeploymentOperation {
        require(isUuid(expectedOperationId)) { "Expected operationId must be a UUID." }
        require(json.toByteArray(Charsets.UTF_8).size <= MAXIMUM_RECORD_BYTES) {
            "Operation receipt is too large."
        }
        val fields = ServerCenterJson.parse(json, MAXIMUM_RECORD_BYTES).asObject()
        val operation = ServerDeploymentOperation(
            schemaVersion = fields.required("schemaVersion").asLong().toIntExact(),
            operationId = fields.required("operationId").asString(),
            installationId = fields.nullableString("installationId"),
            kind = fields.required("kind").enumValue<ServerDeploymentKind>(),
            phase = fields.required("phase").enumValue<ServerDeploymentPhase>(),
            state = fields.required("state").enumValue<ServerDeploymentState>(),
            sequence = fields.required("sequence").asLong(),
            timestampUtc = fields.required("timestampUtc").asString(),
            cancellable = fields.required("cancellable").asBoolean(),
            progress = fields.nullableLong("progress")?.toIntExact(),
            problemCode = fields.nullableString("problemCode"),
            safeMessage = fields.nullableString("safeMessage"),
            startedAtUtc = fields.nullableString("startedAtUtc"),
            completedAtUtc = fields.nullableString("completedAtUtc"),
            result = fields.optionalObject("result")?.let(::readResult),
            snapshot = fields.optionalObject("snapshot")?.let(::readSnapshot),
            probe = fields.optionalObject("probe")?.let(::readProbe),
        )
        require(ServerDeploymentRecordRules.validateRecord(operation, expectedOperationId) == null) {
            "${ServerDeploymentProblemCodes.INVALID_REQUEST}: operation receipt violates the deployment contract."
        }
        return operation
    }

    fun readManifest(bytes: ByteArray): ServerReleaseManifest {
        val fields = ServerCenterJson.parse(String(bytes, Charsets.UTF_8), MAXIMUM_RECORD_BYTES).asObject()
        val payload = fields.required("payload").asObject().mapValues { (_, platform) ->
            platform.asObject().mapValues { (_, path) -> path.asString() }
        }
        return ServerReleaseManifest(
            schemaVersion = fields.required("schemaVersion").asLong().toIntExact(),
            packageKind = fields.required("packageKind").enumValue(),
            version = fields.required("version").asString(),
            runtime = fields.required("runtime").enumValue(),
            supportedSystems = fields.required("supportedSystems").asArray().map { it.asString() },
            payload = payload,
            files = fields.required("files").asArray().map { item ->
                val file = item.asObject()
                ServerReleaseFile(
                    path = file.required("path").asString(),
                    length = file.required("length").asLong(),
                    sha256 = file.required("sha256").asString(),
                )
            },
            createdAtUtc = fields.nullableString("createdAtUtc"),
        )
    }

    fun readSignature(bytes: ByteArray): ServerReleaseSignature {
        val fields = ServerCenterJson.parse(String(bytes, Charsets.UTF_8), MAXIMUM_RECORD_BYTES).asObject()
        return ServerReleaseSignature(
            schemaVersion = fields.required("schemaVersion").asLong().toIntExact(),
            keyId = fields.required("keyId").asString(),
            algorithm = fields.required("algorithm").asString(),
            signedSha256 = fields.required("signedSha256").asString(),
            signature = fields.required("signature").asString(),
        )
    }

    private fun writeOptions(options: ServerDeploymentOptions): String = buildString {
        append("{\"source\":").append(ServerCenterJson.quote(options.source.wireName()))
        append(",\"network\":").append(ServerCenterJson.quote(options.network.wireName()))
        append(",\"retention\":").append(ServerCenterJson.quote(options.retention.wireName()))
        append(",\"mode\":").append(options.mode.jsonEnum())
        append(",\"version\":").append(options.version.jsonString())
        append(",\"packageUri\":").append(options.packageUri.jsonString())
        append(",\"stagedPackageName\":").append(options.stagedPackageName.jsonString())
        append(",\"packageDigest\":").append(options.packageDigest.jsonString())
        append(",\"expectedInstallationId\":").append(options.expectedInstallationId.jsonString())
        append(",\"serverPort\":").append(options.serverPort ?: "null")
        append(",\"confirmed\":").append(options.confirmed).append('}')
    }

    private fun readResult(fields: Map<String, ServerCenterJsonValue>): ServerDeploymentResult =
        ServerDeploymentResult(
            installationId = fields.nullableString("installationId"),
            mode = fields.optionalEnum("mode"),
            version = fields.nullableString("version"),
            previousVersion = fields.nullableString("previousVersion"),
            installRoot = fields.nullableString("installRoot"),
            dataRoot = fields.nullableString("dataRoot"),
            listenUrl = fields.nullableString("listenUrl"),
            healthy = fields.nullableBoolean("healthy") ?: false,
            dataRetained = fields.nullableBoolean("dataRetained"),
            dataCompatible = fields.nullableBoolean("dataCompatible"),
            serviceNames = fields.stringList("serviceNames"),
            completedAtUtc = fields.nullableString("completedAtUtc"),
        )

    private fun readSnapshot(fields: Map<String, ServerCenterJsonValue>): ServerHostSnapshot =
        ServerHostSnapshot(
            installed = fields.required("installed").asBoolean(),
            verifiedAtUtc = fields.required("verifiedAtUtc").asString(),
            installationId = fields.nullableString("installationId"),
            mode = fields.optionalEnum("mode"),
            version = fields.nullableString("version"),
            previousVersion = fields.nullableString("previousVersion"),
            installRoot = fields.nullableString("installRoot"),
            dataRoot = fields.nullableString("dataRoot"),
            listenUrl = fields.nullableString("listenUrl"),
            healthy = fields.nullableBoolean("healthy") ?: false,
            dataRetained = fields.nullableBoolean("dataRetained"),
            serviceNames = fields.stringList("serviceNames"),
        )

    private fun readProbe(fields: Map<String, ServerCenterJsonValue>): ServerHostProbe =
        ServerHostProbe(
            hostPlatform = fields.required("hostPlatform").asString(),
            architecture = fields.required("architecture").asString(),
            runtimeIdentifier = fields.optionalEnum("runtimeIdentifier"),
            osId = fields.nullableString("osId"),
            osVersion = fields.nullableString("osVersion"),
            osSupported = fields.required("osSupported").asBoolean(),
            elevated = fields.required("elevated").asBoolean(),
            sudoAvailable = fields.required("sudoAvailable").asBoolean(),
            systemdAvailable = fields.required("systemdAvailable").asBoolean(),
            diskAvailableBytes = fields.nullableLong("diskAvailableBytes"),
            requestedPort = fields.nullableLong("requestedPort")?.toIntExact(),
            requestedPortAvailable = fields.nullableBoolean("requestedPortAvailable"),
            existingInstallationId = fields.nullableString("existingInstallationId"),
            existingMode = fields.optionalEnum("existingMode"),
            existingVersion = fields.nullableString("existingVersion"),
            existingInstalled = fields.required("existingInstalled").asBoolean(),
            missingDependencies = fields.stringList("missingDependencies"),
            verifiedAtUtc = fields.required("verifiedAtUtc").asString(),
        )

    private fun isUuid(value: String): Boolean = try {
        UUID.fromString(value)
        UUID_PATTERN.matches(value)
    } catch (_: IllegalArgumentException) {
        false
    }

    private val UUID_PATTERN = Regex("^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$")
}

private fun String?.jsonString(): String = this?.let(ServerCenterJson::quote) ?: "null"
private fun Enum<*>?.jsonEnum(): String = this?.wireName()?.let(ServerCenterJson::quote) ?: "null"

private fun Long.toIntExact(): Int {
    require(this in Int.MIN_VALUE..Int.MAX_VALUE) { "JSON integer is outside Int range." }
    return toInt()
}

private inline fun <reified T : Enum<T>> ServerCenterJsonValue.enumValue(): T =
    enumFromWire<T>(asString()) ?: throw IllegalArgumentException("Unknown ${T::class.java.simpleName} value.")

private fun Map<String, ServerCenterJsonValue>.optionalObject(name: String): Map<String, ServerCenterJsonValue>? =
    when (val value = this[name]) {
        null, ServerCenterJsonValue.NullValue -> null
        else -> value.asObject()
    }

private inline fun <reified T : Enum<T>> Map<String, ServerCenterJsonValue>.optionalEnum(name: String): T? =
    when (val value = this[name]) {
        null, ServerCenterJsonValue.NullValue -> null
        else -> value.enumValue()
    }

private fun Map<String, ServerCenterJsonValue>.stringList(name: String): List<String> =
    when (val value = this[name]) {
        null, ServerCenterJsonValue.NullValue -> emptyList()
        else -> value.asArray().map { it.asString() }
    }
