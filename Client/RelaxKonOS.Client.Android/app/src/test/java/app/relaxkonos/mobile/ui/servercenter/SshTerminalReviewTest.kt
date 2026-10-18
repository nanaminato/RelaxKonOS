package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.servercenter.SshTerminalUiState
import org.junit.Assert.*
import org.junit.Test

class SshTerminalReviewTest {
    @Test fun carriageReturnAndModifiersCannotBypassMultilineReview() {
        for (input in listOf("one\rtwo", "one\ntwo", "one\r\ntwo")) {
            assertTrue(sshTerminalNeedsReview(input))
            for (ctrl in listOf(false, true)) for (alt in listOf(false, true)) {
                val expected = (if (alt) "\u001b" else "") + input + (if (ctrl || alt) "" else "\r")
                assertEquals(expected, sshTerminalLinePayload(input, ctrl, alt))
            }
        }
        assertFalse(sshTerminalNeedsReview("echo ok"))
        assertEquals("\u0003", sshTerminalLinePayload("c", ctrl = true, alt = false))
        assertEquals("echo ok\r", sshTerminalLinePayload("echo ok", ctrl = false, alt = false))
    }
    @Test fun reviewBindsExactSessionRevisionAndDraftWithoutInvalidatingOnOutput() {
        val current = SshTerminalUiState(sessionId = "first", hostId = "host", connected = true, draft = "one\ntwo", inputRevision = 4)
        val owner = SshTerminalReviewOwner(current)
        assertTrue(owner.matches(current.copy(output = "new output")))
        assertFalse(owner.matches(current.copy(sessionId = "second")))
        assertFalse(owner.matches(current.copy(hostId = "other")))
        assertFalse(owner.matches(current.copy(inputRevision = 5)))
        assertFalse(owner.matches(current.copy(draft = "other")))
        assertFalse(owner.matches(null))
    }
}
