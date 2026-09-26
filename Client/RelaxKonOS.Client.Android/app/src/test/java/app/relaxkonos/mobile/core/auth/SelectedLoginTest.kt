package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.recordId
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The identity key is `(serviceId, account)`; the effective URL never participates.
 */
class SelectedLoginTest {
    @Test
    fun `identity ignores surrounding whitespace and a trailing slash`() {
        val sloppy = SelectedLogin.direct("  https://HOST:5090/  ", "  nana ")
        val tidy = SelectedLogin.direct("https://host:5090", "nana")

        assertEquals(tidy.id, sloppy.id)
        assertEquals(tidy.credentialKey, sloppy.credentialKey)
        assertEquals("https://host:5090", sloppy.serviceId)
        assertEquals("https://host:5090", sloppy.effectiveBaseUrl)
        assertEquals("nana", sloppy.normalizedIdentifier)
    }

    @Test
    fun `account and path case are never folded`() {
        assertNotEquals(
            SelectedLogin.direct("https://host:5090", "Nana").id,
            SelectedLogin.direct("https://host:5090", "nana").id,
        )
        assertNotEquals(
            SelectedLogin.direct("https://host:5090/Workspace", "nana").id,
            SelectedLogin.direct("https://host:5090/workspace", "nana").id,
        )
    }

    @Test
    fun `the same server with another account is a different identity`() {
        val root = SelectedLogin.direct("https://host:5090", "root")
        val nanami = SelectedLogin.direct("https://host:5090", "nanami")

        assertNotEquals(root.id, nanami.id)
        assertNotEquals(root.credentialKey, nanami.credentialKey)
    }

    @Test
    fun `another server with the same account is a different identity`() {
        val alpha = SelectedLogin.direct("https://alpha:5090", "nanami")
        val beta = SelectedLogin.direct("https://beta:5090", "nanami")

        assertNotEquals(alpha.id, beta.id)
        assertNotEquals(alpha.credentialKey, beta.credentialKey)
    }

    @Test
    fun `the credential key is the connection vault record key`() {
        val login = SelectedLogin.direct("https://host:5090", "nana")

        assertEquals(recordId(VaultKind.Connection, "https://host:5090", "nana"), login.credentialKey)
    }

    @Test
    fun `completeness needs both fields to carry something`() {
        assertTrue(SelectedLogin.direct("https://host:5090", "nana").isComplete)
        assertFalse(SelectedLogin.direct("", "nana").isComplete)
        assertFalse(SelectedLogin.direct("https://host:5090", "   ").isComplete)
    }

    @Test
    fun `managed tunnel port is excluded from login and credential identity`() {
        val installationId = "rki-0123456789abcdef0123456789abcdef"
        val first = SelectedLogin.resolved(
            ServerConnectionIdentityRules.managedTunnel(installationId, "http://127.0.0.1:51000"),
            "nana",
        )
        val rebound = SelectedLogin.resolved(
            ServerConnectionIdentityRules.managedTunnel(installationId, "http://127.0.0.1:52345"),
            "nana",
        )

        assertEquals(first.id, rebound.id)
        assertEquals(first.credentialKey, rebound.credentialKey)
        assertNotEquals(first.effectiveBaseUrl, rebound.effectiveBaseUrl)
    }
}
