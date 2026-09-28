package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Reading the host operating system a server named.
 *
 * The server serialises the enum as its camelCase name (`RelaxKonOSJsonOptions.Default`), so the two
 * things worth pinning are that the name is read whatever its case, and that a name this build has no
 * mark for degrades to [HostOperatingSystemKind.Unknown] instead of throwing — a newer server must
 * still be able to answer a list that an older phone draws.
 */
class HostOperatingSystemTest {
    @Test
    fun `the names the server sends are read`() {
        assertEquals(HostOperatingSystemKind.Ubuntu, HostOperatingSystemKind.fromWire("ubuntu"))
        assertEquals(HostOperatingSystemKind.Windows10, HostOperatingSystemKind.fromWire("windows10"))
        assertEquals(HostOperatingSystemKind.Windows11, HostOperatingSystemKind.fromWire("windows11"))
        assertEquals(HostOperatingSystemKind.Unknown, HostOperatingSystemKind.fromWire("unknown"))
    }

    @Test
    fun `the camel case member name is read too`() {
        assertEquals(HostOperatingSystemKind.WindowsServer, HostOperatingSystemKind.fromWire("windowsServer"))
        assertEquals(HostOperatingSystemKind.WindowsServer, HostOperatingSystemKind.fromWire("WindowsServer"))
    }

    @Test
    fun `a name this build does not know is unknown, never an error`() {
        assertEquals(HostOperatingSystemKind.Unknown, HostOperatingSystemKind.fromWire("windows12"))
        assertEquals(HostOperatingSystemKind.Unknown, HostOperatingSystemKind.fromWire(""))
        assertEquals(HostOperatingSystemKind.Unknown, HostOperatingSystemKind.fromWire(null))
    }
}
