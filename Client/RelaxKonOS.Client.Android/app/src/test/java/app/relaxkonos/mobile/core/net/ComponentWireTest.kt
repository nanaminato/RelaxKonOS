package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ComponentWireTest {
    @Test
    fun `parses a managed component state`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"nginx","state":{"componentId":"nginx","hostId":"host-1","revision":7,
            "ownership":"managed","mode":"systemPackage",
            "target":{"os":"linux","architecture":"x64","distribution":"debian","libc":"glibc",
            "installMode":"SystemPackage","serviceHost":"systemd"},
            "activeVersion":"1.24.0","recoveryRequired":false},
            "modes":[{"mode":"systemPackage","platform":"linux-x64","available":true,"problemCode":null}],
            "problems":[],"pendingOperationId":null}
            """.trimIndent(),
            "nginx",
        )
        assertEquals("nginx", view.componentId)
        assertEquals("managed", view.ownership)
        assertEquals("1.24.0", view.version)
        assertTrue(view.installed)
        assertFalse(view.recoveryRequired)
        assertFalse(view.unsupported)
        assertEquals("linux / x64 / systemd", view.state?.target)
        assertEquals(7L, view.state?.revision)
    }

    @Test
    fun `parses an unsupported component with its declared modes`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"samba","state":null,
            "modes":[{"mode":"systemPackage","platform":"windows-x64","available":false,
            "problemCode":"component.platform_unsupported"}],
            "problems":["component.platform_unsupported"],"pendingOperationId":null}
            """.trimIndent(),
            "samba",
        )
        assertNull(view.state)
        assertTrue(view.unsupported)
        assertFalse(view.installed)
        assertEquals(listOf("component.platform_unsupported"), view.problems)
    }

    @Test
    fun `recovery is visible from a pending operation without a state`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"docker","state":null,"modes":[],"problems":[],
            "pendingOperationId":"11111111-1111-1111-1111-111111111111"}
            """.trimIndent(),
            "docker",
        )
        assertTrue(view.recoveryRequired)
    }

    @Test
    fun `rejects a response for another component`() {
        var failed = false
        try {
            ComponentWire.stateView(
                """{"componentId":"nginx","state":null,"modes":[],"problems":[],"pendingOperationId":null}""",
                "docker",
            )
        } catch (_: IllegalArgumentException) {
            failed = true
        }
        assertTrue(failed)
    }

    @Test
    fun `rejects an unknown ownership value`() {
        var failed = false
        try {
            ComponentWire.stateView(
                """
                {"componentId":"nginx","state":{"componentId":"nginx","hostId":"host-1","revision":1,
                "ownership":"borrowed","mode":"systemPackage",
                "target":{"os":"linux","architecture":"x64","serviceHost":"systemd"},
                "activeVersion":null,"recoveryRequired":false},
                "modes":[],"problems":[],"pendingOperationId":null}
                """.trimIndent(),
                "nginx",
            )
        } catch (_: IllegalArgumentException) {
            failed = true
        }
        assertTrue(failed)
    }
}
