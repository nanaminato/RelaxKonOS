package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.staticCompositionLocalOf
import app.relaxkonos.mobile.AppContainer
import kotlinx.coroutines.flow.StateFlow

/**
 * The single composition root instance.
 *
 * Providing it through a composition local keeps every screen free of constructor plumbing while
 * still leaving exactly one owner for the session, the vaults and the elevation coordinator.
 */
val LocalAppContainer = staticCompositionLocalOf<AppContainer> { error("No AppContainer was provided.") }

@Composable
fun appContainer(): AppContainer = LocalAppContainer.current

/**
 * Reads a `StateFlow` as Compose state.
 *
 * The module does not depend on `lifecycle-runtime-compose`, and none of the flows here are backed by
 * a lifecycle-scoped source, so a plain collector is enough — and it keeps the JVM tests free of
 * Android lifecycle machinery.
 */
@Composable
fun <T> StateFlow<T>.collectAsStateValue(): T {
    val state = remember(this) { mutableStateOf(value) }
    LaunchedEffect(this) { collect { state.value = it } }
    return state.value
}
