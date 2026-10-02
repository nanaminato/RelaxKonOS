package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class InstallationPackagePickerTest {
    @get:Rule val rule = createComposeRule()
    private fun label(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun node(id: Int) = rule.onNodeWithText(label(id))

    @Test fun sourcesOfferServerBrowserAndPhonePickerWithoutSubmittingAnInstall() {
        var picks = 0
        var references = 0
        rule.setContent { MaterialTheme { Column {
            var source by remember { mutableStateOf(InstallationPackageSource.HostDownload) }
            var path by remember { mutableStateOf("") }
            InstallationPackagePicker(source, true, { source = it }, path, { path = it },
                { references++ }, { picks++ }, null)
        } } }
        node(R.string.installation_source_phone).performClick()
        node(R.string.tunnels_package_pick).assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(1, picks); assertEquals(0, references) }
        node(R.string.installation_source_server).performClick()
        node(R.string.remote_path_browse).assertIsDisplayed()
        node(R.string.tunnels_package_reference).assertIsNotEnabled()
        node(R.string.installation_source_host).performClick()
        node(R.string.remote_path_browse).assertDoesNotExist()
        node(R.string.tunnels_package_pick).assertDoesNotExist()
    }

    @Test fun lockedPackageSourcesCannotSwitchOrUpload() {
        var picks = 0
        var changes = 0
        rule.setContent { MaterialTheme { Column {
            InstallationPackagePicker(InstallationPackageSource.PhoneFile, false, { changes++ },
                "", {}, {}, { picks++ }, null)
        } } }
        node(R.string.installation_source_server).assertIsNotEnabled()
        node(R.string.tunnels_package_pick).assertIsNotEnabled()
        rule.runOnIdle { assertEquals(0, changes); assertEquals(0, picks) }
    }

    @Test fun manualDownloadShowsTheProvidedUrlAndResetsOnVersionChange() {
        var requests = 0
        val version = mutableStateOf("v1")
        val url = "https://github.com/MetaCubeX/mihomo/releases/download/v1/package.gz"
        rule.setContent { MaterialTheme { Column {
            ManualPackageDownload(version.value, true, false, if (version.value == "v1") url else null, { requests++ })
            Text(version.value)
        } } }
        rule.onNodeWithText(url).assertDoesNotExist()
        node(R.string.installation_download_myself).performClick()
        rule.onNodeWithText(url).assertIsDisplayed()
        node(R.string.installation_download_copy).assertIsEnabled()
        node(R.string.installation_download_copy).performClick()
        node(R.string.installation_download_copied).assertIsDisplayed()
        node(R.string.installation_download_open).assertIsEnabled()
        node(R.string.installation_download_then_select).assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, requests); version.value = "v2" }
        rule.onNodeWithText(url).assertDoesNotExist()
        node(R.string.installation_download_myself).performClick()
        node(R.string.tunnels_release_missing).assertIsDisplayed()
        rule.runOnIdle { assertEquals(2, requests) }
    }
}
