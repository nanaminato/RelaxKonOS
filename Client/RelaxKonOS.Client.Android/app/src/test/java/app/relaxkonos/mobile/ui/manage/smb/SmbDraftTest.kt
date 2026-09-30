package app.relaxkonos.mobile.ui.manage.smb

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class SmbDraftTest {
    private val permissions = listOf(SmbPermission("S-1-5-7", SmbAccess.Read), SmbPermission("S-1-5-32-546", SmbAccess.Read), SmbPermission("S-1-5-21-1", SmbAccess.ReadWrite))
    private val share = SmbShare("共有", "共有", "D:\\Data", "note", false, false, true, permissions, true, false)
    @Test fun `Windows editor removes only generated guest permissions and preserves all switches`() {
        val draft = SmbDraft.from(share, true)
        assertEquals(permissions.takeLast(1), draft.permissions); assertFalse(draft.enabled)
        assertFalse(draft.readOnly); assertTrue(draft.guestAllowed); assertEquals("note", draft.description)
        assertEquals(permissions, SmbDraft.from(share.copy(guestAllowed = false), true).permissions)
        assertEquals(permissions, SmbDraft.from(share, false).permissions)
        assertTrue(SmbValidation.share(draft.request(), true))
    }
    @Test fun `platform validation refuses injection relative paths and invalid principals`() {
        val request = SmbShareRequest("共有", "/srv/data", "note", false, true, false, listOf(SmbPermission("alice", SmbAccess.ReadWrite)))
        assertTrue(SmbValidation.share(request, false))
        listOf(request.copy(name = "include cfg"), request.copy(path = "relative"), request.copy(description = "[global]"),
            request.copy(permissions = request.permissions + request.permissions), request.copy(name = "-bad"), request.copy(path = "/srv/data\ncommand")).forEach { assertFalse(SmbValidation.share(it, false)) }
        assertFalse(SmbValidation.share(request.copy(path = "D:\\Data"), true))
        assertFalse(SmbValidation.share(request.copy(guestAllowed = true, permissions = listOf(SmbPermission("nobody", SmbAccess.ReadWrite))), false))
        assertTrue(SmbValidation.share(request.copy(guestAllowed = true), false))
    }
    @Test fun `root exposure warning observes directory boundaries and traversal`() {
        assertFalse(SmbValidation.outsideSuggestedRoot("/srv/relaxkonos-shares/a", false))
        assertTrue(SmbValidation.outsideSuggestedRoot("/srv/relaxkonos-shares-other", false))
        assertTrue(SmbValidation.outsideSuggestedRoot("/srv/relaxkonos-shares/../secret", false))
        assertFalse(SmbValidation.outsideSuggestedRoot("D:/RelaxKonOSShares/a", true))
        assertTrue(SmbValidation.outsideSuggestedRoot("D:\\RelaxKonOSShares\\..\\secret", true))
    }
    @Test fun `password boundaries match current write only contract`() {
        assertTrue(SmbValidation.password("x".repeat(12).toCharArray())); assertTrue(SmbValidation.password("x".repeat(1024).toCharArray()))
        assertFalse(SmbValidation.password("x".repeat(11).toCharArray())); assertFalse(SmbValidation.password("x".repeat(1025).toCharArray()))
        assertFalse(SmbValidation.password("private\nsecret".toCharArray()))
    }
}
