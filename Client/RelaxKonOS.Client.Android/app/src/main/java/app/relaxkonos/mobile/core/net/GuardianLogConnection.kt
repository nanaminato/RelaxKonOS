package app.relaxkonos.mobile.core.net

import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.TypeReference
import io.reactivex.rxjava3.core.Completable
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

internal interface GuardianLogConnection {
    suspend fun connect()
    suspend fun subscribe(id: String): List<GuardianLog>
    suspend fun disconnect()
}

/** Current shared GuardianLogsHub contract; disconnecting never changes workload lifetime. */
internal class ServerGuardianLogConnection(url: String, token: String,
    snapshot: (List<GuardianLog>) -> Unit, closed: () -> Unit) : GuardianLogConnection {
    private val hub = HubConnectionBuilder.create(url.trimEnd('/') + "/hubs/guardian-logs")
        .withAccessTokenProvider(Single.just(token)).build()
    private val logType = object : TypeReference<List<GuardianLog>>() {}.type
    init {
        hub.on("OnLogSnapshot", { logs: List<GuardianLog> -> snapshot(logs) }, logType)
        hub.onClosed { closed() }
    }
    override suspend fun connect() = withContext(Dispatchers.IO) { hub.start().awaitGuardian() }
    override suspend fun subscribe(id: String): List<GuardianLog> = withContext(Dispatchers.IO) {
        hub.invoke<List<GuardianLog>>(logType, "Subscribe", id).awaitGuardian()
    }
    // The hub removes subscriptions on disconnect, including a failed Subscribe.
    override suspend fun disconnect() = withContext(Dispatchers.IO) { hub.stop().timeout(5, TimeUnit.SECONDS).awaitGuardian() }
}
private suspend fun Completable.awaitGuardian(): Unit = suspendCancellableCoroutine { continuation ->
    val disposable = timeout(20, TimeUnit.SECONDS).subscribe({ continuation.resume(Unit) }, continuation::resumeWithException)
    continuation.invokeOnCancellation { disposable.dispose() }
}
private suspend fun <T : Any> Single<T>.awaitGuardian(): T = suspendCancellableCoroutine { continuation ->
    val disposable = timeout(20, TimeUnit.SECONDS).subscribe(continuation::resume, continuation::resumeWithException)
    continuation.invokeOnCancellation { disposable.dispose() }
}
