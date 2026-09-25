package app.relaxkonos.mobile.security

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Guards the property the rest of the security package depends on: a JVM unit test loads these
 * classes, so a trace call must never touch `android.util.Log`. With no sink installed it has to be a
 * plain no-op — otherwise one diagnostic line would fail the whole package with "not mocked" instead
 * of reporting a real defect.
 */
class VaultDiagnosticsTest {
    @After
    fun tearDown() {
        VaultDiagnostics.sink = null
    }

    @Test
    fun `no sink discards every call`() {
        VaultDiagnostics.sink = null

        VaultDiagnostics.trace("key.create")
        VaultDiagnostics.trace("key.create", "Connection mode=PerUseStrongBiometric")
        VaultDiagnostics.failure("keystore.failure", IllegalStateException("boom"))
    }

    @Test
    fun `an installed sink receives the event, the detail and the raw exception type`() {
        val events = mutableListOf<Pair<String, String?>>()
        VaultDiagnostics.sink = { event, detail -> events += event to detail }

        VaultDiagnostics.trace("key.create", "Connection mode=PerUseStrongBiometric")
        VaultDiagnostics.failure("keystore.failure", IllegalStateException("boom"))

        assertEquals(
            listOf(
                "key.create" to "Connection mode=PerUseStrongBiometric",
                "keystore.failure" to "java.lang.IllegalStateException: boom",
            ),
            events,
        )
    }

    @Test
    fun `the detail is optional`() {
        val events = mutableListOf<Pair<String, String?>>()
        VaultDiagnostics.sink = { event, detail -> events += event to detail }

        VaultDiagnostics.trace("prompt.show")

        assertEquals(listOf<Pair<String, String?>>("prompt.show" to null), events)
    }
}
