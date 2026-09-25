package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.recordId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The identity key of `RelaxKonOS.Mobile.LoginCredentials.Design.md` §2.1: `(server address, account)`,
 * normalised the same way the session and the vault normalise it, and nothing more.
 */
class SelectedLoginTest {
    @Test
    fun `identity ignores surrounding whitespace and a trailing slash`() {
        val sloppy = SelectedLogin("  https://host:5090/  ", "  nana ")
        val tidy = SelectedLogin("https://host:5090", "nana")

        assertEquals(tidy.id, sloppy.id)
        assertEquals(tidy.credentialKey, sloppy.credentialKey)
        assertEquals("https://host:5090", sloppy.normalizedServerUrl)
        assertEquals("nana", sloppy.normalizedIdentifier)
    }

    @Test
    fun `case is never folded`() {
        // A server address may carry a case-sensitive path, and two identifiers that differ only in case
        // may be two accounts the server keeps apart. Folding either one would merge two records into
        // one, and a save would then overwrite a credential that belongs to another login.
        assertNotEquals(
            SelectedLogin("https://Host:5090", "nana").id,
            SelectedLogin("https://host:5090", "nana").id,
        )
        assertNotEquals(
            SelectedLogin("https://host:5090", "Nana").id,
            SelectedLogin("https://host:5090", "nana").id,
        )
    }

    @Test
    fun `the same server with another account is a different identity`() {
        val root = SelectedLogin("https://host:5090", "root")
        val nanami = SelectedLogin("https://host:5090", "nanami")

        assertNotEquals(root.id, nanami.id)
        assertNotEquals(root.credentialKey, nanami.credentialKey)
    }

    @Test
    fun `another server with the same account is a different identity`() {
        val alpha = SelectedLogin("https://alpha:5090", "nanami")
        val beta = SelectedLogin("https://beta:5090", "nanami")

        assertNotEquals(alpha.id, beta.id)
        assertNotEquals(alpha.credentialKey, beta.credentialKey)
    }

    @Test
    fun `the credential key is the connection vault record key`() {
        val login = SelectedLogin("https://host:5090", "nana")

        assertEquals(recordId(VaultKind.Connection, "https://host:5090", "nana"), login.credentialKey)
    }

    @Test
    fun `completeness needs both fields to carry something`() {
        assertTrue(SelectedLogin("https://host:5090", "nana").isComplete)
        assertFalse(SelectedLogin("", "nana").isComplete)
        assertFalse(SelectedLogin("https://host:5090", "   ").isComplete)
    }
}
