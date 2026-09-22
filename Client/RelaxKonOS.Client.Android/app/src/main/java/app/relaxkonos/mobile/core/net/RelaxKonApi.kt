package app.relaxkonos.mobile.core.net

import android.os.Build
import java.io.File
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

/**
 * Kotlin implementation of the RelaxKonOS REST wire contract. Route names, JSON property names and
 * enum values must stay synchronized with `RelaxKonOS.Protocol`.
 *
 * The client is deliberately stateless: the current server address and access token are supplied by
 * `AuthSession`, which is also the only place that decides when a refresh happens.
 */
class RelaxKonApi(
    private val clientVersion: String,
    private val deviceName: String = defaultDeviceName(),
) : RelaxKonGateway {
    override suspend fun login(serverUrl: String, identifier: String, password: CharArray): ApiResult<LoginSession> {
        val body = JsonBody()
            .string("identifier", identifier.trim())
            .secret("password", password)
            .string("clientPlatform", CLIENT_PLATFORM)
            .string("deviceName", deviceName)
            .string("clientVersion", clientVersion)
        return when (val parsed = execute("POST", serverUrl, AuthRoutes.LOGIN, accessToken = null, body = body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> parseLogin(parsed.value)
        }
    }

    override suspend fun refresh(serverUrl: String, refreshToken: String): ApiResult<AuthTokens> {
        val body = JsonBody().string("refreshToken", refreshToken)
        return when (val parsed = execute("POST", serverUrl, AuthRoutes.REFRESH, accessToken = null, body = body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                AuthTokens(
                    accessToken = json.getString("accessToken"),
                    refreshToken = json.getString("refreshToken"),
                    accessTokenExpiresAtMillis = IsoInstant.toEpochMillis(json.optString("accessTokenExpiresAt")),
                    refreshTokenExpiresAtMillis = IsoInstant.toEpochMillis(json.optString("refreshTokenExpiresAt")),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed refresh response.") })
        }
    }

    override suspend fun logout(serverUrl: String, accessToken: String, refreshToken: String): ApiResult<Unit> {
        val body = JsonBody().string("refreshToken", refreshToken)
        return execute("POST", serverUrl, AuthRoutes.LOGOUT, accessToken, body).asUnit()
    }

    /** One-shot host elevation request. The password never outlives this call. */
    override suspend fun requestElevation(
        serverUrl: String,
        accessToken: String,
        capability: String,
        target: String,
        password: CharArray?,
        administratorUsername: String?,
    ): ApiResult<ElevationGrant> {
        val body = JsonBody()
            .string("capability", capability)
            .string("target", target)
            .nullableSecret("password", password)
            .string("administratorUsername", administratorUsername)
            .bool("includeDescendants", false)
        return when (val parsed = execute("POST", serverUrl, PrivilegedRoutes.ELEVATION, accessToken, body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                ElevationGrant(json.optBoolean("elevated"), IsoInstant.toEpochMillis(json.optString("expiresAt")))
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed elevation response.") })
        }
    }

    /**
     * One-shot file elevation request. Mutating operations grant their directory scope through
     * `includeDescendants` because the target itself may not be readable yet.
     */
    override suspend fun requestFileElevation(
        serverUrl: String,
        accessToken: String,
        path: String,
        capability: String?,
        password: CharArray?,
        relatedPaths: List<String>,
        includeDescendants: Boolean,
        administratorUsername: String?,
    ): ApiResult<FileElevationGrant> {
        val body = JsonBody()
            .string("path", path)
            .string("capability", capability)
            .nullableSecret("password", password)
            .string("administratorUsername", administratorUsername)
            .bool("includeDescendants", includeDescendants)
        if (relatedPaths.isNotEmpty()) {
            body.raw("relatedPaths", jsonStringArray(relatedPaths))
        }
        return when (val parsed = execute("POST", serverUrl, FileRoutes.ELEVATION, accessToken, body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                FileElevationGrant(
                    requiresElevation = json.optBoolean("requiresElevation"),
                    elevated = json.optBoolean("elevated"),
                    expiresAtMillis = IsoInstant.toEpochMillis(json.optString("expiresAt")),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed file elevation response.") })
        }
    }

    override suspend fun listDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<DirectoryListing> {
        val query = "?path=" + encode(path)
        return when (val parsed = execute("GET", serverUrl, FileRoutes.LIST + query, accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                val entries = mutableListOf<RemoteEntry>()
                val directories = json.optJSONArray("directories")
                for (index in 0 until (directories?.length() ?: 0)) {
                    val item = directories!!.getJSONObject(index)
                    entries += RemoteEntry(
                        path = item.getString("path"),
                        name = item.getString("name"),
                        isDirectory = true,
                        sizeBytes = item.optNullableLong("size"),
                        modifiedAtMillis = IsoInstant.toEpochMillis(item.optString("modified")),
                        mimeType = null,
                    )
                }
                val files = json.optJSONArray("files")
                for (index in 0 until (files?.length() ?: 0)) {
                    val item = files!!.getJSONObject(index)
                    entries += RemoteEntry(
                        path = item.getString("path"),
                        name = item.getString("name"),
                        isDirectory = false,
                        sizeBytes = item.optNullableLong("size"),
                        modifiedAtMillis = IsoInstant.toEpochMillis(item.optString("modified")),
                        mimeType = item.optString("mimeType").takeIf { it.isNotBlank() },
                    )
                }
                DirectoryListing(
                    path = json.optString("path", path),
                    name = json.optString("name"),
                    entries = entries.sortedWith(
                        compareByDescending<RemoteEntry> { it.isDirectory }.thenBy { it.name.lowercase() },
                    ),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed directory listing.") })
        }
    }

    override suspend fun fileProperties(serverUrl: String, accessToken: String, path: String): ApiResult<RemoteFileProperties> {
        val query = "?path=" + encode(path)
        return when (val parsed = execute("GET", serverUrl, FileRoutes.PROPERTIES + query, accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                RemoteFileProperties(
                    path = json.getString("path"),
                    name = json.getString("name"),
                    isDirectory = json.optString("type") == "directory",
                    sizeBytes = json.optNullableLong("size"),
                    createdMillis = IsoInstant.toEpochMillis(json.optString("created")),
                    modifiedMillis = IsoInstant.toEpochMillis(json.optString("modified")),
                    permissions = json.optString("permissions"),
                    unixMode = json.optInt("unixMode", -1).takeIf { it >= 0 },
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed file properties.") })
        }
    }

    override suspend fun createDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        execute("POST", serverUrl, FileRoutes.DIRECTORY + "?path=" + encode(path), accessToken, null).asUnit()

    override suspend fun delete(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        execute("DELETE", serverUrl, FileRoutes.DELETE + "?path=" + encode(path), accessToken, null).asUnit()

    override suspend fun rename(serverUrl: String, accessToken: String, sourcePath: String, newName: String): ApiResult<Unit> {
        val body = JsonBody().string("sourcePath", sourcePath).string("newName", newName)
        return execute("POST", serverUrl, FileRoutes.RENAME, accessToken, body).asUnit()
    }

    override suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot> {
        return when (val parsed = execute("GET", serverUrl, SystemRoutes.PERFORMANCE_SNAPSHOT, accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                val cpu = json.getJSONObject("cpu")
                val memory = json.getJSONObject("memory")
                val health = json.optJSONObject("health")
                val filesystems = json.optJSONArray("filesystems") ?: JSONArray()
                PerformanceSnapshot(
                    cpuPercent = cpu.optDouble("totalPercent", 0.0),
                    memoryUsedBytes = memory.optLong("usedBytes"),
                    memoryTotalBytes = memory.optLong("totalBytes"),
                    uptimeSeconds = json.optLong("uptimeSeconds"),
                    filesystems = (0 until filesystems.length()).map { index ->
                        val item = filesystems.getJSONObject(index)
                        DiskUsage(
                            id = item.optString("id"),
                            usedBytes = item.optLong("usedBytes"),
                            totalBytes = item.optLong("totalBytes"),
                            percent = item.optDouble("percent", 0.0),
                        )
                    },
                    isStale = health?.optBoolean("isStale") ?: false,
                    lastSampleMillis = IsoInstant.toEpochMillis(health?.optString("lastSuccessfulSampleAt")),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed performance snapshot.") })
        }
    }

    override suspend fun queryProcesses(
        serverUrl: String,
        accessToken: String,
        page: Int,
        pageSize: Int,
        filter: String?,
    ): ApiResult<ProcessPage> {
        val query = buildString {
            append("?page=").append(page)
            append("&pageSize=").append(pageSize)
            append("&sort=cpuPercent&direction=desc")
            if (!filter.isNullOrBlank()) {
                append("&filter=").append(encode(filter))
            }
        }
        return when (val parsed = execute("GET", serverUrl, SystemRoutes.PROCESS_QUERY + query, accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                val items = json.optJSONArray("items") ?: JSONArray()
                ProcessPage(
                    items = (0 until items.length()).map { index ->
                        val item = items.getJSONObject(index)
                        RemoteProcess(
                            pid = item.getInt("id"),
                            name = item.getString("name"),
                            cpuPercent = item.optDouble("cpuPercent", 0.0),
                            memoryBytes = item.optLong("memoryBytes"),
                            userName = item.optString("userName").takeIf { it.isNotBlank() },
                            threadCount = item.optInt("threadCount"),
                        )
                    },
                    totalCount = json.optInt("totalCount"),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed process page.") })
        }
    }

    override suspend fun killProcess(serverUrl: String, accessToken: String, pid: Int, force: Boolean): ApiResult<Unit> =
        execute("DELETE", serverUrl, SystemRoutes.processKill(pid) + "?force=$force", accessToken, null).asUnit()

    /** Streams a remote file into [target]. Used by the download action. */
    override suspend fun download(
        serverUrl: String,
        accessToken: String,
        path: String,
        target: File,
    ): ApiResult<Long> = withContext(Dispatchers.IO) {
        try {
            val connection = openConnection(serverUrl, FileRoutes.DOWNLOAD + "?path=" + encode(path), "GET", accessToken)
            connection.connect()
            val code = connection.responseCode
            if (code !in 200..299) {
                return@withContext readProblem(connection, code)
            }
            val written = connection.inputStream.use { input ->
                target.outputStream().use { output -> input.copyTo(output) }
            }
            ApiResult.Success(written)
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        }
    }

    private suspend fun execute(
        method: String,
        serverUrl: String,
        path: String,
        accessToken: String?,
        body: JsonBody?,
    ): ApiResult<String> = withContext(Dispatchers.IO) {
        try {
            val connection = openConnection(serverUrl, path, method, accessToken)
            if (body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                connection.connect()
                connection.outputStream.use { output: OutputStream -> body.writeTo(output) }
            } else {
                connection.connect()
            }
            val code = connection.responseCode
            if (code !in 200..299) {
                return@withContext readProblem(connection, code)
            }
            ApiResult.Success(connection.inputStream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty())
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        }
    }

    private fun openConnection(serverUrl: String, path: String, method: String, accessToken: String?): HttpURLConnection {
        val url = URL(serverUrl.trim().trimEnd('/') + path)
        return (url.openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = CONNECT_TIMEOUT_MILLIS
            readTimeout = READ_TIMEOUT_MILLIS
            setRequestProperty("Accept", "application/json")
            if (accessToken != null) {
                setRequestProperty("Authorization", "Bearer $accessToken")
            }
        }
    }

    private fun readProblem(connection: HttpURLConnection, code: Int): ApiResult<Nothing> {
        val text = connection.errorStream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
        // A 5xx is deliberately *not* a verdict about the submitted credential (design §5.8.2).
        if (code >= 500) {
            return ApiResult.Transport("Server returned HTTP $code.")
        }
        return runCatching {
            val json = JSONObject(text)
            ApiResult.Problem(
                status = code,
                code = ProblemCodes.from(json.optString("type"), json.optString("problemCode")),
                traceId = json.optString("traceId").takeIf { it.isNotBlank() },
            )
        }.getOrElse { ApiResult.Transport("Server returned HTTP $code.") }
    }

    private fun parseLogin(payload: String): ApiResult<LoginSession> = runCatching {
        val json = JSONObject(payload)
        val server = json.getJSONObject("server")
        val capabilities = server.optJSONArray("capabilities") ?: JSONArray()
        LoginSession(
            userName = json.getJSONObject("user").optString("name"),
            workspaceName = json.getJSONObject("workspace").optString("name"),
            server = ServerDescriptor(
                platform = server.optString("platform"),
                capabilities = (0 until capabilities.length()).map { capabilities.getString(it) }.toSet(),
            ),
            tokens = json.getJSONObject("tokens").let { tokens ->
                AuthTokens(
                    accessToken = tokens.getString("accessToken"),
                    refreshToken = tokens.getString("refreshToken"),
                    accessTokenExpiresAtMillis = IsoInstant.toEpochMillis(tokens.optString("accessTokenExpiresAt")),
                    refreshTokenExpiresAtMillis = IsoInstant.toEpochMillis(tokens.optString("refreshTokenExpiresAt")),
                )
            },
        )
    }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed login response.") })

    private fun ApiResult<String>.asUnit(): ApiResult<Unit> = when (this) {
        is ApiResult.Success -> ApiResult.Success(Unit)
        is ApiResult.Problem -> this
        is ApiResult.Transport -> this
    }

    private fun encode(value: String): String = URLEncoder.encode(value, "UTF-8")

    private fun JSONObject.optNullableLong(name: String): Long? = if (isNull(name)) null else optLong(name)

    private companion object {
        const val CLIENT_PLATFORM = "android"
        const val CONNECT_TIMEOUT_MILLIS = 15_000
        const val READ_TIMEOUT_MILLIS = 20_000

        fun defaultDeviceName(): String = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
    }
}

/** Route constants mirroring `AuthApiRoutes`. */
private object AuthRoutes {
    private const val V1 = "/api/v1.0"
    const val LOGIN = "$V1/auth/login"
    const val REFRESH = "$V1/auth/refresh"
    const val LOGOUT = "$V1/auth/logout"
}

/** Route constants mirroring `PrivilegedApiRoutes`. */
private object PrivilegedRoutes {
    const val ELEVATION = "/api/v1.0/privileged/elevation"
}

/** Route constants mirroring `FileApiRoutes`. */
private object FileRoutes {
    private const val V1 = "/api/v1.0"
    const val LIST = "$V1/files/list"
    const val PROPERTIES = "$V1/files/properties"
    const val ELEVATION = "$V1/files/elevation"
    const val DIRECTORY = "$V1/files/directory"
    const val DELETE = "$V1/files"
    const val RENAME = "$V1/files/rename"
    const val DOWNLOAD = "$V1/files/download"
}

/** Serialises one path into a JSON string literal. Paths are not secrets, so a plain builder is fine. */
private fun jsonString(value: String): String {
    val builder = StringBuilder("\"")
    for (char in value) {
        when {
            char == '"' -> builder.append("\\\"")
            char == '\\' -> builder.append("\\\\")
            char == '\n' -> builder.append("\\n")
            char == '\r' -> builder.append("\\r")
            char == '\t' -> builder.append("\\t")
            char < ' ' -> builder.append("\\u%04x".format(char.code))
            else -> builder.append(char)
        }
    }
    return builder.append('"').toString()
}

private fun jsonStringArray(values: List<String>): String = values.joinToString(",", "[", "]") { jsonString(it) }

/** Route constants mirroring `SystemMonitorApiRoutes`. */
private object SystemRoutes {
    private const val V1 = "/api/v1.0"
    const val PERFORMANCE_SNAPSHOT = "$V1/system/performance/snapshot"
    const val PROCESS_QUERY = "$V1/system/processes/query"

    fun processKill(pid: Int) = "$V1/system/processes/$pid"
}
