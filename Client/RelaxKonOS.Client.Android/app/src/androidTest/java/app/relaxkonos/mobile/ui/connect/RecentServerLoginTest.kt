package app.relaxkonos.mobile.ui.connect

import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.security.model.SavedLogin
import org.junit.After
import org.junit.Assert.*
import org.junit.Test

/** Restoring a target must not spend credentials or silently choose a different transport. */
class RecentServerLoginTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val app get() = instrumentation.targetContext.applicationContext as RelaxKonApplication
    private val store = ViewModelStore()
    private val inserted = mutableListOf<SavedLogin>()

    private fun save(service: String, time: Long): SavedLogin = SavedLogin(service, "desktop-review", time).also {
        app.container.profiles.upsert(it)
        inserted += it
    }

    private fun model(check: (LoginViewModel) -> Unit) = instrumentation.runOnMainSync {
        val model = LoginViewModel(app)
        store.put("recent-login", model)
        check(model)
    }

    @After fun cleanup() {
        instrumentation.runOnMainSync { store.clear() }
        inserted.forEach { app.container.profiles.remove(it.serviceId, it.identifier) }
    }

    @Test fun newestSuccessfulServerIsSelectedWithoutConnecting() {
        save("https://older-desktop.invalid", Long.MAX_VALUE - 2)
        save("https://recent-desktop.invalid", Long.MAX_VALUE - 1)
        model {
            assertEquals("https://recent-desktop.invalid", it.serverUrl)
            assertEquals("desktop-review", it.identifier)
            assertFalse(it.connectionDetailsOpen)
            assertFalse(it.isLoggingIn)
            assertEquals("", it.passwordText)
            assertEquals(EndpointDiscoveryState.Idle, it.endpointDiscoveryState)
        }
    }

    @Test fun recentManagedServerIsNotReplacedByAnOlderDirectServer() {
        save("https://older-desktop.invalid", Long.MAX_VALUE - 2)
        save("rki-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Long.MAX_VALUE - 1)
        model {
            assertTrue(it.managedLoginSelected)
            assertEquals("", it.serverUrl)
            assertNotNull(it.message)
            assertFalse(it.isLoggingIn)
            assertEquals("", it.passwordText)
        }
    }

    @Test fun missingRecentTunnelRequiresRepairInsteadOfFallingBack() {
        save("https://older-desktop.invalid", Long.MAX_VALUE - 2)
        save("ssh-tunnel:missing-desktop-profile", Long.MAX_VALUE - 1)
        model {
            assertEquals("", it.serverUrl)
            assertTrue(it.connectionDetailsOpen)
            assertNotNull(it.message)
            assertFalse(it.isLoggingIn)
        }
    }
}
