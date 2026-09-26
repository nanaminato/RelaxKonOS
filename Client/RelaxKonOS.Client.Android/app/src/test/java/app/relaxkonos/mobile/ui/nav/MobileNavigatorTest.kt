package app.relaxkonos.mobile.ui.nav

import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.snapshots.Snapshot
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The three navigation rules of `RelaxKonOS.Mobile.V1.Design.md` §4.1, asserted directly because they
 * are product behaviour rather than an implementation detail of a navigation library.
 */
class MobileNavigatorTest {
    @OptIn(ExperimentalCoroutinesApi::class)
    @Test
    fun `the first sub-route push invalidates a route observer`() = runTest {
        val navigator = MobileNavigator(Routes.MORE)
        val observed = mutableListOf<String>()
        val collector = launch {
            snapshotFlow { navigator.route }.take(2).toList(observed)
        }
        runCurrent()

        navigator.push(Routes.MORE_ACCOUNT_SECURITY)
        Snapshot.sendApplyNotifications()
        runCurrent()

        assertEquals(listOf(Routes.MORE, Routes.MORE_ACCOUNT_SECURITY), observed)
        collector.cancel()
    }

    @Test
    fun `the initial route is the destination itself`() {
        val navigator = MobileNavigator(Routes.HOME)

        assertEquals(Routes.HOME, navigator.route)
        assertEquals(Routes.HOME, navigator.currentDestination)
        assertTrue(navigator.isAtDestinationRoot)
    }

    @Test
    fun `each destination keeps its own stack`() {
        val navigator = MobileNavigator(Routes.HOME)

        navigator.select(Routes.FILES)
        navigator.push(Routes.FILES_DETAIL)
        assertEquals(Routes.FILES_DETAIL, navigator.route)
        assertFalse(navigator.isAtDestinationRoot)

        navigator.select(Routes.MORE)
        assertEquals(Routes.MORE, navigator.route)
        navigator.push(Routes.MORE_ABOUT)
        assertEquals(Routes.MORE_ABOUT, navigator.route)

        // Returning to files restores where the user was, not the list.
        navigator.select(Routes.FILES)
        assertEquals(Routes.FILES_DETAIL, navigator.route)
    }

    @Test
    fun `switching destination does not disturb the other stack`() {
        val navigator = MobileNavigator(Routes.HOME)
        navigator.select(Routes.MANAGE)
        navigator.push(Routes.MANAGE_MONITOR)
        navigator.select(Routes.FILES)
        navigator.push(Routes.FILES_DETAIL)

        navigator.select(Routes.MANAGE)
        assertEquals(Routes.MANAGE_MONITOR, navigator.route)
    }

    @Test
    fun `pop returns false when the destination is already at its root`() {
        val navigator = MobileNavigator(Routes.HOME)

        assertFalse(navigator.pop())
        assertTrue(navigator.isStackEmpty())
    }

    @Test
    fun `pop unwinds one sub-route at a time`() {
        val navigator = MobileNavigator(Routes.HOME)
        navigator.select(Routes.MORE)
        navigator.push(Routes.MORE_APPEARANCE)

        assertTrue(navigator.pop())
        assertEquals(Routes.MORE, navigator.route)
        assertFalse(navigator.pop())
    }

    @Test
    fun `popToDestinationRoot clears only the active stack`() {
        val navigator = MobileNavigator(Routes.HOME)
        navigator.select(Routes.FILES)
        navigator.push(Routes.FILES_DETAIL)
        navigator.select(Routes.MORE)
        navigator.push(Routes.MORE_ABOUT)

        assertTrue(navigator.popToDestinationRoot())
        assertEquals(Routes.MORE, navigator.route)

        navigator.select(Routes.FILES)
        assertEquals(Routes.FILES_DETAIL, navigator.route)
    }

    @Test
    fun `pushing home switches destination instead of nesting`() {
        val navigator = MobileNavigator(Routes.FILES)
        navigator.push(Routes.FILES_DETAIL)

        navigator.push(Routes.HOME)

        assertEquals(Routes.HOME, navigator.currentDestination)
        assertEquals(Routes.HOME, navigator.route)
        // The files stack survives the detour, which is what makes the home shortcut safe.
        navigator.select(Routes.FILES)
        assertEquals(Routes.FILES_DETAIL, navigator.route)
    }

    @Test
    fun `resetTo clears every stack`() {
        val navigator = MobileNavigator(Routes.HOME)
        navigator.select(Routes.FILES)
        navigator.push(Routes.FILES_DETAIL)
        navigator.select(Routes.MORE)
        navigator.push(Routes.MORE_ABOUT)

        navigator.resetTo(Routes.HOME)

        assertEquals(Routes.HOME, navigator.route)
        navigator.select(Routes.FILES)
        assertEquals(Routes.FILES, navigator.route)
        navigator.select(Routes.MORE)
        assertEquals(Routes.MORE, navigator.route)
    }

    @Test
    fun `isStackEmpty can be asked about another destination`() {
        val navigator = MobileNavigator(Routes.HOME)
        navigator.select(Routes.FILES)
        navigator.push(Routes.FILES_DETAIL)

        assertTrue(navigator.isStackEmpty(Routes.HOME))
        assertTrue(navigator.isStackEmpty(Routes.MANAGE))
        assertFalse(navigator.isStackEmpty(Routes.FILES))
    }
}
