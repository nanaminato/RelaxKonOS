package app.relaxkonos.mobile.ui.common

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import org.junit.Assert.assertEquals
import org.junit.Test

class FileReadFeedbackTest {
    @Test fun `file read problems give path specific recovery guidance`() {
        assertEquals(R.string.files_invalid_path, ApiResult.Problem(400, "invalid-path", null).fileReadFailureMessage()?.resId)
        assertEquals(R.string.files_path_missing, ApiResult.Problem(404, "not-found", null).fileReadFailureMessage()?.resId)
        assertEquals(R.string.files_device_unavailable, ApiResult.Problem(503, "device-unavailable", null).fileReadFailureMessage()?.resId)
    }

    @Test fun `other outcomes preserve existing authentication and transport feedback`() {
        listOf(ApiResult.Problem(401, "invalid-credential", null), ApiResult.Problem(403, "elevation-required", null),
            ApiResult.Problem(500, "not-found", null), ApiResult.Transport(null), ApiResult.Success(Unit)).forEach {
            assertEquals(it.failureMessage(), it.fileReadFailureMessage())
        }
    }
}
