package app.relaxkonos.mobile.ui.files

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.UiMessage
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ImagePreviewRecoveryTest {
    @get:Rule val rule = createComposeRule()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var preview: ImagePreview by mutableStateOf(ImagePreview.Hidden)
    private val requests = mutableListOf<Boolean>()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun node(id: Int) = rule.onNodeWithText(context.getString(id))
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }

    private fun show(onRead: suspend (Boolean) -> Unit) {
        rule.setContent {
            MaterialTheme {
                PreviewActions(preview, {}, { authorize ->
                    requests += authorize
                    preview = ImagePreview.Downloading(0, null)
                    scope.launch {
                        ImagePreviewRequest({ true }, {
                            preview = ImagePreview.Unavailable(UiMessage(R.string.files_preview_failed), false)
                        }).run { onRead(authorize) }
                    }
                })
            }
        }
    }

    @Test fun cancelledRetryReturnsActionAndNeverAuthorizesOrReplaysAutomatically() {
        rule.runOnUiThread {
            preview = ImagePreview.Unavailable(UiMessage(R.string.files_preview_failed), false)
        }
        show { throw CancellationException("private detail") }
        node(R.string.common_retry).assertIsDisplayed().performClick()
        rule.waitUntil(5_000) { requests.size == 1 && preview is ImagePreview.Unavailable }
        node(R.string.common_retry).assertIsDisplayed()
        node(R.string.files_preview_authorize).assertDoesNotExist()
        rule.onNodeWithText("private detail", substring = true).assertDoesNotExist()
        rule.runOnIdle { assertEquals(listOf(false), requests) }
        node(R.string.common_retry).performClick()
        rule.waitUntil(5_000) { requests.size == 2 && preview is ImagePreview.Unavailable }
        rule.runOnIdle { assertEquals(listOf(false, false), requests) }
    }

    @Test fun authorizationRequiresItsButtonAndLoadingRemovesSubmissionActions() {
        val pending = CompletableDeferred<Unit>()
        rule.runOnUiThread {
            preview = ImagePreview.Unavailable(UiMessage(R.string.files_preview_elevation), true)
        }
        show { pending.await() }
        node(R.string.common_retry).assertDoesNotExist()
        rule.runOnIdle { assertTrue(requests.isEmpty()) }
        node(R.string.files_preview_authorize).assertIsDisplayed().performClick()
        rule.waitUntil(5_000) { preview is ImagePreview.Downloading }
        node(R.string.files_preview_authorize).assertDoesNotExist()
        node(R.string.common_retry).assertDoesNotExist()
        rule.runOnIdle { assertEquals(listOf(true), requests) }
        rule.runOnUiThread { pending.completeExceptionally(CancellationException()) }
        rule.waitUntil(5_000) { preview is ImagePreview.Unavailable }
        node(R.string.common_retry).assertIsDisplayed()
        node(R.string.files_preview_authorize).assertDoesNotExist()
        rule.runOnIdle { assertEquals(listOf(true), requests) }
    }
}
