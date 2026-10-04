package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.graphics.asAndroidBitmap
import android.graphics.Bitmap
import androidx.test.platform.app.InstrumentationRegistry
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.ColorMode
import app.relaxkonos.mobile.ui.theme.RelaxKonOSTheme
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import java.io.File
import androidx.compose.material3.Surface
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density

class ApplicationCatalogTest {
    @get:Rule val rule = createComposeRule()

    @Test fun appearanceSupportsDarkHighContrastAndLargeText() {
        val mode = mutableStateOf(ColorMode.Light)
        val contrast = mutableStateOf(false)
        val fontScale = mutableFloatStateOf(1f)
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, fontScale.floatValue)) {
                RelaxKonOSTheme(mode.value, contrast.value) {
                    Surface(Modifier.requiredSize(360.dp, 640.dp).testTag("catalog-preview")) {
                        ApplicationCatalog(listOf(
                            CatalogApplication("docker", "Docker", "Containers and Compose", R.drawable.ic_app_docker) {},
                            CatalogApplication("git", "Git", "Repositories and builds", R.drawable.ic_sys_file_git_config) {},
                            CatalogApplication("terminal", "Terminal", "SSH sessions", DesktopIcons.navTerminal) {},
                        ), Modifier.fillMaxSize().padding(16.dp))
                    }
                }
            }
        }
        fun capture(name: String) {
            rule.onNodeWithText("Docker").assertIsDisplayed()
            val bitmap = rule.onNodeWithTag("catalog-preview").captureToImage().asAndroidBitmap()
            val context = InstrumentationRegistry.getInstrumentation().targetContext
            val directory = File(context.getExternalFilesDir(null), "ui-verification").apply { mkdirs() }
            File(directory, "$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        }
        capture("catalog-light")
        rule.runOnIdle { mode.value = ColorMode.Dark; contrast.value = true }
        capture("catalog-dark-high-contrast")
        rule.runOnIdle { fontScale.floatValue = 2f }
        capture("catalog-large-text")
        rule.onNodeWithText("Terminal").performScrollTo().assertIsDisplayed()
    }

    @Test fun searchFiltersLocallyAndOnlyClickOpensAnApplication() {
        var opened = 0
        rule.setContent {
            RelaxKonOSTheme(ColorMode.Light, false) {
                ApplicationCatalog(listOf(
                    CatalogApplication("docker", "Docker", "Containers", R.drawable.ic_app_docker) { opened++ },
                    CatalogApplication("git", "Git", "Repositories", R.drawable.ic_sys_file_git_config) { opened++ },
                ), Modifier.requiredSize(360.dp, 640.dp))
            }
        }
        rule.onNode(hasSetTextAction()).performTextInput("REPOS")
        rule.onNodeWithText("Git").assertIsDisplayed()
        rule.onNodeWithText("Docker").assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, opened) }
        rule.onNodeWithText("Git").performClick()
        rule.runOnIdle { assertEquals(1, opened) }
        rule.onNode(hasSetTextAction()).performTextReplacement("no-match")
        rule.onNodeWithText("Git").assertDoesNotExist()
        rule.onNode(hasSetTextAction()).performTextClearance()
        rule.onNodeWithText("Docker").assertIsDisplayed()
    }
}
