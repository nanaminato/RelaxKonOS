package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.core.net.IsoInstant
import java.text.DateFormat
import java.util.Date
import org.junit.Assert.assertEquals
import org.junit.Test

class TerminalSessionLabelTest {
    private val createdAt = "2026-09-29T15:08:11.9806172+00:00"
    private val sessionName = "Session 1"

    @Test fun `a session started today is labelled with its local time and name`() {
        val createdMillis = IsoInstant.toEpochMillis(createdAt)!!
        val time = DateFormat.getTimeInstance(DateFormat.MEDIUM).format(Date(createdMillis))

        assertEquals(
            "Session 1 · $time",
            terminalSessionLabel(createdAt, sessionName, createdMillis + 1_000),
        )
    }

    @Test fun `a session from an earlier day keeps its date so the time cannot read as today`() {
        val createdMillis = IsoInstant.toEpochMillis(createdAt)!!
        val stamp = DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(createdMillis))

        assertEquals(
            "Session 1 · $stamp",
            terminalSessionLabel(createdAt, sessionName, createdMillis + 2L * 86_400_000),
        )
    }

    @Test fun `an unusable timestamp falls back to the name instead of a guessed time`() {
        assertEquals("Session 1", terminalSessionLabel("", sessionName, System.currentTimeMillis()))
        assertEquals("Session 1", terminalSessionLabel("2026-09-29", sessionName, System.currentTimeMillis()))
    }
}
