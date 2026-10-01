package app.relaxkonos.mobile.ui.manage.guardian

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class GuardianDraftTest {
    private val original = GuardianDefinition("job", "Job", "/bin/job", listOf("", " spaced ", "a\nb"), "/work", true,
        49, 17, GuardianHealthCheck("HTTP", "https://host/health", 37, 11, 9), "alice", "uid:1042")
    @Test fun `editing a name preserves all advanced fields and exact arguments`() {
        val draft = GuardianDraft(original, false)
        assertFalse(draft.dirty("linux")); draft.name = "Renamed"
        assertEquals(original.copy(name = "Renamed"), draft.definition("linux"))
        val pathWithSpace = original.copy(workingDirectory = "/work with trailing space ")
        val spaced = GuardianDraft(pathWithSpace, false); spaced.name = "Renamed"
        assertEquals(pathWithSpace.copy(name = "Renamed"), spaced.definition("linux"))
    }
    @Test fun `individual argument values remain empty multiline and ordered`() {
        val draft = GuardianDraft(original, false); draft.arguments.removeAt(1); draft.arguments.add("last\nvalue")
        assertEquals(listOf("", "a\nb", "last\nvalue"), draft.definition("linux").arguments)
        draft.arguments.add("bad\u0000arg"); assertFalse(draft.valid("linux"))
    }
    @Test fun `account comparison follows host platform and a changed identity is never reused`() {
        val draft = GuardianDraft(original, false); draft.runAs = "ALICE"
        assertEquals("uid:1042", draft.definition("windows").runAsIdentity)
        assertNull(draft.definition("linux").runAsIdentity)
        assertTrue(guardianSameAccount("windows", " Alice ", "alice"))
        assertFalse(guardianSameAccount("linux", "Alice", "alice"))
    }
    @Test fun `health targets and all numeric limits reject invalid input`() {
        val draft = GuardianDraft(original, false)
        draft.stopTimeout = "0"; assertFalse(draft.valid("linux")); draft.stopTimeout = "300"
        draft.attempts = "101"; assertFalse(draft.valid("linux")); draft.attempts = "100"
        draft.timeout = "61"; assertFalse(draft.valid("linux")); draft.timeout = "60"
        draft.interval = "3601"; assertFalse(draft.valid("linux")); draft.interval = "3600"
        draft.threshold = "0"; assertFalse(draft.valid("linux")); draft.threshold = "100"
        draft.healthTarget = "https://user:password@host/health"; assertFalse(draft.valid("linux"))
        draft.healthType = "tcp"; draft.healthTarget = "tcp://host:65535"; assertTrue(draft.valid("linux"))
        draft.healthTarget = "tcp://host"; assertFalse(draft.valid("linux"))
        draft.healthType = "process"; draft.healthTarget = ""; assertTrue(draft.valid("linux"))
        draft.healthType = ""; assertNull(draft.definition("linux").healthCheck)
    }
}
