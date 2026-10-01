package app.relaxkonos.mobile.core.net

import com.google.gson.JsonElement
import com.microsoft.signalr.HubConnectionBuilder
import io.reactivex.rxjava3.core.Completable
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

internal interface PerformanceConnection {
    suspend fun connect()
    suspend fun subscribe()
    suspend fun disconnect()
}

/** Shared PerformanceHub: raw JSON is validated before it becomes a metric, including nulls. */
internal class ServerPerformanceConnection(url: String, token: String,
    snapshot: (PerformanceSnapshot) -> Unit, closed: () -> Unit) : PerformanceConnection {
    private val hub = HubConnectionBuilder.create(url.trimEnd('/') + "/hubs/performance")
        .withAccessTokenProvider(Single.just(token)).build()
    init {
        hub.on("OnPerformanceSnapshot", { json: JsonElement ->
            runCatching { PerformanceWire.snapshot(json.toString()) }.onSuccess(snapshot).onFailure { closed() }
        }, JsonElement::class.java)
        hub.onClosed { closed() }
    }
    override suspend fun connect() = withContext(Dispatchers.IO) { hub.start().awaitPerformance() }
    override suspend fun subscribe() = withContext(Dispatchers.IO) { hub.invoke("Subscribe").awaitPerformance() }
    // Disconnect removes the server subscription even when the handshake or Subscribe failed.
    override suspend fun disconnect() = withContext(Dispatchers.IO) { hub.stop().timeout(5, TimeUnit.SECONDS).awaitPerformance() }
}
private suspend fun Completable.awaitPerformance(): Unit = suspendCancellableCoroutine { continuation ->
    val disposable = timeout(20, TimeUnit.SECONDS).subscribe({ continuation.resume(Unit) }, continuation::resumeWithException)
    continuation.invokeOnCancellation { disposable.dispose() }
}
