package app.relaxkonos.mobile.core.net

import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class ServerEndpointDiscoveryCancellationTest {
    @Test fun cancelledBlockingHttpsProbeDoesNotContinueToHttpFallback() = runBlocking {
        val server = ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"))
        server.soTimeout = 8_000
        val firstConnection = CompletableFuture<Socket>()
        val firstSocket = AtomicReference<Socket?>()
        val fallbackRequests = AtomicInteger()
        val serverFailure = AtomicReference<Throwable?>()
        val serving = thread(isDaemon = true, name = "endpoint-cancellation-test") {
            try {
                val first = server.accept()
                firstSocket.set(first)
                first.soTimeout = 5_000
                first.getInputStream().read() // The TLS client hello proves the blocking probe started.
                firstConnection.complete(first)
                server.soTimeout = 2_000
                while (!server.isClosed) {
                    val next = try { server.accept() } catch (_: SocketTimeoutException) { break }
                    next.use {
                        it.soTimeout = 1_000
                        if (it.getInputStream().read() == 'O'.code) {
                            fallbackRequests.incrementAndGet()
                            it.getOutputStream().write("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
                            it.getOutputStream().flush()
                        }
                    }
                }
            } catch (error: SocketException) {
                if (!server.isClosed) serverFailure.set(error)
            } catch (error: Throwable) { serverFailure.set(error) }
        }
        val probe = async(Dispatchers.Default) { ServerEndpointDiscovery.discover("127.0.0.1:${server.localPort}") }
        try {
            val first = firstConnection.get(8, TimeUnit.SECONDS)
            probe.cancel()
            first.close() // Unblock the non-suspending HTTPS handshake after cancellation.
            withTimeout(6_000) { probe.join() }
            serving.join(3_000)
            assertFalse("The loopback test server must finish", serving.isAlive)
            assertNull(serverFailure.get())
            assertEquals("No credential-free HTTP fallback may be issued after cancellation", 0, fallbackRequests.get())
        } finally {
            server.close()
            firstSocket.get()?.close()
            probe.cancelAndJoin()
            serving.join(3_000)
        }
    }
}
