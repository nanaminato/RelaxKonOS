package app.relaxkonos.mobile.core.net

import android.util.Base64
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.HttpRequestException
import com.microsoft.signalr.TypeReference
import io.reactivex.rxjava3.core.Completable
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.suspendCancellableCoroutine
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Injectable transport: reconnect and session selection can be verified without an Android PTY. */
interface TerminalConnection {
    suspend fun connect()
    suspend fun sessions(): List<TerminalSessionSummary>
    suspend fun attach(sessionId: String?, columns: Int, rows: Int): TerminalAttachment
    suspend fun input(bytes: ByteArray)
    suspend fun resize(columns: Int, rows: Int)
    suspend fun closeSession(sessionId: String)
    suspend fun disconnect()
}

internal fun terminalConnectionFailure(error: Throwable): ApiResult<Nothing> {
    val http = generateSequence(error) { it.cause }.filterIsInstance<HttpRequestException>().firstOrNull()
    return if (http != null && http.statusCode < 500) ApiResult.Problem(http.statusCode,
        if (http.statusCode == 401) ProblemCodes.UNAUTHORIZED else "http.${http.statusCode}", null)
    else ApiResult.Transport(error.message)
}

/** The Server owns PTY lifetime. Closing this transport only detaches its SignalR connection. */
class ServerTerminalConnection(
    baseUrl: String,
    token: () -> String?,
    private val onOutput: (ByteArray) -> Unit,
    private val onExit: (Int) -> Unit,
    private val onDisconnect: () -> Unit,
) : TerminalConnection {
    private val hub: HubConnection = HubConnectionBuilder.create(baseUrl.trimEnd('/') + "/hubs/terminals")
        .withAccessTokenProvider(Single.defer { Single.just(token() ?: "") })
        .build()

    init {
        hub.on("OnOutput", { encoded: String -> onOutput(Base64.decode(encoded, Base64.DEFAULT)) }, String::class.java)
        hub.on("OnProcessExited", { code: Int -> onExit(code) }, Int::class.javaObjectType)
        hub.onClosed { _ -> onDisconnect() }
    }

    override suspend fun connect() = withContext(Dispatchers.IO) { hub.start().await() }

    override suspend fun sessions(): List<TerminalSessionSummary> = withContext(Dispatchers.IO) {
        val type = object : TypeReference<List<TerminalSessionSummary>>() {}.type
        hub.invoke<List<TerminalSessionSummary>>(type, "ListSessions").await()
    }

    override suspend fun attach(sessionId: String?, columns: Int, rows: Int): TerminalAttachment = withContext(Dispatchers.IO) {
        if (sessionId == null) {
            // The hub method is Start(request, sessionId). SignalR rejects a call whose argument count
            // does not match the method signature - it never applies the C# default value - so the second
            // argument has to be sent explicitly even when no existing session is being resumed.
            val request = TerminalStartRequest(columns, rows, 0, 0, null, null)
            hub.invoke(TerminalAttachment::class.java, "Start", request, null).await()
        } else hub.invoke(TerminalAttachment::class.java, "AttachExisting", sessionId).await().also {
            hub.invoke("Resize", columns, rows, 0, 0).await()
        }
    }

    override suspend fun input(bytes: ByteArray) = withContext(Dispatchers.IO) {
        bytes.asList().chunked(16 * 1024).forEach { chunk ->
            hub.invoke("Input", Base64.encodeToString(chunk.toByteArray(), Base64.NO_WRAP)).await()
        }
    }
    override suspend fun resize(columns: Int, rows: Int) = withContext(Dispatchers.IO) {
        hub.invoke("Resize", columns, rows, 0, 0).await()
    }
    /**
     * Terminates one Server session by ID.
     *
     * The ID is sent explicitly because the phone closes sessions it is not attached to: the Server
     * only drops the caller's attachment when the closed session happens to be the attached one.
     */
    override suspend fun closeSession(sessionId: String) = withContext(Dispatchers.IO) {
        hub.invoke("CloseSession", sessionId).await()
    }
    override suspend fun disconnect() = withContext(Dispatchers.IO) { hub.stop().timeout(5, TimeUnit.SECONDS).await() }
}

private suspend fun Completable.await(): Unit = suspendCancellableCoroutine { continuation ->
    val disposable = timeout(20, TimeUnit.SECONDS).subscribe({ continuation.resume(Unit) }, continuation::resumeWithException)
    continuation.invokeOnCancellation { disposable.dispose() }
}

private suspend fun <T : Any> Single<T>.await(): T = suspendCancellableCoroutine { continuation ->
    val disposable = timeout(20, TimeUnit.SECONDS).subscribe(continuation::resume, continuation::resumeWithException)
    continuation.invokeOnCancellation { disposable.dispose() }
}

class TerminalSessionSummary {
    var sessionId: String = ""
    var createdAt: String = ""
    var hasExited: Boolean = false
}

class TerminalAttachment {
    var sessionId: String = ""
    var created: Boolean = false
}

private data class TerminalStartRequest(
    val columns: Int,
    val rows: Int,
    val widthPixels: Int,
    val heightPixels: Int,
    val shell: String?,
    val workingDirectory: String?,
)
