package app.relaxkonos.mobile.ui.common

import app.relaxkonos.mobile.R
import org.junit.Assert.assertEquals
import org.junit.Test

class OperationMessageTitleTest {
    @Test fun `already current catalog is informational instead of a failure`() {
        val notice = app.relaxkonos.mobile.ui.manage.deployments.deploymentProblem("application-catalog.already_current")
        assertEquals(StatusTone.Info, notice.tone)
        assertEquals(R.string.operation_info_title, operationMessageTitle(notice.tone))
    }
    @Test fun `every severity has an appropriate title`() {
        val expected = mapOf(
            StatusTone.Danger to R.string.operation_error_title,
            StatusTone.Warning to R.string.operation_warning_title,
            StatusTone.Success to R.string.operation_success_title,
            StatusTone.Neutral to R.string.operation_info_title,
            StatusTone.Primary to R.string.operation_info_title,
            StatusTone.Info to R.string.operation_info_title,
        )
        assertEquals(StatusTone.entries.toSet(), expected.keys)
        expected.forEach { (tone, title) -> assertEquals(tone.name, title, operationMessageTitle(tone)) }
    }
}
