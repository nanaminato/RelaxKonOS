package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.*
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.nav.Routes
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.awaitCancellation

/** Actual module facts make disabled providers discoverable without inventing installed capabilities. */
@Composable
internal fun rememberServerModuleDirectory(owner: SessionState.Active): List<ServerModuleFact> {
    val container = appContainer()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var facts by remember(owner) { mutableStateOf<List<ServerModuleFact>>(emptyList()) }
    LaunchedEffect(owner, lifecycle) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            facts = emptyList()
            try {
                val result = container.serverModules.read(owner)
                if (container.session.state.value == owner && result is ApiResult.Success) facts = result.value
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { facts = emptyList() }
            awaitCancellation()
        }
    }
    return facts
}

internal fun missingModuleForRoute(route: String, capabilities: Set<String>, modules: List<ServerModuleFact>): String? {
    val capability = when (route) {
        Routes.TERMINAL -> ServerCapabilities.TERMINAL
        Routes.MANAGE_DOCKER -> ServerCapabilities.DOCKER
        Routes.MANAGE_WEBSITES -> ServerCapabilities.WEB_SERVER
        Routes.MANAGE_CERTIFICATES -> ServerCapabilities.CERTIFICATES
        Routes.MANAGE_FIREWALL -> ServerCapabilities.FIREWALL
        Routes.MANAGE_TAILSCALE -> ServerCapabilities.TAILSCALE
        Routes.MANAGE_PROXY -> ServerCapabilities.PROXY
        Routes.MANAGE_PROXY_SERVER -> ServerCapabilities.PROXY_SERVER
        Routes.MANAGE_MONITOR -> ServerCapabilities.METRICS
        Routes.MANAGE_DEPLOYMENTS -> ServerCapabilities.APPLICATION_DEPLOYMENTS
        Routes.MANAGE_TUNNELS -> ServerCapabilities.TUNNELS
        Routes.MANAGE_SMB -> ServerCapabilities.FILE_SERVICES
        Routes.MANAGE_SQUID -> ServerCapabilities.GATEWAY_PROXY
        else -> return null
    }
    if (capability in capabilities) return null
    return modules.firstOrNull { !it.effectiveEnabled && capability in it.capabilities }?.id
}
