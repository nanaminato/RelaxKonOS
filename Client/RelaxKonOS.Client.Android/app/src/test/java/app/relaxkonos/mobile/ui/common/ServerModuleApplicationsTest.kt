package app.relaxkonos.mobile.ui.common

import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.nav.*
import org.junit.Assert.*
import org.junit.Test

class ServerModuleApplicationsTest {
    private val module = ServerModuleFact("relaxkonos.terminal", "host", false, false, false, "revision", "process",
        "1.0", null, null, null, emptyList(), emptyList(), null, null, capabilities = listOf(ServerCapabilities.TERMINAL))
    @Test fun `disabled provider locates module while dormant enabled provider preserves business entry`() {
        assertEquals(module.id, missingModuleForRoute(Routes.TERMINAL, emptySet(), listOf(module)))
        assertNull(missingModuleForRoute(Routes.TERMINAL, setOf(ServerCapabilities.TERMINAL), listOf(module)))
        assertNull(missingModuleForRoute(Routes.TERMINAL, emptySet(), listOf(module.copy(effectiveEnabled = true, residencyState = "dormant"))))
        assertNull(missingModuleForRoute(Routes.TERMINAL, emptySet(), emptyList()))
    }
    @Test fun `module location is bound to a login and removed with its navigation entry`() {
        val navigator = MobileNavigator(Routes.HOME)
        val target = ServerModuleNavigationTarget("connection", "login", module.id)
        navigator.openServerModule(target)
        assertEquals(Routes.MANAGE_SERVER_MODULES, navigator.route)
        assertEquals(target, navigator.currentModuleTarget)
        assertTrue(navigator.pop())
        assertNull(navigator.currentModuleTarget)
        assertTrue(runCatching { navigator.openServerModule(target.copy(moduleId = "../install")) }.isFailure)
    }
}
