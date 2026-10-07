package app.relaxkonos.mobile.servercenter

import com.jcraft.jsch.JSchException
import com.jcraft.jsch.ReviewDisconnectException
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import org.junit.Assert.assertEquals
import org.junit.Test

class SshFailureRulesTest {
    @Test
    fun `structured authentication disconnect rejects credentials`() {
        for (reason in listOf(14, 15)) {
            assertEquals(SshFailureReason.AuthenticationRejected,
                SshFailureRules.classify(ReviewDisconnectException(reason, "private server detail")))
        }
    }

    @Test
    fun `OpenSSH authentication limit disconnect rejects credentials`() {
        assertEquals(SshFailureReason.AuthenticationRejected,
            SshFailureRules.classify(ReviewDisconnectException(2, "Too many authentication failures")))
    }

    @Test
    fun `ordinary protocol disconnect is not blamed on credentials`() {
        assertEquals(SshFailureReason.HandshakeFailed,
            SshFailureRules.classify(ReviewDisconnectException(2, "Protocol error")))
    }

    @Test
    fun `structured authentication cancellation remains distinct`() {
        assertEquals(SshFailureReason.AuthenticationCancelled,
            SshFailureRules.classify(ReviewDisconnectException(13, "cancelled")))
    }

    @Test
    fun `wrapped name resolution failure is reported as an unresolved name`() {
        val error = JSchException(
            "java.net.UnknownHostException: nas.local",
            UnknownHostException("nas.local"),
        )

        assertEquals(SshFailureReason.NameNotResolved, SshFailureRules.classify(error))
    }

    @Test
    fun `wrapped socket timeout is reported as timed out, not as a credential problem`() {
        val error = JSchException("Session.connect: timed out", SocketTimeoutException("connect timed out"))

        assertEquals(SshFailureReason.TimedOut, SshFailureRules.classify(error))
    }

    @Test
    fun `connection refused is reported as an unreachable port`() {
        val error = JSchException("Session.connect: refused", ConnectException("Connection refused"))

        assertEquals(SshFailureReason.ConnectionRefused, SshFailureRules.classify(error))
    }

    @Test
    fun `no route to host is reported as an unreachable port`() {
        assertEquals(
            SshFailureReason.ConnectionRefused,
            SshFailureRules.classify(NoRouteToHostException("No route to host")),
        )
    }

    @Test
    fun `network failure outranks the generic SSH library message`() {
        // JSch reports both of these as an indistinguishable JSchException; only the cause tells a
        // dead network apart from a refused password.
        val refused = JSchException("Session.connect: java.net.ConnectException", ConnectException("refused"))

        assertEquals(SshFailureReason.ConnectionRefused, SshFailureRules.classify(refused))
    }

    @Test
    fun `auth fail is reported as rejected credentials`() {
        val error = JSchException("Auth fail for methods 'password' at nana@host")

        assertEquals(SshFailureReason.AuthenticationRejected, SshFailureRules.classify(error))
    }

    @Test
    fun `auth cancel is reported as cancelled authentication`() {
        assertEquals(
            SshFailureReason.AuthenticationCancelled,
            SshFailureRules.classify(JSchException("Auth cancel")),
        )
    }

    @Test
    fun `algorithm negotiation failure keeps its own reason`() {
        assertEquals(
            SshFailureReason.AlgorithmNegotiation,
            SshFailureRules.classify(JSchException("Algorithm negotiation fail")),
        )
    }

    @Test
    fun `rejected host key keeps its own reason`() {
        assertEquals(
            SshFailureReason.HostKeyRejected,
            SshFailureRules.classify(JSchException("reject HostKey: ssh-ed25519")),
        )
    }

    @Test
    fun `unrecognised library message is reported as a handshake failure`() {
        assertEquals(
            SshFailureReason.HandshakeFailed,
            SshFailureRules.classify(JSchException("Transport: read failed")),
        )
    }

    @Test
    fun `failure outside the SSH library has no claimed cause`() {
        assertEquals(SshFailureReason.Unexpected, SshFailureRules.classify(IllegalStateException("closed")))
    }

    @Test
    fun `diagnostic names stay stable and distinct`() {
        val names = SshFailureReason.entries.map { it.diagnosticName }

        assertEquals(names.size, names.toSet().size)
        assertEquals("authentication_failed", SshFailureReason.AuthenticationRejected.diagnosticName)
        assertEquals("connect_timed_out", SshFailureReason.TimedOut.diagnosticName)
        assertEquals("unexpected_failure", SshFailureReason.Unexpected.diagnosticName)
    }
}
