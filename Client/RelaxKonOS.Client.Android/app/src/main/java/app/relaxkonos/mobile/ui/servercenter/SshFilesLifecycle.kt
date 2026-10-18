package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.runtime.*
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner

/** Stop foreground SFTP work synchronously, including when Compose frames are suspended. */
@Composable
internal fun rememberSshFilesResumed(model: SshFilesController, hostId: String, revision: Int, allowed: () -> Boolean): Boolean {
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val currentAllowed by rememberUpdatedState(allowed)
    var resumed by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(lifecycle, model, hostId, revision, allowed()) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_RESUME -> {
                    resumed = true
                    if (currentAllowed()) model.resume(hostId) else model.stop()
                }
                Lifecycle.Event.ON_PAUSE -> { resumed = false; model.detach() }
                else -> Unit
            }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); model.detach() }
    }
    return resumed
}
