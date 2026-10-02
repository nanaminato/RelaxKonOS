package app.relaxkonos.mobile.ui.manage.proxy

import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ProxyDelay
import app.relaxkonos.mobile.core.net.ProxyGroup
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit

/** Bound host load while publishing each result as soon as its request completes. */
internal suspend fun testProxyGroupLatency(
    group: ProxyGroup,
    test: suspend (String) -> ApiResult<ProxyDelay>,
    onResult: (String, ApiResult<ProxyDelay>) -> Unit,
) = coroutineScope {
    val requests = Semaphore(4)
    group.proxies.distinct().map { proxy ->
        async {
            requests.withPermit {
                val result = try { test(proxy) }
                catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { ApiResult.Transport(null) }
                onResult(proxy, result)
            }
        }
    }.awaitAll()
}
