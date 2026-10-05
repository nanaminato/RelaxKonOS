package app.relaxkonos.mobile.ui.manage.git

import app.relaxkonos.mobile.ui.common.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.Spacing

/** Repository workflows and isolated remote builds share the native Git destination. */
@Composable
fun GitScreen(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier,
    initialBuildId: String? = null) {
    var section by rememberSaveable(owner) { mutableStateOf(if (initialBuildId == null) "workspace" else "build") }
    LaunchedEffect(initialBuildId) { if (initialBuildId != null) section = "build" }
    WorkspaceColumn(stringResource(R.string.git_title), onBack,
        listOf(WorkspaceDestination("workspace", R.string.workspace_workspace), WorkspaceDestination("branches", R.string.workspace_branches), WorkspaceDestination("history", R.string.workspace_history), WorkspaceDestination("conflicts", R.string.workspace_conflicts), WorkspaceDestination("repositories", R.string.git_repositories), WorkspaceDestination("build", R.string.workspace_build), WorkspaceDestination("environment", R.string.git_environment)), section, { section = it }, modifier, stateKey = owner) {
        GitWorkspaceSection(owner, section, onSelectSection = { section = it })
        WorkspaceSection(section == "build") { GitBuildSection(owner, initialBuildId) }
    }
}

