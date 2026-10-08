package app.relaxkonos.mobile.ui.common

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult

internal fun ApiResult<*>.fileReadFailureMessage(): UiMessage? {
    if (this is ApiResult.Problem) {
        when (status to code) {
            400 to "invalid-path" -> return UiMessage(R.string.files_invalid_path)
            404 to "not-found" -> return UiMessage(R.string.files_path_missing)
            503 to "device-unavailable" -> return UiMessage(R.string.files_device_unavailable)
        }
    }
    return failureMessage()
}
