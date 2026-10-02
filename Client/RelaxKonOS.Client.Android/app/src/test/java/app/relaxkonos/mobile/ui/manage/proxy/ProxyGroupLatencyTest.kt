package app.relaxkonos.mobile.ui.manage.proxy

import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProxyGroupLatencyTest {
    @Test fun `tests the whole group with bounded concurrency and incremental results`() = runTest {
        val names = (1..11).map { "node $it" }
        var running = 0; var maximum = 0
        val results = mutableListOf<String>()
        testProxyGroupLatency(ProxyGroup("group", "Selector", names.first(), names + names.first()), test = { name ->
            running++; maximum = maxOf(maximum, running)
            delay(100)
            running--
            ApiResult.Success(ProxyDelay(name, 42, false, ""))
        }, onResult = { name, _ -> results += name })
        assertEquals(4, maximum)
        assertEquals(names, results)
        assertEquals(300L, testScheduler.currentTime)
    }

    @Test fun `a failed node does not suppress successful or timed out results`() = runTest {
        val results = mutableMapOf<String, ApiResult<ProxyDelay>>()
        testProxyGroupLatency(ProxyGroup("group", "URLTest", "ok", listOf("broken", "timeout", "ok")), test = { name ->
            when (name) {
                "broken" -> throw IllegalStateException("network unavailable")
                "timeout" -> ApiResult.Success(ProxyDelay(name, null, true, ""))
                else -> ApiResult.Success(ProxyDelay(name, 24, false, ""))
            }
        }, onResult = { name, result -> results[name] = result })
        assertTrue(results["broken"] is ApiResult.Transport)
        assertTrue((results["timeout"] as ApiResult.Success).value.timedOut)
        assertEquals(24, (results["ok"] as ApiResult.Success).value.delayMilliseconds)
    }

    @Test fun `session cancellation propagates without publishing a failed result`() = runTest {
        val results = mutableListOf<String>()
        val result = runCatching {
            testProxyGroupLatency(ProxyGroup("group", "Selector", "node", listOf("node")),
                test = { throw CancellationException("session changed") }, onResult = { name, _ -> results += name })
        }
        assertTrue(result.exceptionOrNull() is CancellationException)
        assertTrue(results.isEmpty())
    }
}
