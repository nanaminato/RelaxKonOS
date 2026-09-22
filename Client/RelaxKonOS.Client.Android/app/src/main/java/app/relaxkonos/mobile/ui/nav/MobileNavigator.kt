package app.relaxkonos.mobile.ui.nav

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshots.SnapshotStateList

/**
 * Navigation state for the shell.
 *
 * The three navigation rules of `RelaxKonOS.Mobile.V1.Design.md` §4.1 are expressed directly here
 * instead of emerging from a generic graph:
 *
 * 1. top-level destinations do not push onto each other — each owns its own stack, so leaving and
 *    returning to a destination restores where the user was;
 * 2. a sub-route may be rendered as a pane instead of a pushed page by the caller, in which case it
 *    simply is not pushed;
 * 3. signing out clears every stack while the saved connection profiles stay untouched.
 *
 * The class has no dependency on Compose beyond observable state, which keeps the rules testable on
 * the JVM.
 */
class MobileNavigator(initialDestination: String) {
    // This must be observable as well as each individual stack. It preserves the stacks for
    // navigation rules and tests, while [route] below is the single state screens subscribe to.
    private val stacks = mutableStateMapOf<String, SnapshotStateList<String>>()

    var currentDestination: String by mutableStateOf(initialDestination)
        private set

    /** The route that should be rendered right now. Updated with every navigation mutation. */
    var route: String by mutableStateOf(initialDestination)
        private set

    /** True when the destination is showing a pushed sub-page rather than its own root. */
    val isAtDestinationRoot: Boolean get() = peek(currentDestination).isEmpty()

    /** Switches top-level destination. The previous destination keeps its own stack. */
    fun select(destination: String) {
        currentDestination = destination
        route = peek(destination).lastOrNull() ?: destination
    }

    /** Pushes a sub-route onto the active destination's stack. */
    fun push(route: String) {
        if (route == Routes.HOME) {
            select(Routes.HOME)
            return
        }
        stackOf(currentDestination).add(route)
        this.route = route
    }

    /** Pops one sub-route. Returns false when the destination is already at its root. */
    fun pop(): Boolean {
        val stack = stackOf(currentDestination)
        if (stack.isEmpty()) {
            return false
        }
        stack.removeAt(stack.lastIndex)
        route = stack.lastOrNull() ?: currentDestination
        return true
    }

    /** Clears the active destination's stack without changing destination. */
    fun popToDestinationRoot(): Boolean {
        val stack = stackOf(currentDestination)
        if (stack.isEmpty()) {
            return false
        }
        stack.clear()
        route = currentDestination
        return true
    }

    /** Full reset used by sign-out and by a rejected refresh. */
    fun resetTo(destination: String) {
        stacks.values.forEach { it.clear() }
        stacks.clear()
        currentDestination = destination
        route = destination
    }

    /** True when the destination is on its own root, e.g. to decide whether back exits the app. */
    fun isStackEmpty(destination: String = currentDestination): Boolean = peek(destination).isEmpty()

    /** Reads without creating, so composition never mutates navigation state. */
    private fun peek(destination: String): List<String> = stacks[destination] ?: emptyList()

    private fun stackOf(destination: String): SnapshotStateList<String> = stacks.getOrPut(destination) { mutableStateListOf() }
}
