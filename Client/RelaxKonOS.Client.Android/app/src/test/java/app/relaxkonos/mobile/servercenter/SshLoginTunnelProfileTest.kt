package app.relaxkonos.mobile.servercenter

import app.relaxkonos.mobile.security.model.SavedLogin
import java.nio.file.Files
import org.junit.Assert.*
import org.junit.Test

class SshLoginTunnelProfileTest {
    @Test fun `empty forbidden components and invalid remote ports are rejected without normalization loss`() {
        listOf("http://@localhost:5000", "http://localhost:5000?", "http://localhost:5000#",
            "http://localhost:0", "https://localhost:65536").forEach {
            assertTrue(it, runCatching { SshLoginTunnelProfile.create("host.example", 22, "test", it) }.isFailure)
        }
        assertEquals("http://localhost:5000", SshLoginTunnelProfile.create("host.example", 22, "test", "http://localhost:5000/").remoteUrl)
    }
    @Test fun `valid scheme and localhost casing normalize to the same login identity`() {
        val first = SshLoginTunnelProfile.create("host.example", 22, "test", "HTTPS://LOCALHOST:5000/")
        val second = SshLoginTunnelProfile.create("host.example", 22, "test", "https://localhost:5000")
        assertEquals(second, first)
        assertEquals(second.serviceId, first.serviceId)
    }
    @Test fun identityMatchesDesktopAndSurvivesPortChanges() {
        val profile = SshLoginTunnelProfile.create("EXAMPLE.COM", 22, " alice ", "http://127.0.0.1:5000/")
        assertEquals("ssh-tunnel:e464a6ec4a747c982d459dc9d7ec83753b59f5db781936ff6de3cb8809b83d65", profile.serviceId)
        assertEquals(profile.resolve(51000).serviceId, profile.resolve(52000).serviceId)
        assertNotEquals(profile.resolve(51000).effectiveBaseUrl, profile.resolve(52000).effectiveBaseUrl)
        assertNotEquals(profile.serviceId, profile.copy(userName = "bob").serviceId)
        assertNotEquals(profile.serviceId, profile.copy(remoteUrl = "http://127.0.0.1:5001").serviceId)
        assertNull(SavedLogin(profile.serviceId, "server-user", 0).directServerUrl)
    }
    @Test fun rejectsNonLoopbackAndUnsupportedRemoteEndpoints() {
        listOf("http://example.com:5000", "ftp://127.0.0.1:5000", "http://alice@127.0.0.1:5000", "http://127.0.0.1:5000/?secret=x", "http://127.0.0.1:5000/app").forEach { address ->
            assertTrue(address, runCatching { SshLoginTunnelProfile.create("example.com", 22, "alice", address) }.isFailure)
        }
    }
    @Test fun profilesSurviveRestartAndDeduplicate() {
        val directory = Files.createTempDirectory("rk-login-tunnel").toFile()
        try {
            val profile = SshLoginTunnelProfile.create("example.com", 22, "alice", "http://127.0.0.1:5000")
            val store = LoginTunnelStore(directory)
            val shared = profile.copy(useServerCredentials = true)
            assertEquals(profile.serviceId, shared.serviceId)
            store.save(profile); store.save(shared); store.save(profile.copy(userName = "bob"))
            val loaded = LoginTunnelStore(directory).all()
            assertEquals(2, loaded.size)
            assertTrue(loaded.contains(shared))
        } finally { directory.deleteRecursively() }
    }
}
