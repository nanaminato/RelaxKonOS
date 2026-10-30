package app.relaxkonos.mobile.ui.manage.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.data.ComponentRead
import app.relaxkonos.mobile.data.ComponentRepository
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException

/**
 * The host components one application depends on. Several ids mean alternatives: Tunnel Manager is
 * usable with either frpc or frps, so one installed component satisfies the dependency.
 */
object AppComponentDependencies {
    private val byRoute: Map<String, List<String>> = mapOf(
        Routes.MANAGE_WEBSITES to listOf("nginx"),
        Routes.MANAGE_PROXY to listOf("mihomo"),
        Routes.MANAGE_PROXY_SERVER to listOf("sing-box"),
        Routes.MANAGE_DOCKER to listOf("docker"),
        Routes.MANAGE_DOCKER_RESOURCES to listOf("docker"),
        Routes.MANAGE_DOCKER_CONTROL to listOf("docker"),
        Routes.MANAGE_SMB to listOf("samba"),
        Routes.MANAGE_SQUID to listOf("squid"),
        Routes.MANAGE_TUNNELS to listOf("frpc", "frps"),
        Routes.MANAGE_TAILSCALE to listOf("tailscale"),
    )

    fun componentsFor(route: String): List<String>? = byRoute[route]
}

/** What an application entry check may act on. */
enum class ComponentDependencyVerdict { Ready, Missing, Recovery, Busy, Unknown }

/**
 * Classifies one row the way the application entry check does. Only a measured absence blocks: an
 * unreadable state, a platform the host cannot provide and an external installation are not a reason
 * to refuse entry, and a pending recovery outranks a sibling's missing state.
 */
fun componentVerdict(read: ComponentRead): ComponentDependencyVerdict {
    val view = read.view ?: return ComponentDependencyVerdict.Unknown
    // A pending operation is the live task; ComponentStateView.recoveryRequired already folds it in,
    // so it must be checked first or a running install would read as a broken transaction.
    if (view.pendingOperationId != null) return ComponentDependencyVerdict.Busy
    if (view.recoveryRequired) return ComponentDependencyVerdict.Recovery
    val state = view.state ?: return if (view.unsupported) ComponentDependencyVerdict.Ready else ComponentDependencyVerdict.Unknown
    return if (state.ownership == "absent") ComponentDependencyVerdict.Missing else ComponentDependencyVerdict.Ready
}

/** Alternatives: one ready component satisfies the dependency, otherwise the decisive blocker wins. */
fun dependencyVerdict(reads: List<ComponentRead>): ComponentDependencyVerdict {
    if (reads.isEmpty()) return ComponentDependencyVerdict.Ready
    val verdicts = reads.map(::componentVerdict)
    if (verdicts.any { it == ComponentDependencyVerdict.Ready }) return ComponentDependencyVerdict.Ready
    for (kind in listOf(ComponentDependencyVerdict.Recovery, ComponentDependencyVerdict.Busy,
        ComponentDependencyVerdict.Missing, ComponentDependencyVerdict.Unknown)) {
        if (verdicts.any { it == kind }) return kind
    }
    return ComponentDependencyVerdict.Ready
}

/** A blocked entry; [rows] keeps the host answer so the prompt can name the decisive component. */
data class ComponentDependencyPrompt(val verdict: ComponentDependencyVerdict, val rows: List<ComponentRead>)

/**
 * Reads dependency rows without inventing state. A permission or transport failure yields an empty
 * answer, which [dependencyVerdict] treats as "do not block": an unreadable server never locks a user
 * out of an application on this client.
 */
suspend fun readComponentDependencies(owner: SessionState.Active, repository: ComponentRepository,
    componentIds: List<String>): List<ComponentRead> = try {
    when (val result = repository.read(owner, componentIds)) {
        is ApiResult.Success -> result.value
        else -> emptyList()
    }
} catch (cancelled: CancellationException) {
    throw cancelled
} catch (_: Exception) {
    emptyList()
}

/**
 * The application's own dependency entry: a compact action that opens the read-only dependency summary
 * and one navigation to Component Center. It is not a banner and carries no install command.
 */
@Composable
fun ComponentDependencyAction(route: String, onOpenComponents: () -> Unit, modifier: Modifier = Modifier) {
    val componentIds = AppComponentDependencies.componentsFor(route) ?: return
    val container = appContainer()
    val owner = container.activeSession ?: return
    var open by remember(owner) { mutableStateOf(false) }
    var rows by remember(owner, open) { mutableStateOf<List<ComponentRead>?>(null) }
    OutlinedButton(onClick = { open = true }, modifier = modifier) {
        Text(stringResource(R.string.components_dependency_entry))
    }
    if (!open) return
    LaunchedEffect(owner, open) {
        if (rows == null) rows = readComponentDependencies(owner, container.components, componentIds)
    }
    AlertDialog(
        onDismissRequest = { open = false },
        title = { Text(stringResource(R.string.components_dependency_entry)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.components_dependency_hint), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                val current = rows
                if (current == null) {
                    CircularProgressIndicator()
                } else {
                    current.forEach { read -> DependencyRow(read) }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { open = false; onOpenComponents() }) {
                Text(stringResource(R.string.components_open_center))
            }
        },
        dismissButton = { TextButton(onClick = { open = false }) { Text(stringResource(R.string.common_close)) } },
    )
}

@Composable
private fun DependencyRow(read: ComponentRead) {
    val definition = ComponentCatalog.definition(read.componentId)
    val view = read.view
    val state = view?.state
    Column(Modifier.fillMaxWidth()) {
        Text(stringResource(definition?.title ?: R.string.components_title), style = MaterialTheme.typography.titleSmall)
        Text(
            when {
                view == null -> stringResource(R.string.components_unavailable_reason, read.problem ?: "component.unavailable")
                state == null -> stringResource(if (view.unsupported) R.string.components_unsupported else R.string.components_state_unavailable)
                else -> stringResource(R.string.components_dependency_state, stringResource(ownershipLabel(view)),
                    state.activeVersion ?: stringResource(R.string.components_version_unknown))
            },
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        if (view?.recoveryRequired == true) {
            Text(stringResource(R.string.components_recovery), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.error)
        }
    }
}
