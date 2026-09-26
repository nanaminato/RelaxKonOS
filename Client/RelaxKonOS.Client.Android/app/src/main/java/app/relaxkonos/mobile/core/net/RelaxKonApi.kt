package app.relaxkonos.mobile.core.net

import android.os.Build
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
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
                        mimeType = item.optNullableString("mimeType"),
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

    override suspend fun move(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> {
        val body = JsonBody().string("sourcePath", sourcePath).string("destinationPath", destinationPath).bool("overwrite", false)
        return execute("POST", serverUrl, FileRoutes.MOVE, accessToken, body).asUnit()
    }

    override suspend fun copy(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> {
        val body = JsonBody().string("sourcePath", sourcePath).string("destinationPath", destinationPath).bool("overwrite", false)
        return execute("POST", serverUrl, FileRoutes.COPY, accessToken, body).asUnit()
    }

    override suspend fun upload(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        source: InputStream,
        contentLength: Long?,
        onProgress: ((Long) -> Unit)?,
    ): ApiResult<Unit> = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            val boundary = "RelaxKonOS-${System.currentTimeMillis()}"
            val header = buildMultipartHeader(boundary, fileName)
            val footer = "\r\n--$boundary--\r\n".toByteArray(Charsets.UTF_8)
            connection = openConnection(serverUrl, FileRoutes.UPLOAD + "?path=" + encode(targetDirectoryPath), "POST", accessToken)
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            if (contentLength != null && contentLength >= 0) {
                val total = header.size.toLong() + contentLength + footer.size
                if (total <= Int.MAX_VALUE) connection.setFixedLengthStreamingMode(total.toInt()) else connection.setFixedLengthStreamingMode(total)
            } else {
                connection.setChunkedStreamingMode(BUFFER_SIZE)
            }
            connection.outputStream.use { output ->
                output.write(header)
                source.use { input ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    var written = 0L
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        output.write(buffer, 0, count)
                        written += count
                        onProgress?.invoke(written)
                    }
                }
                output.write(footer)
            }
            val code = connection.responseCode
            if (code !in 200..299) return@withContext readProblem(connection, code)
            ApiResult.Success(Unit)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    override suspend fun createUploadSession(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        length: Long,
        lastModifiedMillis: Long?,
        idempotencyKey: String,
    ): ApiResult<UploadSession> {
        val body = JsonBody()
            .string("targetDirectoryPath", targetDirectoryPath)
            .string("fileName", fileName)
            .raw("length", length.toString())
            .raw("lastModifiedUtc", lastModifiedMillis?.let { "\"${IsoInstant.fromEpochMillis(it)}\"" } ?: "null")
        return when (val parsed = execute(
            "POST",
            serverUrl,
            FileRoutes.UPLOADS,
            accessToken,
            body,
            headers = mapOf(UploadProtocol.IDEMPOTENCY_KEY_HEADER to idempotencyKey),
        )) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> parseUploadSession(parsed.value)
        }
    }

    override suspend fun uploadSession(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
    ): ApiResult<UploadSession> =
        when (val parsed = execute("GET", serverUrl, FileRoutes.upload(uploadId), accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> parseUploadSession(parsed.value)
        }

    override suspend fun sendUploadChunk(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        offset: Long,
        chunkLength: Long,
        source: InputStream,
        onInFlight: ((Long) -> Unit)?,
    ): UploadChunkResult = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            // One chunk, one connection. A pooled keep-alive socket that survived a network change is
            // exactly the thing that must not be reused here, so the request never depends on one.
            connection = openConnection(
                serverUrl,
                FileRoutes.upload(uploadId),
                "PATCH",
                accessToken,
                readTimeoutMillis = CHUNK_READ_TIMEOUT_MILLIS,
            )
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/octet-stream")
            connection.setRequestProperty(UploadProtocol.OFFSET_HEADER, offset.toString())
            // Declaring the length is what gives the server a Content-Length to hold the chunk to; a
            // chunk is far below 2 GiB, so the long overload's ceiling is never reached.
            connection.setFixedLengthStreamingMode(chunkLength)
            connection.outputStream.use { output ->
                val buffer = ByteArray(BUFFER_SIZE)
                var written = 0L
                while (written < chunkLength) {
                    val count = source.read(buffer, 0, minOf(buffer.size.toLong(), chunkLength - written).toInt())
                    if (count < 0) break
                    output.write(buffer, 0, count)
                    written += count
                    onInFlight?.invoke(written)
                }
                if (written < chunkLength) {
                    // The source stopped early: the document changed under us. Sending the remainder of
                    // a file that no longer exists would publish a mixture of two versions.
                    throw TruncatedSourceException(written)
                }
            }
            val code = connection.responseCode
            val authoritative = connection.getHeaderField(UploadProtocol.OFFSET_HEADER)?.trim()?.toLongOrNull()
            if (code in 200..299) {
                // The server always names the offset on 204. The declared end is the same number and is
                // only used when something between us dropped the header, never in preference to it.
                return@withContext UploadChunkResult.Confirmed(authoritative ?: (offset + chunkLength))
            }
            val problem = readProblemBody(connection, code)
                ?: return@withContext UploadChunkResult.Unreachable("Server returned HTTP $code.")
            UploadChunkResult.Refused(code, problem.code, authoritative)
        } catch (error: CancellationException) {
            throw error
        } catch (error: TruncatedSourceException) {
            UploadChunkResult.SourceShort(error.deliveredBytes)
        } catch (error: Exception) {
            UploadChunkResult.Unreachable(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    override suspend fun commitUpload(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        contentHash: String?,
    ): ApiResult<Unit> {
        val body = JsonBody().string("contentHash", contentHash)
        return execute("POST", serverUrl, FileRoutes.uploadCommit(uploadId), accessToken, body).asUnit()
    }

    override suspend fun abortUpload(serverUrl: String, accessToken: String, uploadId: String): ApiResult<Unit> =
        execute("DELETE", serverUrl, FileRoutes.upload(uploadId), accessToken, null).asUnit()

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
                            userName = item.optNullableString("userName"),
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

    /** Streams a remote file into [sink]. Used by the download action. */
    override suspend fun download(
        serverUrl: String,
        accessToken: String,
        path: String,
        sink: DownloadSink,
        onProgress: ((writtenBytes: Long, totalBytes: Long?) -> Unit)?,
    ): ApiResult<Long> = streamInto(
        serverUrl = serverUrl,
        accessToken = accessToken,
        route = FileRoutes.DOWNLOAD,
        query = "?path=" + encode(path),
        sink = sink,
        onProgress = onProgress,
    )

    override suspend fun thumbnail(
        serverUrl: String,
        accessToken: String,
        path: String,
        maxEdge: Int,
    ): ApiResult<ByteArray> {
        val buffer = ByteArrayOutputStream()
        val result = streamInto(
            serverUrl = serverUrl,
            accessToken = accessToken,
            route = FileRoutes.THUMBNAIL,
            // `maxEdge` is a plain count, so it needs no encoding; the path does.
            query = "?path=" + encode(path) + "&maxEdge=" + maxEdge,
            sink = { buffer },
            onProgress = null,
        )
        // The buffer is the answer, so the byte count the transfer reported is discarded and the
        // bytes themselves are returned. A thumbnail is small by construction; that is what makes
        // holding one in memory reasonable at all.
        return when (result) {
            is ApiResult.Success -> ApiResult.Success(buffer.toByteArray())
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }

    /**
     * Streams one GET body into [sink].
     *
     * A download and a thumbnail differ in the route they ask and what comes back; everything that
     * makes the transfer itself correct is the same, and it is the part that has to stay correct —
     * the destination is opened only after the server has accepted the request, so a refused or
     * unauthorized transfer writes nothing anywhere.
     */
    private suspend fun streamInto(
        serverUrl: String,
        accessToken: String,
        route: String,
        query: String,
        sink: DownloadSink,
        onProgress: ((writtenBytes: Long, totalBytes: Long?) -> Unit)?,
    ): ApiResult<Long> = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            connection = openConnection(serverUrl, route + query, "GET", accessToken)
            connection.connect()
            val code = connection.responseCode
            if (code !in 200..299) {
                return@withContext readProblem(connection, code)
            }
            val total = connection.contentLengthLong.takeIf { it >= 0 }
            val written = connection.inputStream.use { input ->
                sink.open().use { output ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    var copied = 0L
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        output.write(buffer, 0, count)
                        copied += count
                        onProgress?.invoke(copied, total)
                    }
                    copied
                }
            }
            ApiResult.Success(written)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    private suspend fun execute(
        method: String,
        serverUrl: String,
        path: String,
        accessToken: String?,
        body: JsonBody?,
        headers: Map<String, String> = emptyMap(),
    ): ApiResult<String> = withContext(Dispatchers.IO) {
        try {
            val connection = openConnection(serverUrl, path, method, accessToken)
            for ((name, value) in headers) {
                connection.setRequestProperty(name, value)
            }
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

    private fun openConnection(
        serverUrl: String,
        path: String,
        method: String,
        accessToken: String?,
        readTimeoutMillis: Int = READ_TIMEOUT_MILLIS,
    ): HttpURLConnection {
        val url = URL(serverUrl.trim().trimEnd('/') + path)
        return (url.openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = CONNECT_TIMEOUT_MILLIS
            readTimeout = readTimeoutMillis
            setRequestProperty("Accept", "application/json")
            if (accessToken != null) {
                setRequestProperty("Authorization", "Bearer $accessToken")
            }
        }
    }

    /**
     * Turns a non-2xx response into a verdict.
     *
     * The 4xx/5xx split is decided by [readsAsProblem]: a 4xx body is always a Problem, a 5xx body only
     * when the server named a RelaxKonOS problem code. A body that is not JSON at all is never a verdict
     * — it is the transport failure it looks like.
     */
    private fun readProblem(connection: HttpURLConnection, code: Int): ApiResult<Nothing> {
        val problem = readProblemBody(connection, code)
            ?: return ApiResult.Transport("Server returned HTTP $code.")
        return ApiResult.Problem(status = code, code = problem.code, traceId = problem.traceId)
    }

    /**
     * Reads a ProblemDetails body, or `null` when the response is not a contract answer.
     *
     * Split out of [readProblem] because the chunk route needs the code *and* keeps the connection open
     * to read the authoritative `Upload-Offset` header, which a sealed [ApiResult] cannot carry.
     */
    private fun readProblemBody(connection: HttpURLConnection, status: Int): ProblemBody? {
        val text = connection.errorStream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
        val json = runCatching { JSONObject(text) }.getOrNull() ?: return null
        val type = json.optNullableString("type")
        val problemCode = json.optNullableString("problemCode")
        if (!readsAsProblem(status, type, problemCode)) {
            return null
        }
        return ProblemBody(code = ProblemCodes.from(type, problemCode), traceId = json.optNullableString("traceId"))
    }

    private fun parseUploadSession(payload: String): ApiResult<UploadSession> = runCatching {
        val json = JSONObject(payload)
        UploadSession(
            uploadId = json.getString("uploadId"),
            offset = json.getLong("offset"),
            length = json.getLong("length"),
            chunkSize = json.optInt("chunkSize", UploadProtocol.DEFAULT_CHUNK_SIZE),
            elevated = json.optBoolean("elevated"),
            expiresAtMillis = IsoInstant.toEpochMillis(json.optString("expiresAt")),
        )
    }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed upload session.") })

    private fun parseLogin(payload: String): ApiResult<LoginSession> = runCatching {
        val json = JSONObject(payload)
        val server = json.getJSONObject("server")
        val capabilities = server.optJSONArray("capabilities") ?: JSONArray()
        // Required, not optional: the answer decides whether the shell warns before the first folder
        // open, and a missing one is a contract break rather than "assume usable".
        val eligibility = json.getJSONObject("executionEligibility")
        LoginSession(
            // `UserDto` names the field `username`; reading `name` left the account blank on the home page.
            userName = json.getJSONObject("user").optString("username"),
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
            executionEligibility = ExecutionEligibility(
                available = eligibility.getBoolean("available"),
                // Null when the identity is usable, so the absent-reason case is the normal one.
                reason = eligibility.optNullableString("reason"),
            ),
        )
    }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed login response.") })

    private fun ApiResult<String>.asUnit(): ApiResult<Unit> = when (this) {
        is ApiResult.Success -> ApiResult.Success(Unit)
        is ApiResult.Problem -> this
        is ApiResult.Transport -> this
    }

    private fun encode(value: String): String = URLEncoder.encode(value, "UTF-8")

    private fun buildMultipartHeader(boundary: String, fileName: String): ByteArray {
        // A Content-Disposition filename is a header value, not a display string. Remove control
        // characters and delimiters so a malicious provider cannot inject a second MIME header.
        val safeName = fileName.replace(Regex("[\\r\\n\\\"\\\\]"), "_")
        return (
            "--$boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"$safeName\"\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n"
            ).toByteArray(Charsets.UTF_8)
    }

    private fun JSONObject.optNullableLong(name: String): Long? = if (isNull(name)) null else optLong(name)

    /**
     * Reads a string the server is allowed to leave empty.
     *
     * `optString` alone cannot answer this. Android's `org.json` renders a JSON null as the four-letter
     * string "null" (`JSON.toString(JSONObject.NULL)` -> `String.valueOf(NULL)`), so a blank-check keeps
     * it and the field reaches the UI as that literal word — which is exactly what put "null" in every
     * row of the process list: a Windows server never resolves a process owner, so `userName` is
     * genuinely null on the wire. Only `isNull` separates "the server sent nothing" from "the server
     * sent a value", and it answers the same way on Android and on the reference `org.json`.
     */
    private fun JSONObject.optNullableString(name: String): String? =
        if (isNull(name)) null else optString(name).takeIf { it.isNotBlank() }

    private companion object {
        const val CLIENT_PLATFORM = "android"
        const val CONNECT_TIMEOUT_MILLIS = 15_000
        const val READ_TIMEOUT_MILLIS = 20_000

        /**
         * Read timeout of one chunk request.
         *
         * Deliberately not the 20 s the rest of the API uses: an 8 MiB chunk on a slow mobile link
         * legitimately takes minutes, and a timeout there would abort a transfer that is making
         * progress. A stalled chunk is instead answered by the loop's backoff, which retries after
         * asking the server where it actually is.
         */
        const val CHUNK_READ_TIMEOUT_MILLIS = 300_000
        const val BUFFER_SIZE = 81_920

        fun defaultDeviceName(): String = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
    }
}

/** The code and trace id of one ProblemDetails response, read while the connection is still open. */
private class ProblemBody(val code: String, val traceId: String?)

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
    const val MOVE = "$V1/files/move"
    const val COPY = "$V1/files/copy"
    const val UPLOAD = "$V1/files/upload"
    const val DOWNLOAD = "$V1/files/download"
    const val THUMBNAIL = "$V1/files/thumbnail"

    /** Resumable upload sessions: POST opens one, GET reads its offset, DELETE abandons it. */
    const val UPLOADS = "$V1/files/uploads"

    /**
     * The session sub-resource and its chunk route, which are the same URL under different methods.
     *
     * [uploadId] is issued by the server as 32 lowercase hex characters, so no escaping is involved;
     * it is never built from anything the user typed.
     */
    fun upload(uploadId: String) = "$UPLOADS/$uploadId"

    fun uploadCommit(uploadId: String) = "${upload(uploadId)}/commit"
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
