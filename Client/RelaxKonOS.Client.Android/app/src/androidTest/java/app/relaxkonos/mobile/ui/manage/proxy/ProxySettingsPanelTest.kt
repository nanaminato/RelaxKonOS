package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.theme.Spacing
import androidx.compose.foundation.layout.Arrangement
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class ProxySettingsPanelTest {
    @get:Rule val rule = createComposeRule()
    private fun label(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private val settings = ProxySettings(false, false, true, false, true, "info", 7890, false, "127.0.0.1",
        ProxyTunSettings("mixed", "Mihomo", true, false, true, "any:53", 1500),
        ProxySystemOptions(false, false, 30, true, "localhost"))
    private val overview = ProxyOverview(
        ProxyRuntime("managed", ProxyRuntimeState.Running, "1.19.30", null, true, ""),
        ProxyProfile("profile", "Profile", "mihomo", true, 1), true, true, "healthy", "", true, true, true,
        "Windows", false, ProxySystemProxyCapabilities(true, true, false, true), supportsDns = true)
    private val state = ProxyState(overview = ApiResult.Success(overview), settings = ApiResult.Success(settings),
        recovery = ApiResult.Success(ProxyRecovery(false, false, null, "")), geoData = ApiResult.Success(ProxyGeoData(true, 8597075)),
        dns = ApiResult.Success(ProxyDns(true, false, "fake-ip", "")))

    private fun show(facts: ProxyState = state, canManage: Boolean = true, ready: Boolean = true,
        edit: (ProxySettingsSection) -> Unit = {}, toggle: (Boolean) -> Unit = {}, emergency: () -> Unit = {}, systemProxy: (Boolean) -> Unit = {}) {
        rule.setContent { MaterialTheme {
            Column(Modifier.width(360.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                ProxySettingsPanel(facts, canManage, ready, edit, {}, toggle, systemProxy, emergency, {})
            }
        } }
    }

    @Test fun linuxCapabilitiesEnableSystemProxyControls() {
        var selected: ProxySettingsSection? = null
        var requested: Boolean? = null
        show(state.copy(overview = ApiResult.Success(overview.copy(operatingSystem = "linux",
            systemProxy = ProxySystemProxyCapabilities(true, false, true, true)))),
            edit = { selected = it }, systemProxy = { requested = it })
        rule.onNodeWithContentDescription(label(R.string.mihomo_system_proxy_settings)).performScrollTo().assertIsEnabled().performClick()
        assertEquals(ProxySettingsSection.SystemProxy, selected)
        rule.onNodeWithContentDescription(label(R.string.mihomo_system_proxy_title)).assertIsEnabled().performClick()
        assertEquals(true, requested)
        rule.onNodeWithText(label(R.string.mihomo_system_proxy_unsupported)).assertDoesNotExist()
    }

    @Test fun unavailableWriterDisablesControlsEvenOnWindows() {
        show(state.copy(overview = ApiResult.Success(overview.copy(systemProxy = ProxySystemProxyCapabilities(false, false, false, false)))))
        rule.onNodeWithContentDescription(label(R.string.mihomo_system_proxy_settings)).assertIsNotEnabled()
        rule.onNodeWithContentDescription(label(R.string.mihomo_system_proxy_title)).assertIsNotEnabled()
        rule.onNodeWithText(label(R.string.mihomo_system_proxy_unsupported)).assertExists()
    }

    @Test fun unavailableWriterStillAllowsDisablingAnEnabledProxy() {
        var requested: Boolean? = null
        show(state.copy(overview = ApiResult.Success(overview.copy(systemProxy = ProxySystemProxyCapabilities(false, false, false, false))),
            settings = ApiResult.Success(settings.copy(systemProxyEnabled = true))), systemProxy = { requested = it })
        rule.onNodeWithContentDescription(label(R.string.mihomo_system_proxy_title)).performScrollTo().assertIsEnabled().performClick()
        assertEquals(false, requested)
    }

    @Test fun interruptedProxyWriteOffersRecoveryEvenWhenSavedSettingIsOff() {
        var requested: Boolean? = null
        show(state.copy(problemCode = "proxy.system_proxy_conflict"), systemProxy = { requested = it })
        rule.onNodeWithText(label(R.string.mihomo_system_proxy_restore)).performScrollTo().performClick()
        assertEquals(false, requested)
    }

    @Test fun separateSettingsEntriesAndCollapsedDiagnosticsOnPhone() {
        var selected: ProxySettingsSection? = null
        show(edit = { selected = it })
        for (section in ProxySettingsSection.entries) {
            rule.onNodeWithContentDescription(label(section.title)).performScrollTo().performClick()
            assertEquals(section, selected)
        }
        rule.onNodeWithText(label(R.string.mihomo_host_network_note)).assertDoesNotExist()
        rule.onNodeWithText(label(R.string.mihomo_dns_status)).assertDoesNotExist()
        rule.onNodeWithText(label(R.string.mihomo_diagnostics_show)).performScrollTo().performClick()
        rule.onNodeWithText(label(R.string.mihomo_dns_status)).assertExists()
        rule.onNodeWithText(label(R.string.mihomo_diagnostics_hide)).performScrollTo().performClick()
        rule.onNodeWithText(label(R.string.mihomo_dns_status)).assertDoesNotExist()
    }

    @Test fun observersCannotChangeHostNetworking() {
        show(canManage = false)
        rule.onNodeWithContentDescription(label(R.string.mihomo_tun_mode)).assertIsNotEnabled()
        rule.onNodeWithContentDescription(label(R.string.mihomo_system_proxy_title)).assertIsNotEnabled()
        rule.onNodeWithText(label(R.string.mihomo_tun_emergency)).assertDoesNotExist()
        rule.onNodeWithContentDescription(label(ProxySettingsSection.Mihomo.title)).assertDoesNotExist()
    }

    @Test fun unsafeManagementRouteBlocksTunWhileEmergencyRemainsAvailable() {
        var invoked = false
        show(state.copy(overview = ApiResult.Success(overview.copy(managementRouteSafe = false)), uncertain = true),
            ready = false, emergency = { invoked = true })
        rule.onNodeWithContentDescription(label(R.string.mihomo_tun_mode)).assertIsNotEnabled()
        rule.onNodeWithText(label(R.string.mihomo_tun_emergency)).performScrollTo().assertIsEnabled().performClick()
        assertEquals(true, invoked)
    }

    @Test fun failedTunWithRecoveryMarkerOffersDisableWithoutOfferingEnable() {
        var requested: Boolean? = null
        show(state.copy(overview = ApiResult.Success(overview.copy(tunState = "failed")),
            recovery = ApiResult.Success(ProxyRecovery(true, true, 1, ""))), toggle = { requested = it })
        rule.onNodeWithContentDescription(label(R.string.mihomo_tun_mode)).assertIsNotEnabled()
        rule.onNodeWithText(label(R.string.mihomo_recovery_required)).assertExists()
        rule.onNodeWithText(label(R.string.mihomo_tun_disable)).performScrollTo().performClick()
        assertEquals(false, requested)
    }
}
