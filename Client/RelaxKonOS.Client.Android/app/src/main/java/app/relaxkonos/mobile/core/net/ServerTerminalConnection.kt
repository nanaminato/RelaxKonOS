package app.relaxkonos.mobile.core.net

import android.util.Base64
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.TypeReference
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** The Server owns PTY lifetime. Closing this transport only detaches its SignalR connection. */
class ServerTerminalConnection(
    baseUrl: String,
    token: () -> String?,
    private val onOutput: (ByteArray) -> Unit,
    private val onExit: (Int) -> Unit,
    private val onDisconnect: () -> Unit,
) : AutoCloseable {
    private val hub: HubConnection = HubConnectionBuilder.create(baseUrl.trimEnd('/') + "/hubs/terminals")
        .withAccessTokenProvider(Single.defer { Single.just(token() ?: "") })
        .build()

    init {
        hub.on("OnOutput", { encoded: String -> onOutput(Base64.decode(encoded, Base64.DEFAULT)) }, String::class.java)
        hub.on("OnProcessExited", { code: Int -> onExit(code) }, Int::class.javaObjectType)
        hub.onClosed { _ -> onDisconnect() }
    }

    suspend fun connect() = withContext(Dispatchers.IO) { hub.start().blockingAwait() }

    suspend fun sessions(): List<TerminalSessionSummary> = withContext(Dispatchers.IO) {
        val type = object : TypeReference<List<TerminalSessionSummary>>() {}.type
        hub.invoke<List<TerminalSessionSummary>>(type, "ListSessions").blockingGet()
    }

    suspend fun attach(sessionId: String?, columns: Int, rows: Int): TerminalAttachment = withContext(Dispatchers.IO) {
        if (sessionId == null) {
            // The hub method is Start(request, sessionId). SignalR rejects a call whose argument count
            // does not match the method signature - it never applies the C# default value - so the second
            // argument has to be sent explicitly even when no existing session is being resumed.
            val request = TerminalStartRequest(columns, rows, 0, 0, null, null)
            hub.invoke(TerminalAttachment::class.java, "Start", request, null).blockingGet()
        } else hub.invoke(TerminalAttachment::class.java, "AttachExisting", sessionId).blockingGet().also {
            hub.invoke("Resize", columns, rows, 0, 0).blockingAwait()
        }
    }

    suspend fun input(bytes: ByteArray) = withContext(Dispatchers.IO) {
        bytes.asList().chunked(16 * 1024).forEach { chunk ->
            hub.invoke("Input", Base64.encodeToString(chunk.toByteArray(), Base64.NO_WRAP)).blockingAwait()
        }
    }
    suspend fun resize(columns: Int, rows: Int) = withContext(Dispatchers.IO) {
        hub.invoke("Resize", columns, rows, 0, 0).blockingAwait()
    }
    suspend fun terminate() = withContext(Dispatchers.IO) { hub.invoke("Close").blockingAwait() }
    override fun close() { hub.stop().blockingAwait() }
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
