package app.relaxkonos.mobile.ui.files

import app.relaxkonos.mobile.core.net.ApiResult
import kotlinx.coroutines.CancellationException

internal suspend fun <T> fileMutationRequest(request: suspend () -> ApiResult<T>): ApiResult<T> =
    try {
        request()
    } catch (cancelled: CancellationException) {
        throw cancelled
    } catch (_: Exception) {
        ApiResult.Transport(null)
    }
