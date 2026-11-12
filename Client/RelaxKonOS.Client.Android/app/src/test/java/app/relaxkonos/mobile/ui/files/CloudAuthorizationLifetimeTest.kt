package app.relaxkonos.mobile.ui.files

import org.junit.Assert.*
import org.junit.Test

class CloudAuthorizationLifetimeTest {
    @Test fun `old resolution cannot authorize a replacement session`() {
        val lifetime = CloudAuthorizationLifetime("login-one")
        lifetime.bind("session-one")
        assertTrue(lifetime.matches("login-one", "session-one"))
        lifetime.bind("session-two")
        assertFalse(lifetime.matches("login-one", "session-one"))
        assertTrue(lifetime.matches("login-one", "session-two"))
    }
    @Test fun `switching login rejects pending start response and provider callback`() {
        val lifetime = CloudAuthorizationLifetime("login-one")
        lifetime.bind("session-one")
        assertFalse(lifetime.live("login-two"))
        assertFalse(lifetime.matches("login-two", "session-one"))
    }
    @Test fun `leaving dialog permanently invalidates callback even for same login`() {
        val lifetime = CloudAuthorizationLifetime("login-one")
        lifetime.bind("session-one")
        lifetime.close()
        assertFalse(lifetime.live("login-one"))
        assertFalse(lifetime.matches("login-one", "session-one"))
        assertTrue(runCatching { lifetime.bind("session-two") }.isFailure)
    }
}
