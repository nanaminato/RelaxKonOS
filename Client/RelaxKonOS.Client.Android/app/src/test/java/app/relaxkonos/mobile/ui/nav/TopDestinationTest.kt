package app.relaxkonos.mobile.ui.nav

import app.relaxkonos.mobile.core.net.ServerCapabilities
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Capability gating of the top-level navigation.
 *
 * `Shell.Design.md` §8 forbids an entry without a usable workflow, so an unimplemented
 * destination and a destination the server cannot serve are both absent rather than disabled.
 */
class TopDestinationTest {
    @Test
    fun `home, manage and more need no capability`() {
        val visible = TopDestination.visible(emptySet())

        assertTrue(visible.contains(TopDestination.Home))
        assertTrue(visible.contains(TopDestination.Manage))
        assertTrue(visible.contains(TopDestination.More))
    }

    @Test
    fun `files appears only when the server offers the capability`() {
        assertFalse(TopDestination.visible(emptySet()).contains(TopDestination.Files))
        assertFalse(TopDestination.visible(setOf("server.metrics")).contains(TopDestination.Files))
        assertTrue(TopDestination.visible(setOf(ServerCapabilities.FILES)).contains(TopDestination.Files))
    }

    @Test
    fun `terminal is an application rather than a top level destination`() {
        val all = setOf(
            ServerCapabilities.FILES,
            ServerCapabilities.METRICS,
            ServerCapabilities.PROCESSES,
            ServerCapabilities.TERMINAL,
            ServerCapabilities.DOCKER,
            ServerCapabilities.GUARDIAN,
            ServerCapabilities.APPLICATION_DEPLOYMENTS,
            ServerCapabilities.WEB_SERVER,
            ServerCapabilities.POSIX_PERMISSIONS,
        )

        assertEquals(listOf(Routes.HOME, Routes.FILES, Routes.MANAGE, Routes.MORE), TopDestination.visible(all).map { it.route })
    }

    @Test
    fun `the advertised order is stable`() {
        val visible = TopDestination.visible(setOf(ServerCapabilities.FILES))

        assertEquals(
            listOf(TopDestination.Home, TopDestination.Files, TopDestination.Manage, TopDestination.More),
            visible,
        )
    }
}
