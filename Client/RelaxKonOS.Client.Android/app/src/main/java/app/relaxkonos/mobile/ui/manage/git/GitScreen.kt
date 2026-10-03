package app.relaxkonos.mobile.ui.manage.git

import app.relaxkonos.mobile.ui.common.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.RemotePathField
import app.relaxkonos.mobile.ui.common.RemotePathKind
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import java.util.UUID

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
    val git = container.git
    val deployments = container.deployments
    val scope = rememberCoroutineScope()
    var url by rememberSaveable(owner) { mutableStateOf("") }
    var reference by rememberSaveable(owner) { mutableStateOf("main") }
    var context by rememberSaveable(owner) { mutableStateOf(".") }
    var dockerfile by rememberSaveable(owner) { mutableStateOf("Dockerfile") }
    var credentialName by rememberSaveable(owner) { mutableStateOf("") }
    var credentialToken by remember(owner) { mutableStateOf("") }
    var credentialId by rememberSaveable(owner) { mutableStateOf<String?>(null) }
    var credentials by remember(owner) { mutableStateOf<List<GitBuildCredential>>(emptyList()) }
    var remoteRefs by remember(owner) { mutableStateOf<List<GitBuildRef>>(emptyList()) }
    var resolved by remember(owner) { mutableStateOf<GitBuildResolved?>(null) }
    var lastResolvedSha by rememberSaveable(owner) { mutableStateOf<String?>(null) }
    var buildKey by rememberSaveable(owner) { mutableStateOf<String?>(null) }
    var builds by remember(owner) { mutableStateOf<List<GitBuildOperation>>(emptyList()) }
    var selectedBuildId by rememberSaveable(owner) { mutableStateOf<String?>(null) }
    var appliedInitialBuildId by remember(owner) { mutableStateOf<String?>(null) }
    var applications by remember(owner) { mutableStateOf<List<DeploymentApplication>>(emptyList()) }
    var applicationId by rememberSaveable(owner) { mutableStateOf<String?>(null) }
    var applicationName by rememberSaveable(owner) { mutableStateOf("") }
    var containerPort by rememberSaveable(owner) { mutableStateOf("8080") }
    var definitionKey by rememberSaveable(owner) { mutableStateOf<String?>(null) }
    var publishKey by rememberSaveable(owner) { mutableStateOf<String?>(null) }
    var active by remember(owner) { mutableStateOf(false) }
    var problem by remember(owner) { mutableStateOf<String?>(null) }
    var notice by remember(owner) { mutableStateOf<String?>(null) }
    val selected = builds.firstOrNull { it.id == selectedBuildId }

    fun report(result: ApiResult<*>) {
        problem = when (result) {
            is ApiResult.Problem -> result.code
            is ApiResult.Transport -> "transport"
            else -> null
        }
    }
    fun refresh() {
        scope.launch {
            when (val result = git.builds(owner)) {
                is ApiResult.Success -> {
                    builds = result.value
                    if (selectedBuildId == null) selectedBuildId = builds.firstOrNull()?.id
                }
                else -> report(result)
            }
            when (val result = deployments.applications(owner)) {
                is ApiResult.Success -> applications = result.value.filter { it.sourceKind == "image" }
                else -> Unit
            }
        }
    }
    LaunchedEffect(owner) {
        when (val result = git.credentials(owner)) {
            is ApiResult.Success -> credentials = result.value
            else -> report(result)
        }
        when (val result = git.builds(owner)) {
            is ApiResult.Success -> {
                builds = result.value
                selectedBuildId = selectedBuildId?.takeIf { id -> builds.any { it.id == id } } ?: builds.firstOrNull()?.id
            }
            else -> report(result)
        }
        when (val result = deployments.applications(owner)) {
            is ApiResult.Success -> applications = result.value.filter { it.sourceKind == "image" }
            else -> Unit
        }
    }
    LaunchedEffect(owner, initialBuildId, builds) {
        if (initialBuildId != null && appliedInitialBuildId != initialBuildId && builds.any { it.id == initialBuildId }) {
            selectedBuildId = initialBuildId
            appliedInitialBuildId = initialBuildId
        }
    }
    LaunchedEffect(selectedBuildId, selected?.state) {
        val id = selectedBuildId ?: return@LaunchedEffect
        while (selected?.state == "queued" || selected?.state == "running") {
            delay(2500)
            when (val result = git.build(owner, id)) {
                is ApiResult.Success -> builds = builds.map { if (it.id == id) result.value else it }
                else -> break
            }
        }
    }

    Text(stringResource(R.string.git_build_title), style = MaterialTheme.typography.titleLarge)
    Text(stringResource(R.string.git_build_note), style = MaterialTheme.typography.bodySmall)
    OperationMessageDialog(problem?.let { stringResource(R.string.git_problem, it) }, onDismiss = { problem = null })
    notice?.let { Text(it, color = MaterialTheme.colorScheme.primary) }
    GitPanel(stringResource(R.string.git_build_source), collapsible = true, initiallyExpanded = initialBuildId == null) {
        OutlinedTextField(url, { url = it; resolved = null; buildKey = null; remoteRefs = emptyList() },
            label = { Text(stringResource(R.string.git_build_url)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(reference, { reference = it; resolved = null; buildKey = null },
            label = { Text(stringResource(R.string.git_build_ref)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedButton(onClick = {
                scope.launch {
                    active = true; problem = null
                    when (val result = git.refs(owner, url, credentialId)) {
                        is ApiResult.Success -> remoteRefs = result.value
                        else -> report(result)
                }
                    active = false
            }
        }, enabled = !active && url.isNotBlank()) { Text(stringResource(R.string.git_build_list_refs)) }
        if (remoteRefs.isNotEmpty()) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            remoteRefs.forEach { ref -> FilterChip(selected = reference == ref.name,
                    onClick = { reference = ref.name; resolved = null; buildKey = null }, label = { Text(ref.name) }) }
        }
        Text(stringResource(R.string.git_build_credential), style = MaterialTheme.typography.titleSmall)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            FilterChip(selected = credentialId == null, onClick = { credentialId = null; resolved = null; buildKey = null; remoteRefs = emptyList() },
                label = { Text(stringResource(R.string.git_build_public)) })
            credentials.forEach { credential ->
                FilterChip(selected = credentialId == credential.id,
                    onClick = { credentialId = credential.id; resolved = null; buildKey = null; remoteRefs = emptyList() },
                    label = { Text(credential.name) })
            }
        }
        GitPanel(stringResource(R.string.git_build_save_credential), collapsible = true, initiallyExpanded = false) {
            OutlinedTextField(credentialName, { credentialName = it },
                label = { Text(stringResource(R.string.git_build_credential_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(credentialToken, { credentialToken = it },
                label = { Text(stringResource(R.string.git_build_token)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation())
            OutlinedButton(onClick = {
                    scope.launch {
                        active = true; problem = null
                        when (val result = git.setCredential(owner, credentialName, credentialToken)) {
                            is ApiResult.Success -> {
                                credentials = credentials + result.value; credentialId = result.value.id
                                credentialToken = ""; credentialName = ""; resolved = null; buildKey = null
                        }
                            else -> report(result)
                    }
                        active = false
                }
            }, enabled = !active && credentialName.isNotBlank() && credentialToken.isNotBlank()) {
                Text(stringResource(R.string.git_build_save_credential))
            }
        }
        GitPanel(stringResource(R.string.git_build_options), collapsible = true, initiallyExpanded = false) {
            OutlinedTextField(context, { context = it; buildKey = null },
                label = { Text(stringResource(R.string.git_build_context)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(dockerfile, { dockerfile = it; buildKey = null },
                label = { Text(stringResource(R.string.git_build_dockerfile)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
        }
        OutlinedButton(onClick = {
                scope.launch {
                    active = true; problem = null
                    when (val result = git.resolve(owner, url, reference, credentialId)) {
                        is ApiResult.Success -> {
                            if (lastResolvedSha != result.value.commitSha) buildKey = null
                            resolved = result.value
                            lastResolvedSha = result.value.commitSha
                    }
                        else -> report(result)
                }
                    active = false
            }
        }, enabled = !active && url.isNotBlank() && reference.isNotBlank()) {
            Text(stringResource(R.string.git_build_resolve))
        }
        resolved?.let { fixed ->
            SelectionContainer { Text(stringResource(R.string.git_build_fixed_sha, fixed.commitSha)) }
            Button(onClick = {
                    scope.launch {
                        active = true; problem = null; notice = null
                        val key = buildKey ?: UUID.randomUUID().toString().also { buildKey = it }
                        val request = GitBuildRequest(fixed.repositoryUrl, fixed.reference, fixed.commitSha,
                        context.trim(), dockerfile.trim(), credentialId)
                        when (val result = git.startBuild(owner, request, key)) {
                            is ApiResult.Success -> {
                                builds = (listOf(result.value) + builds).distinctBy { it.id }
                                selectedBuildId = result.value.id; publishKey = null
                        }
                            else -> report(result)
                    }
                        active = false
                }
            }, enabled = !active && context.isNotBlank() && dockerfile.isNotBlank()) {
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
            OutlinedCard(onClick = { selectedBuildId = build.id; publishKey = null; problem = null }, enabled = !active, modifier = Modifier.fillMaxWidth()) {
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
                OutlinedButton(onClick = {
                        scope.launch {
                            when (val result = git.cancelBuild(owner, build.id)) {
                                is ApiResult.Success -> builds = builds.map { if (it.id == build.id) result.value else it }
                                else -> report(result)
                        }
                    }
                }) { Text(stringResource(R.string.git_build_cancel)) }
            }
        }
        if (build.state == "succeeded" && build.imageReference != null && build.imageId != null) {
            GitPanel(stringResource(R.string.git_build_publish_title), collapsible = true, initiallyExpanded = false) {
                Text(stringResource(R.string.git_build_publish_note), style = MaterialTheme.typography.bodySmall)
                SelectionContainer { Text("${build.imageReference}\n${build.imageId}") }
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    FilterChip(selected = applicationId == null, onClick = { applicationId = null; publishKey = null },
                        label = { Text(stringResource(R.string.git_build_new_app)) })
                    applications.forEach { app -> FilterChip(selected = applicationId == app.id,
                            onClick = { applicationId = app.id; publishKey = null }, label = { Text(app.name) }) }
                }
                if (applicationId == null) {
                    OutlinedTextField(applicationName, { applicationName = it; definitionKey = null },
                        label = { Text(stringResource(R.string.git_build_app_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(containerPort, { containerPort = it; definitionKey = null },
                        label = { Text(stringResource(R.string.git_build_app_port)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                }
                Button(onClick = {
                        scope.launch {
                            active = true; problem = null; notice = null
                            val target = if (applicationId != null) applicationId else {
                                val key = definitionKey ?: UUID.randomUUID().toString().also { definitionKey = it }
                                val definition = ImageDeploymentDefinition(applicationName, containerPort.toInt())
                                when (val created = deployments.createImageDefinition(owner, definition, key)) {
                                    is ApiResult.Success -> {
                                        applications = applications + created.value
                                        applicationId = created.value.id
                                        created.value.id
                                }
                                    else -> { report(created); null }
                            }
                        }
                            if (target != null) {
                                val key = publishKey ?: UUID.randomUUID().toString().also { publishKey = it }
                                when (val result = deployments.deployGitBuild(owner, target, build, key)) {
                                    is ApiResult.Success -> notice = result.value.operationId
                                    else -> report(result)
                            }
                        }
                            active = false
                    }
                }, enabled = !active && (applicationId != null ||
                    applicationName.isNotBlank() && (containerPort.toIntOrNull()?.let { it in 1..65535 } == true))) {
                    Text(stringResource(R.string.git_build_publish))
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
