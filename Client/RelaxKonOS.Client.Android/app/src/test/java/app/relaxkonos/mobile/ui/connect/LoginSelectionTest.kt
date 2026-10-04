package app.relaxkonos.mobile.ui.connect

import app.relaxkonos.mobile.core.auth.SelectedLogin
import app.relaxkonos.mobile.servercenter.SshLoginTunnelProfile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * What a picked identity decides about the form.
 *
 * The transport belongs to the identity that was picked, never to the previous one: a paired Windows
 * device key and a saved password are both direct connections, and an SSH profile is the only selection
 * that keeps the tunnel on. Inheriting the tunnel is what made the device-key row fail with "enter the
 * server address and login identity" — the form was still describing a tunnel profile, so the paired
 * server had stopped being the identity on screen and the key sign-in had no direct connection to use.
 */
class LoginSelectionTest {
    @Test
    fun `a paired device row is a direct selection that turns the tunnel off`() {
        val selection = LoginSelection.Direct("https://192.168.1.7:5000", "")
        assertFalse(selection.requiresTunnel)
        assertEquals("https://192.168.1.7:5000", selection.serverUrl)
        // The key replaces the password identity, so there is nothing to fill in and nothing to ask for.
        assertEquals("", selection.identifier)
    }

    @Test
    fun `a managed login names its host and leaves the address field alone`() {
        val selection = LoginSelection.Managed("DESKTOP-56H0GC1", "betha")
        assertFalse(selection.requiresTunnel)
        assertEquals("", selection.serverUrl)
        assertEquals("DESKTOP-56H0GC1", selection.hostDisplayName)
        assertEquals("betha", selection.identifier)
    }

    @Test
    fun `an ssh profile is the only selection that keeps the tunnel on`() {
        val profile = SshLoginTunnelProfile.create("192.168.1.2", 22, "nanami", "https://127.0.0.1:5000")
        val selection = LoginSelection.Tunnel(profile, "nanami")
        assertTrue(selection.requiresTunnel)
        assertEquals(profile.remoteUrl, selection.serverUrl)
        assertEquals("https://127.0.0.1:5000", selection.serverUrl)
    }

    @Test
    fun `the device-key form is deliberately not a complete password identity`() {
        val selection = LoginSelection.Direct("https://192.168.1.7:5000", "")
        // No identifier: the password decision table has nothing to work with here, which is exactly why
        // the row must route the click to the key sign-in itself instead of to `submit`.
        assertFalse(SelectedLogin.direct(selection.serverUrl, selection.identifier).isComplete)
    }
}