/** A remote Git commit is built first; publishing its recorded image is a separate AD02 action. */
@Composable
private fun GitBuildSection(owner: SessionState.Active, initialBuildId: String?) {
    val container = appContainer()
    val scope = rememberCoroutineScope()
    val ownerKey = GitBuildOwnerKey(owner)
    val saver = remember(ownerKey, scope) {
        GitBuildEditor.saver(container.git, container.deployments, owner, { container.session.state.value === owner }, scope)
    }
    val editor = rememberSaveable(ownerKey, saver = saver) {
        GitBuildEditor(container.git, container.deployments, owner, { container.session.state.value === owner }, scope)
    }
    DisposableEffect(editor) { onDispose { editor.close() } }
    LaunchedEffect(editor) { editor.load() }
    LaunchedEffect(editor, initialBuildId) { editor.open(initialBuildId) }
    LaunchedEffect(editor, editor.selectedBuildId, editor.selected?.state) { editor.observeSelected() }
    with(editor) {
        Text(stringResource(R.string.git_build_title), style = MaterialTheme.typography.titleLarge)
        Text(stringResource(R.string.git_build_note), style = MaterialTheme.typography.bodySmall)
        OperationMessageDialog(problem?.let { stringResource(R.string.git_problem, it) }, onDismiss = { problem = null })
        notice?.let { Text(it, color = MaterialTheme.colorScheme.primary) }
        GitPanel(stringResource(R.string.git_build_source), collapsible = true, initiallyExpanded = initialBuildId == null) {
            OutlinedTextField(url, { changeUrl(it) },
                label = { Text(stringResource(R.string.git_build_url)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(reference, { changeReference(it) },
                label = { Text(stringResource(R.string.git_build_ref)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedButton(onClick = { listRefs() }, enabled = !active && url.isNotBlank()) { Text(stringResource(R.string.git_build_list_refs)) }
            if (remoteRefs.isNotEmpty()) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                remoteRefs.forEach { ref -> FilterChip(selected = reference == ref.name,
                        onClick = { changeReference(ref.name) }, label = { Text(ref.name) }) }
            }
            Text(stringResource(R.string.git_build_credential), style = MaterialTheme.typography.titleSmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                FilterChip(selected = credentialId == null, onClick = { selectCredential(null) },
                    label = { Text(stringResource(R.string.git_build_public)) })
                credentials.forEach { credential ->
                    FilterChip(selected = credentialId == credential.id,
                        onClick = { selectCredential(credential.id) },
                        label = { Text(credential.name) })
                }
            }
            GitPanel(stringResource(R.string.git_build_save_credential), collapsible = true, initiallyExpanded = false) {
                OutlinedTextField(credentialName, { credentialName = it },
                    label = { Text(stringResource(R.string.git_build_credential_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(credentialToken, { credentialToken = it },
                    label = { Text(stringResource(R.string.git_build_token)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation())
                OutlinedButton(onClick = { saveCredential() }, enabled = !active && credentialName.isNotBlank() && credentialToken.isNotBlank()) {
                    Text(stringResource(R.string.git_build_save_credential))
                }
            }
            GitPanel(stringResource(R.string.git_build_options), collapsible = true, initiallyExpanded = false) {
                OutlinedTextField(context, { changeContext(it) },
                    label = { Text(stringResource(R.string.git_build_context)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(dockerfile, { changeDockerfile(it) },
                    label = { Text(stringResource(R.string.git_build_dockerfile)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            }
            OutlinedButton(onClick = { resolve() }, enabled = !active && url.isNotBlank() && reference.isNotBlank()) {
                Text(stringResource(R.string.git_build_resolve))
            }
            resolved?.let { fixed ->
                SelectionContainer { Text(stringResource(R.string.git_build_fixed_sha, fixed.commitSha)) }
                Button(onClick = { startBuild(fixed) }, enabled = !active && context.isNotBlank() && dockerfile.isNotBlank()) {
                    Text(stringResource(R.string.git_build_start))
                }
            }
        }
        GitPanel(stringResource(R.string.git_build_history)) {
            PageActionRow(refresh = {
                TextButton(onClick = { refresh() }, enabled = !active) { ActionLabel(R.string.common_refresh) }
            })
            if (builds.isEmpty()) Text(stringResource(R.string.git_build_empty))
            builds.forEach { build ->
                OutlinedCard(onClick = { selectBuild(build.id) }, enabled = !active, modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        Text(build.reference, style = MaterialTheme.typography.titleSmall)
                        Text(gitBuildStateLabel(build.state), color = if (build.id == selectedBuildId) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                        GitCode(build.commitSha.take(12))
                        if (build.id == selectedBuildId) Text(stringResource(R.string.git_selected), style = MaterialTheme.typography.labelSmall)
                    }
                }
            }
        }
        selected?.let { build ->
            GitPanel(gitBuildStateLabel(build.state)) {
                SelectionContainer { Text("${build.commitSha}\n${build.repositoryUrl}") }
                build.problemCode?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (build.logs.isNotEmpty()) GitPanel(stringResource(R.string.git_build_logs), collapsible = true, initiallyExpanded = build.state == "failed") {
                    SelectionContainer { GitCode(build.logs.joinToString("\n")) }
                }
                if (build.state == "queued" || build.state == "running") {
                    OutlinedButton(onClick = { cancelBuild(build) }) { Text(stringResource(R.string.git_build_cancel)) }
                }
            }
            if (build.state == "succeeded" && build.imageReference != null && build.imageId != null) {
                GitPanel(stringResource(R.string.git_build_publish_title), collapsible = true, initiallyExpanded = false) {
                    Text(stringResource(R.string.git_build_publish_note), style = MaterialTheme.typography.bodySmall)
                    SelectionContainer { Text("${build.imageReference}\n${build.imageId}") }
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        FilterChip(selected = applicationId == null, onClick = { selectApplication(null) },
                            label = { Text(stringResource(R.string.git_build_new_app)) })
                        applications.forEach { app -> FilterChip(selected = applicationId == app.id,
                                onClick = { selectApplication(app.id) }, label = { Text(app.name) }) }
                    }
                    if (applicationId == null) {
                        OutlinedTextField(applicationName, { changeApplicationName(it) },
                            label = { Text(stringResource(R.string.git_build_app_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                        OutlinedTextField(containerPort, { changeContainerPort(it) },
                            label = { Text(stringResource(R.string.git_build_app_port)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    }
                    Button(onClick = { publish(build) }, enabled = !active && (applicationId != null ||
                        applicationName.isNotBlank() && (containerPort.toIntOrNull()?.let { it in 1..65535 } == true))) {
                        Text(stringResource(R.string.git_build_publish))
                    }
                }
            }
        }
    }
}

/** A bounded line comparison for review; saving still uses the server's exact byte version. */
internal fun lineDiff(original: String, edited: String): String {
    val before = original.lines()
    val after = edited.lines()
    val prefix = before.zip(after).takeWhile { it.first == it.second }.size
    val suffix = before.drop(prefix).asReversed().zip(after.drop(prefix).asReversed())
    .takeWhile { it.first == it.second }.size
    val removed = before.subList(prefix, before.size - suffix)
    val added = after.subList(prefix, after.size - suffix)
    val lines = (removed.map { "− $it" } + added.map { "+ $it" })
    return lines.take(120).joinToString("\n") + if (lines.size > 120) "\n…" else ""
}

private class GitBuildOwnerKey(private val owner: SessionState.Active) {
    override fun equals(other: Any?) = other is GitBuildOwnerKey && other.owner === owner
    override fun hashCode() = System.identityHashCode(owner)
}
