package app.relaxkonos.mobile.ui.manage.git

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

/** An intentionally small editor: working-tree save, commit, push and deployment stay separate. */
@Composable
fun GitScreen(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier,
    initialBuildId: String? = null) {
    val client = appContainer().git
    val scope = rememberCoroutineScope()
    var repositories by remember(owner) { mutableStateOf<List<GitRepository>>(emptyList()) }
    var selectedId by remember(owner) { mutableStateOf<String?>(null) }
    var branches by remember(owner) { mutableStateOf<List<GitBranch>>(emptyList()) }
    var status by remember(owner) { mutableStateOf<GitStatus?>(null) }
    var path by remember(owner) { mutableStateOf("") }
    var baseline by remember(owner) { mutableStateOf<GitTextFile?>(null) }
    var draft by remember(owner) { mutableStateOf("") }
    var message by remember(owner) { mutableStateOf("") }
    var problem by remember(owner) { mutableStateOf<String?>(null) }
    var notice by remember(owner) { mutableIntStateOf(0) }
    var busy by remember(owner) { mutableStateOf(false) }
    var preview by remember(owner) { mutableStateOf(false) }
    var serverFile by remember(owner) { mutableStateOf<GitTextFile?>(null) }
    var newRepositoryName by remember(owner) { mutableStateOf("") }
    var newRepositoryPath by remember(owner) { mutableStateOf("") }
    val drafts = remember(owner) { mutableStateMapOf<String, Pair<GitTextFile, String>>() }

    fun refresh(id: String) {
        scope.launch {
            when (val result = client.status(owner, id)) {
                is ApiResult.Success -> status = result.value
                is ApiResult.Problem -> problem = result.code
                is ApiResult.Transport -> problem = "transport"
            }
            when (val result = client.branches(owner, id)) {
                is ApiResult.Success -> branches = result.value
                else -> Unit
            }
        }
    }

    LaunchedEffect(owner) {
        when (val result = client.repositories(owner)) {
            is ApiResult.Success -> {
                repositories = result.value
                selectedId = selectedId?.takeIf { id -> result.value.any { it.id == id } } ?: result.value.firstOrNull()?.id
            }
            is ApiResult.Problem -> problem = result.code
            is ApiResult.Transport -> problem = "transport"
        }
    }
    LaunchedEffect(selectedId) {
        status = null
        branches = emptyList()
        baseline = null
        draft = ""
        preview = false
        serverFile = null
        selectedId?.let { id ->
            when (val result = client.status(owner, id)) {
                is ApiResult.Success -> status = result.value
                is ApiResult.Problem -> problem = result.code
                is ApiResult.Transport -> problem = "transport"
            }
            when (val result = client.branches(owner, id)) {
                is ApiResult.Success -> branches = result.value
                else -> Unit
            }
        }
    }

    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.git_title), onBack = onBack,
            subtitle = stringResource(R.string.git_subtitle))
        Text(stringResource(R.string.git_scope), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        problem?.let { code -> Text(
            if (code == "git-text-changed") stringResource(R.string.git_conflict_reload)
            else stringResource(R.string.git_problem, code), color = MaterialTheme.colorScheme.error) }
        if (notice != 0) Text(stringResource(notice), color = MaterialTheme.colorScheme.primary)

        if (repositories.isEmpty()) {
            Text(stringResource(R.string.git_no_repositories))
        } else {
            Text(stringResource(R.string.git_repository), style = MaterialTheme.typography.titleMedium)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                repositories.forEach { repository ->
                    FilterChip(selected = repository.id == selectedId,
                        onClick = { selectedId = repository.id; problem = null; notice = 0 },
                        label = { Text(repository.name) })
                }
            }
        }
        Text(stringResource(R.string.git_register_title), style = MaterialTheme.typography.titleMedium)
        Text(stringResource(R.string.git_register_note), style = MaterialTheme.typography.bodySmall)
        OutlinedTextField(newRepositoryName, { newRepositoryName = it },
            label = { Text(stringResource(R.string.git_register_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
        RemotePathField(newRepositoryPath, { newRepositoryPath = it }, R.string.git_register_path,
            RemotePathKind.Directory, modifier = Modifier.fillMaxWidth())
        OutlinedButton(onClick = {
            scope.launch {
                busy = true; problem = null; notice = 0
                when (val result = client.register(owner, newRepositoryName, newRepositoryPath)) {
                    is ApiResult.Success -> {
                        repositories = (repositories + result.value).distinctBy { it.id }
                        selectedId = result.value.id
                        newRepositoryName = ""; newRepositoryPath = ""
                        notice = R.string.git_registered
                    }
                    is ApiResult.Problem -> problem = result.code
                    is ApiResult.Transport -> problem = "transport"
                }
                busy = false
            }
        }, enabled = !busy && newRepositoryName.isNotBlank() && newRepositoryPath.isNotBlank()) {
            Text(stringResource(R.string.git_register))
        }

        selectedId?.let { id ->
            status?.let { current ->
                Text(stringResource(R.string.git_branch_status, current.branch, current.ahead, current.behind))
                if (branches.isNotEmpty()) Text(stringResource(R.string.git_branches, branches.joinToString { it.name }))
                if (current.conflicts.isNotEmpty())
                    Text(stringResource(R.string.git_conflicts, current.conflicts.joinToString { it.path }),
                        color = MaterialTheme.colorScheme.error)
                TextButton(onClick = { refresh(id) }, enabled = !busy) { Text(stringResource(R.string.common_refresh)) }
                val changes = (current.staged + current.unstaged + current.untracked).distinctBy { it.path }
                if (changes.isNotEmpty()) {
                    Text(stringResource(R.string.git_changed_files), style = MaterialTheme.typography.titleMedium)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        changes.forEach { change ->
                            AssistChip(onClick = { path = change.path; baseline = null; draft = ""; preview = false; serverFile = null },
                                label = { Text("${change.path} · ${change.status}") })
                        }
                    }
                }
            }
            OutlinedTextField(path, { path = it; baseline = null; draft = ""; preview = false; serverFile = null },
                label = { Text(stringResource(R.string.git_file_path)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            Button(onClick = {
                scope.launch {
                    busy = true; problem = null; notice = 0
                    when (val result = client.textFile(owner, id, path)) {
                        is ApiResult.Success -> {
                            val cached = drafts["$id\u0000${result.value.path}"]
                            baseline = cached?.first ?: result.value
                            draft = cached?.second ?: result.value.content
                            if (cached != null && cached.first.version != result.value.version)
                            {
                                serverFile = result.value
                                problem = "git-text-changed"
                            } else serverFile = null
                            preview = false
                        }
                        is ApiResult.Problem -> problem = result.code
                        is ApiResult.Transport -> problem = "transport"
                    }
                    busy = false
                }
            }, enabled = path.isNotBlank() && !busy) { Text(stringResource(R.string.git_open_file)) }

            baseline?.let { opened ->
                serverFile?.let { latest ->
                    Text(stringResource(R.string.git_server_changes), style = MaterialTheme.typography.titleSmall)
                    SelectionContainer { Text(lineDiff(latest.content, draft), style = MaterialTheme.typography.bodySmall) }
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        OutlinedButton(onClick = {
                            baseline = latest; draft = latest.content; serverFile = null; problem = null; preview = false
                            drafts.remove("$id\u0000${latest.path}")
                        }) { Text(stringResource(R.string.git_discard_draft)) }
                        OutlinedButton(onClick = {
                            baseline = latest; serverFile = null; problem = null; preview = true
                            drafts["$id\u0000${latest.path}"] = latest to draft
                        }) { Text(stringResource(R.string.git_compare_latest)) }
                    }
                }
                Text(stringResource(R.string.git_editor_limit), style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(draft, {
                    draft = it; preview = false
                    drafts["$id\u0000${opened.path}"] = opened to it
                },
                    label = { Text(opened.path) }, modifier = Modifier.fillMaxWidth().heightIn(min = Spacing.lg * 8),
                    minLines = 8)
                if (draft != opened.content) {
                    OutlinedButton(onClick = { preview = true }, enabled = !busy) {
                        Text(stringResource(R.string.git_preview_diff))
                    }
                    if (preview) {
                        Text(stringResource(R.string.git_diff_title), style = MaterialTheme.typography.titleSmall)
                        SelectionContainer {
                            Text(lineDiff(opened.content, draft), style = MaterialTheme.typography.bodySmall)
                        }
                        Button(onClick = {
                            scope.launch {
                                busy = true; problem = null; notice = 0
                                when (val result = client.save(owner, id, opened, draft)) {
                                    is ApiResult.Success -> {
                                        baseline = result.value; preview = false; serverFile = null; notice = R.string.git_saved
                                        drafts.remove("$id\u0000${opened.path}")
                                        refresh(id)
                                    }
                                    is ApiResult.Problem -> {
                                        problem = result.code
                                        if (result.status == 409) {
                                            when (val latest = client.textFile(owner, id, opened.path)) {
                                                is ApiResult.Success -> serverFile = latest.value
                                                else -> Unit
                                            }
                                        }
                                    }
                                    is ApiResult.Transport -> problem = "transport"
                                }
                                busy = false
                            }
                        }, enabled = !busy && serverFile == null && draft.toByteArray(Charsets.UTF_8).size <= 256 * 1024) {
                            Text(stringResource(R.string.git_save))
                        }
                    }
                }
                HorizontalDivider()
                OutlinedTextField(message, { message = it }, label = { Text(stringResource(R.string.git_commit_message)) },
                    modifier = Modifier.fillMaxWidth(), singleLine = true)
                Text(stringResource(R.string.git_actions_note), style = MaterialTheme.typography.bodySmall)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Button(onClick = {
                        scope.launch {
                            busy = true; problem = null; notice = 0
                            when (val result = client.commit(owner, id, opened.path, message)) {
                                is ApiResult.Success -> if (result.value.success) {
                                    notice = R.string.git_committed; message = ""; refresh(id)
                                } else problem = "commit-rejected"
                                is ApiResult.Problem -> problem = result.code
                                is ApiResult.Transport -> problem = "transport"
                            }
                            busy = false
                        }
                    }, enabled = !busy && serverFile == null && message.isNotBlank() && draft == opened.content &&
                        status?.conflicts?.isEmpty() == true) { Text(stringResource(R.string.git_commit)) }
                    OutlinedButton(onClick = {
                        scope.launch {
                            busy = true; problem = null; notice = 0
                            when (val result = client.push(owner, id)) {
                                is ApiResult.Success -> if (result.value.success) {
                                    notice = R.string.git_pushed; refresh(id)
                                } else problem = if (result.value.requiresCredentials) "credentials-required" else "push-rejected"
                                is ApiResult.Problem -> problem = result.code
                                is ApiResult.Transport -> problem = "transport"
                            }
                            busy = false
                        }
                    }, enabled = !busy && serverFile == null && draft == opened.content && status?.conflicts?.isEmpty() == true) {
                        Text(stringResource(R.string.git_push))
                    }
                }
            }
        }
        HorizontalDivider()
        GitBuildSection(owner, initialBuildId)
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
    problem?.let { Text(stringResource(R.string.git_problem, it), color = MaterialTheme.colorScheme.error) }
    notice?.let { Text(it, color = MaterialTheme.colorScheme.primary) }
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
    OutlinedTextField(context, { context = it; buildKey = null },
        label = { Text(stringResource(R.string.git_build_context)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
    OutlinedTextField(dockerfile, { dockerfile = it; buildKey = null },
        label = { Text(stringResource(R.string.git_build_dockerfile)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
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
    HorizontalDivider()
    Text(stringResource(R.string.git_build_history), style = MaterialTheme.typography.titleMedium)
    TextButton(onClick = { refresh() }, enabled = !active) { Text(stringResource(R.string.common_refresh)) }
    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        builds.forEach { build -> FilterChip(selected = build.id == selectedBuildId,
            onClick = { selectedBuildId = build.id; publishKey = null; problem = null },
            label = { Text("${build.reference} · ${build.state}") }) }
    }
    selected?.let { build ->
        SelectionContainer { Text("${build.commitSha}\n${build.repositoryUrl}") }
        build.problemCode?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        if (build.logs.isNotEmpty()) SelectionContainer {
            Text(build.logs.joinToString("\n"), style = MaterialTheme.typography.bodySmall)
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
        if (build.state == "succeeded" && build.imageReference != null && build.imageId != null) {
            HorizontalDivider()
            Text(stringResource(R.string.git_build_publish_title), style = MaterialTheme.typography.titleMedium)
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
