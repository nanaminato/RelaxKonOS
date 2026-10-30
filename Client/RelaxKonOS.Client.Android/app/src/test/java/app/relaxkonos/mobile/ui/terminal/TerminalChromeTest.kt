package app.relaxkonos.mobile.ui.terminal

import org.junit.Assert.assertEquals
import org.junit.Test

class TerminalChromeTest {
    @Test fun wideWindowsForceTheFullHeaderOnlyWhenThereIsRoom() {
        for (preferred in listOf(TerminalChrome.Full, TerminalChrome.Compact, TerminalChrome.Immersive)) {
            assertEquals("$preferred", TerminalChrome.Full,
                resolveTerminalChrome(preferred, wide = true, shortViewport = false, connected = true))
        }
        assertEquals(TerminalChrome.Compact,
            resolveTerminalChrome(TerminalChrome.Full, wide = true, shortViewport = true, connected = true))
        assertEquals(TerminalChrome.Compact,
            resolveTerminalChrome(TerminalChrome.Compact, wide = true, shortViewport = true, connected = true))
        assertEquals(TerminalChrome.Immersive,
            resolveTerminalChrome(TerminalChrome.Immersive, wide = true, shortViewport = true, connected = true))
    }

    @Test fun shortViewportFoldsOnlyTheExpandedHeader() {
        assertEquals(TerminalChrome.Compact,
            resolveTerminalChrome(TerminalChrome.Full, wide = false, shortViewport = true, connected = true))
        assertEquals(TerminalChrome.Compact,
            resolveTerminalChrome(TerminalChrome.Compact, wide = false, shortViewport = true, connected = true))
        assertEquals(TerminalChrome.Immersive,
            resolveTerminalChrome(TerminalChrome.Immersive, wide = false, shortViewport = true, connected = true))
    }

    @Test fun aDisconnectedShellNeverKeepsTheImmersiveChrome() {
        assertEquals(TerminalChrome.Compact,
            resolveTerminalChrome(TerminalChrome.Immersive, wide = false, shortViewport = false, connected = false))
        assertEquals(TerminalChrome.Compact,
            resolveTerminalChrome(TerminalChrome.Immersive, wide = true, shortViewport = false, connected = false))
        assertEquals(TerminalChrome.Immersive,
            resolveTerminalChrome(TerminalChrome.Immersive, wide = false, shortViewport = false, connected = true))
    }

    @Test fun tallConnectedPhonesHonourTheStoredPreference() {
        assertEquals(TerminalChrome.Full,
            resolveTerminalChrome(TerminalChrome.Full, wide = false, shortViewport = false, connected = true))
        assertEquals(TerminalChrome.Compact,
            resolveTerminalChrome(TerminalChrome.Compact, wide = false, shortViewport = false, connected = true))
    }
}
