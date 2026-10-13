package app.relaxkonos.mobile.ui.manage.tunnels

import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TunnelFacts

internal fun TunnelsState.withReadResults(
    facts: ApiResult<TunnelFacts>, runtime: ApiResult<TunnelRuntime>,
    frps: ApiResult<ManagedFrps>, checkedAtMillis: Long,
): TunnelsState = copy(
    facts = facts, runtime = runtime, frps = frps, frpsAtMillis = checkedAtMillis,
    problemCode = listOf(facts, runtime, frps).filterIsInstance<ApiResult.Problem>().firstOrNull()?.code,
    uncertain = facts !is ApiResult.Success || runtime !is ApiResult.Success || frps !is ApiResult.Success,
)
