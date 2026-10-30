package app.relaxkonos.mobile.ui.manage.components

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.graphics.asAndroidBitmap
import android.graphics.Bitmap
import java.io.File
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ComponentChange
import app.relaxkonos.mobile.core.net.ComponentPlanResult
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

/** Production review UI with frozen Windows-host metadata; no native installer is invoked. */
class TailscaleInstallationReviewTest {
    @get:Rule val rule = createComposeRule()
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val url = "https://pkgs.tailscale.com/stable/tailscale-setup-1.104.4-amd64.msi"
    private fun capture(name: String) {
        val file = File(context.getExternalFilesDir(null), "component-review/$name-${context.resources.displayMetrics.widthPixels}.png")
        file.parentFile!!.mkdirs()
        file.outputStream().use { rule.onNode(isDialog()).captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it) }
    }
    private fun review(change: ComponentChange) = ComponentChangeReview("tailscale", change, true,
        ComponentPlanResult("tailscale", change, emptyList(), emptyList(), """{"hostId":"windows11-host"}""",
            """{"mode":"nativeInstaller","installer":{"version":"1.104.4","downloadUrl":"$url","sha256":"${"a".repeat(64)}"}}"""), "1.104.4")

    @Test fun cancelDoesNotSubmitAndConfirmationRemainsVisibleWithLargeText() {
        val visible = mutableStateOf(true)
        var submitted = 0
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                MaterialTheme { if (visible.value) ChangeDialog(review(ComponentChange.Install), false,
                    { submitted++; visible.value = false }, { visible.value = false }) }
            }
        }
        rule.onNodeWithText("windows11-host", substring = true).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(url).performScrollTo().assertIsDisplayed()
        capture("tailscale-install-source")
        rule.onNodeWithText("SHA-256 ${"a".repeat(64)}").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.components_tailscale_change_warning)).performScrollTo().assertIsDisplayed()
        capture("tailscale-install-warning")
        rule.onNodeWithText(context.getString(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertEquals(0, submitted); visible.value = true }
        rule.onNodeWithText(context.getString(R.string.components_action_install)).assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals(1, submitted) }
    }

    @Test fun uninstallExplainsRetainedDataAndBusyReviewCannotSubmit() {
        var submitted = 0
        rule.setContent { MaterialTheme { ChangeDialog(review(ComponentChange.Uninstall), true, { submitted++ }, {}) } }
        rule.onNodeWithText(context.getString(R.string.components_change_keep_data)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.components_action_uninstall)).assertIsDisplayed().assertIsNotEnabled()
        capture("tailscale-uninstall")
        rule.runOnIdle { assertEquals(0, submitted) }
    }
}
