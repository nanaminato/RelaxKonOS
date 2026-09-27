package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Test

class SshTerminalTranscriptTest {
    @Test fun keepsShellOutputReadableAcrossChunks() {
        val transcript = SshTerminalTranscript()
        transcript.append("\u001b[31")
        assertEquals("user@host:~$ ", transcript.append("muser@host:~$ \u001b[0m"))
        assertEquals("user@host:~$ cd /tmp\n/tmp\n", transcript.append("cd /tmp\r\n/tmp\r\n"))
    }

    @Test fun boundsOutputAndHandlesRewrittenLines() {
        val transcript = SshTerminalTranscript(8)
        transcript.append("progress 1")
        assertEquals("done", transcript.append("\rdone"))
        assertEquals("45678901", transcript.append("2345678901"))
    }
}
