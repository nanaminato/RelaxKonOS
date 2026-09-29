package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.core.net.IsoInstant
import java.text.DateFormat
import java.util.Date
import org.junit.Assert.assertEquals
import org.junit.Test

class TerminalSessionLabelTest {
    private val createdAt = "2026-09-29T15:08:11.9806172+00:00"
    private val sessionId = "b65db7839f0d4e2a"

    @Test fun `a session started today is labelled with its local time and id`() {
        val createdMillis = IsoInstant.toEpochMillis(createdAt)!!
        val time = DateFormat.getTimeInstance(DateFormat.MEDIUM).format(Date(createdMillis))

        assertEquals(
            "$time · b65db783",
            terminalSessionLabel(createdAt, sessionId, createdMillis + 1_000),
        )
    }

    @Test fun `a session from an earlier day keeps its date so the time cannot read as today`() {
        val createdMillis = IsoInstant.toEpochMillis(createdAt)!!
        val stamp = DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(createdMillis))

        assertEquals(
            "$stamp · b65db783",
            terminalSessionLabel(createdAt, sessionId, createdMillis + 2L * 86_400_000),
        )
    }

    @Test fun `an unusable timestamp falls back to the id instead of a guessed time`() {
        assertEquals("b65db783", terminalSessionLabel("", sessionId, System.currentTimeMillis()))
        assertEquals("b65db783", terminalSessionLabel("2026-09-29", sessionId, System.currentTimeMillis()))
    }
}
