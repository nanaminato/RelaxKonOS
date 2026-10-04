package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.Composable
import androidx.compose.runtime.staticCompositionLocalOf
import app.relaxkonos.mobile.AppContainer
import kotlinx.coroutines.flow.StateFlow
import androidx.lifecycle.compose.collectAsStateWithLifecycle

/**
 * The single composition root instance.
 *
 * Providing it through a composition local keeps every screen free of constructor plumbing while
 * still leaving exactly one owner for the session, the vaults and the elevation coordinator.
 */
val LocalAppContainer = staticCompositionLocalOf<AppContainer> { error("No AppContainer was provided.") }

@Composable
fun appContainer(): AppContainer = LocalAppContainer.current

/** Observes presentation state only while the hosting lifecycle is started. */
@Composable
fun <T> StateFlow<T>.collectAsStateValue(): T = collectAsStateWithLifecycle().value
