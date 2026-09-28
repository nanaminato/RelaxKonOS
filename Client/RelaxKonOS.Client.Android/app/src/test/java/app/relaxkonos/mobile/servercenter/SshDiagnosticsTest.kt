package app.relaxkonos.mobile.servercenter

import com.jcraft.jsch.JSchException
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SshDiagnosticsTest {

    @After
    fun tearDown() {
        SshDiagnostics.sink = null
    }

    @Test
    fun `classifies authentication failure without logging the SSH library message`() {
        val events = mutableListOf<Pair<String, String?>>()
        SshDiagnostics.sink = { event, detail -> events += event to detail }

        SshDiagnostics.failure("connect.failed", JSchException("Auth fail for methods 'password' at nana@192.168.1.4"))

        val detail = events.single().second.orEmpty()
        assertEquals("connect.failed", events.single().first)
        assertTrue(detail.startsWith("classification=authentication_failed types=JSchException frames="))
        assertFalse(detail.contains("nana"))
        assertFalse(detail.contains("192.168.1.4"))
    }

    @Test
    fun `records negotiation failure as a separate category`() {
        val events = mutableListOf<Pair<String, String?>>()
        SshDiagnostics.sink = { event, detail -> events += event to detail }

        SshDiagnostics.failure("connect.failed", JSchException("Algorithm negotiation fail"))

        assertEquals("connect.failed", events.single().first)
        assertTrue(events.single().second.orEmpty().startsWith(
            "classification=algorithm_negotiation_failed types=JSchException frames=",
        ))
    }
}
