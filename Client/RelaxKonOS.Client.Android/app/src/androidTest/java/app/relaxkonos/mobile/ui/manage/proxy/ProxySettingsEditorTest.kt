package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import androidx.test.espresso.Espresso
import androidx.compose.ui.semantics.SemanticsActions
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class ProxySettingsEditorTest {
    @get:Rule val rule = createComposeRule()
    private fun label(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    private fun show(capabilities: ProxySystemProxyCapabilities, submit: (ProxySettings) -> Unit = {}, section: ProxySettingsSection = ProxySettingsSection.SystemProxy) {
        val settings = ProxySettings(false, false, true, false, true, "info", 7890, false, "127.0.0.1",
            ProxyTunSettings("mixed", "Mihomo", true, false, true, "any:53", 1500),
            ProxySystemOptions(false, false, 30, true, "localhost"))
        val overview = ProxyOverview(
            ProxyRuntime("managed", ProxyRuntimeState.Running, "1", null, true, ""),
            null, false, true, "healthy", "", true, true, true,
            "linux", false, capabilities)
        rule.setContent { MaterialTheme {
            ProxySettingsEditor(settings, overview, section, ProxyState(), 0L, { true }, submit, {})
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

    @Test fun changingConfirmedDraftRequiresNewConfirmationAndRejectsOldSave() {
        val submitted = mutableListOf<ProxySettings>()
        show(ProxySystemProxyCapabilities(true, false, true, true), submitted::add)
        val checkbox = rule.onNode(SemanticsMatcher.expectValue(SemanticsProperties.Role, Role.Checkbox))
        checkbox.performScrollTo().performClick()
        val save = rule.onNodeWithText(label(R.string.common_save))
        save.assertIsEnabled()
        val oldSave = save.fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(label(R.string.mihomo_bypass)).performScrollTo().performTextReplacement("review.invalid")
        Espresso.closeSoftKeyboard()
        save.assertIsNotEnabled()
        rule.runOnIdle { oldSave(); assertEquals(0, submitted.size) }
        checkbox.performScrollTo().performClick()
        val currentSave = save.fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.runOnIdle {
            oldSave(); assertEquals(0, submitted.size)
            currentSave(); currentSave()
            assertEquals(1, submitted.size)
            assertEquals("review.invalid", submitted.single().systemProxy?.bypassList)
        }
    }

    private fun numericChangeRevokesApproval(section: ProxySettingsSection, field: Int, replacement: String) {
        show(ProxySystemProxyCapabilities(true, false, true, true), section = section)
        val checkbox = rule.onNode(SemanticsMatcher.expectValue(SemanticsProperties.Role, Role.Checkbox))
        checkbox.performScrollTo().performClick()
        rule.onNodeWithText(label(R.string.common_save)).assertIsEnabled()
        rule.onNodeWithText(label(field)).performScrollTo().performTextReplacement(replacement)
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(label(R.string.common_save)).assertIsNotEnabled()
        checkbox.performScrollTo().performClick()
        rule.onNodeWithText(label(R.string.common_save)).assertIsEnabled()
    }

    @Test fun listenerPortChangeRevokesApproval() =
        numericChangeRevokesApproval(ProxySettingsSection.Mihomo, R.string.mihomo_mixed_port, "7891")

    @Test fun tunMtuChangeRevokesApproval() =
        numericChangeRevokesApproval(ProxySettingsSection.Tun, R.string.mihomo_mtu, "1400")

    @Test fun consecutiveFieldEventsBeforeRedrawKeepLatestInput() {
        show(ProxySystemProxyCapabilities(true, false, true, true), section = ProxySettingsSection.Mihomo)
        val edit = rule.onNodeWithText(label(R.string.mihomo_mixed_port)).performScrollTo()
            .fetchSemanticsNode().config[SemanticsActions.SetText].action!!
        rule.runOnIdle {
            edit(androidx.compose.ui.text.AnnotatedString("7891"))
            edit(androidx.compose.ui.text.AnnotatedString("7892"))
        }
        rule.onNodeWithText("7892").assertExists()
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
