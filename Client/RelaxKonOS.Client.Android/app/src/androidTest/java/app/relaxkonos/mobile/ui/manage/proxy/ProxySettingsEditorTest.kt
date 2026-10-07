package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import org.junit.Rule
import org.junit.Test

class ProxySettingsEditorTest {
    @get:Rule val rule = createComposeRule()
    private fun label(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    private fun show(capabilities: ProxySystemProxyCapabilities) {
        val settings = ProxySettings(false, false, true, false, true, "info", 7890, false, "127.0.0.1",
            ProxyTunSettings("mixed", "Mihomo", true, false, true, "any:53", 1500),
            ProxySystemOptions(false, false, 30, true, "localhost"))
        val overview = ProxyOverview(
            ProxyRuntime("managed", ProxyRuntimeState.Running, "1", null, true, ""),
            null, false, true, "healthy", "", true, true, false,
            "linux", false, capabilities)
        rule.setContent { MaterialTheme {
            ProxySettingsEditor(settings, overview, ProxySettingsSection.SystemProxy, ProxyState(), {}, {})
        } }
    }

    @Test fun linuxDesktopEditorShowsScopeAndHidesPac() {
        show(ProxySystemProxyCapabilities(true, false, true, true))
        rule.onNodeWithText(label(R.string.mihomo_system_proxy_linux_desktop_scope)).assertExists()
        rule.onNodeWithText(label(R.string.mihomo_system_proxy_linux_scope)).assertDoesNotExist()
        rule.onNodeWithText(label(R.string.mihomo_pac)).assertDoesNotExist()
        rule.onNodeWithText(label(R.string.mihomo_bypass)).performScrollTo().assertExists()
        rule.onNodeWithText(label(R.string.common_save)).assertIsNotEnabled()
        rule.onNode(SemanticsMatcher.expectValue(SemanticsProperties.Role, Role.Checkbox))
            .performScrollTo().performClick()
        rule.onNodeWithText(label(R.string.common_save)).assertIsEnabled()
    }

    @Test fun linuxWithoutDesktopShowsLoginEnvironmentScope() {
        show(ProxySystemProxyCapabilities(true, false, true, false))
        rule.onNodeWithText(label(R.string.mihomo_system_proxy_linux_scope)).assertExists()
        rule.onNodeWithText(label(R.string.mihomo_system_proxy_linux_desktop_scope)).assertDoesNotExist()
        rule.onNodeWithText(label(R.string.mihomo_pac)).assertDoesNotExist()
    }

    @Test fun pacVisibilityFollowsCapabilityRatherThanOsName() {
        show(ProxySystemProxyCapabilities(true, true, true, true))
        rule.onNodeWithText(label(R.string.mihomo_pac)).performScrollTo().assertExists()
    }
}
