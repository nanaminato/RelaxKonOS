package app.relaxkonos.mobile.ui.manage.git

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.horizontalScroll
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.editor.CodeTextField
import app.relaxkonos.mobile.ui.editor.TextEditorDialog
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay

@Composable
internal fun GitWorkspaceSection(owner: SessionState.Active, section: String, onSelectSection: (String) -> Unit) {
    val model: GitWorkspaceViewModel = viewModel()
    val state = model.state
    var name by remember(owner) { mutableStateOf("") }
    var directory by remember(owner) { mutableStateOf("") }
    var branchFilter by remember(owner) { mutableStateOf("") }
    var remoteBranches by remember(owner) { mutableStateOf(false) }
    var createBranch by remember(owner) { mutableStateOf(false) }
    var repositoryMenu by remember(owner) { mutableStateOf(false) }
    var historyAttempt by remember(owner, state.selectedId) { mutableStateOf(false) }
    var branch by remember(owner) { mutableStateOf("") }
    var path by remember(owner) { mutableStateOf("") }
    var openFile by remember(owner) { mutableStateOf(false) }
    var editor by remember(owner) { mutableStateOf<String?>(null) }
    var search by remember(owner) { mutableStateOf("") }
    var strategy by remember(owner) { mutableStateOf("ff-only") }
    var adoption by remember(owner) { mutableStateOf<PendingGitMutation?>(null) }
    var diffSection by remember(owner) { mutableStateOf<String?>(null) }
    LaunchedEffect(state.owner) { if (state.owner === owner) model.refresh() }
    DisposableEffect(model) { onDispose { model.stop() } }
    LaunchedEffect(state.installation?.operationId, state.installation?.state, state.busy) {
        if (state.installation?.state?.active == true && !state.busy) { delay(2500); model.pollInstall() }
    }
    LaunchedEffect(section, state.selectedId, state.busy) {
        if (section == "history" && state.selectedId != null && !state.busy && !state.historyLoaded && !historyAttempt) {
            historyAttempt = true
            model.history(0, search)
        }
    }
    val facts = state.facts
    val pending = state.pending.filter { it.repositoryId == state.selectedId }
    val ready = owner.executionEligibility.available && !state.busy && facts != null && pending.isEmpty() && !state.pendingInstallation && state.installation?.state?.active != true
    val ordinary = ready && facts?.conflicts?.let { it.operation == null && it.paths.isEmpty() } == true
    // Keep shared state and installation observation alive, but builds own their entire UI.
    if (section == "build") return
    if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
    if (!owner.executionEligibility.available) Text(stringResource(R.string.gw_identity_unavailable), color = MaterialTheme.colorScheme.error)
    GitWorkspaceProblem(state.problem.takeUnless { state.busy })
    if (state.saved) Text(stringResource(R.string.gw_receipt), color = MaterialTheme.colorScheme.primary)
    WorkspaceSection(section == "environment") {
        GitPanel(stringResource(R.string.git_environment)) {
            state.engine?.let { engine ->
                Text(stringResource(if (engine.available) R.string.gw_engine_ready else R.string.gw_engine_missing), style = MaterialTheme.typography.titleMedium)
                engine.version?.let { SelectionContainer { Text(it) } }
            }
            OutlinedButton(onClick = model::refresh, enabled = !state.busy) { ActionLabel(R.string.common_refresh) }
            GitInstallation(state, model, owner)
        }
    }
    if (section != "environment") {
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            if (section != "repositories" && state.repositories.isNotEmpty()) Box {
                OutlinedButton(onClick = { repositoryMenu = true }, enabled = !state.busy) {
                    Text(state.repositories.firstOrNull { it.id == state.selectedId }?.name ?: stringResource(R.string.git_repositories))
                }
                DropdownMenu(expanded = repositoryMenu, onDismissRequest = { repositoryMenu = false }) {
                    state.repositories.forEach { repository ->
                        DropdownMenuItem(text = { Text(repository.name) }, onClick = { repositoryMenu = false; model.select(repository.id) })
                    }
                    HorizontalDivider()
                    DropdownMenuItem(text = { Text(stringResource(R.string.git_repositories)) }, onClick = { repositoryMenu = false; onSelectSection("repositories") })
                }
            }
            TextButton(onClick = model::refresh, enabled = !state.busy) { ActionLabel(R.string.common_refresh) }
            if (section == "workspace" && facts != null) TextButton(onClick = { openFile = true }, enabled = ordinary) { Text(stringResource(R.string.git_open_file)) }
        }
        if (state.engine?.available == false || state.pendingInstallation || state.installation?.state?.active == true) {
            Text(stringResource(R.string.git_environment_required), style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { onSelectSection("environment") }) { Text(stringResource(R.string.git_open_environment)) }
        }
    }
    if (section != "workspace" && section != "environment" && section != "repositories") {
        if (pending.isNotEmpty()) {
            Text(stringResource(R.string.gw_unknown_note), style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { onSelectSection("workspace") }) { Text(stringResource(R.string.workspace_workspace)) }
        }
        if (state.engine?.available == true && state.repositories.isEmpty()) {
            Text(stringResource(R.string.git_no_repositories))
            TextButton(onClick = { onSelectSection("repositories") }) { Text(stringResource(R.string.git_open_repositories)) }
        }
    }
    WorkspaceSection(section == "workspace") {
        state.pending.forEach { entry ->
            Card { Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(state.repositories.firstOrNull { it.id == entry.repositoryId }?.name ?: entry.repositoryId)
                    Text(stringResource(R.string.gw_unknown))
                    Text(stringResource(actionLabel(entry.action)))
                    Text(stringResource(R.string.gw_unknown_note), style = MaterialTheme.typography.bodySmall)
                    OutlinedButton(onClick = { adoption = entry }, enabled = !state.busy) { Text(stringResource(R.string.gw_adopt)) }
                } }
        }
    }
    if (state.engine?.available == true && section != "environment") {
        if (state.repositories.isEmpty() && section == "workspace") {
            Text(stringResource(R.string.git_no_repositories))
            TextButton(onClick = { onSelectSection("repositories") }) { Text(stringResource(R.string.git_open_repositories)) }
        }
        WorkspaceSection(section == "repositories") {
            GitPanel(stringResource(R.string.git_repositories)) {
                if (state.repositories.isEmpty()) Text(stringResource(R.string.git_no_repositories))
                state.repositories.forEach { repository ->
                    OutlinedCard(onClick = { model.select(repository.id); onSelectSection("workspace") }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            Text(repository.name, style = MaterialTheme.typography.titleSmall)
                            GitCode(repository.path)
                        }
                    }
                }
            }
            GitPanel(stringResource(R.string.git_register_title), collapsible = true, initiallyExpanded = state.repositories.isEmpty()) {
                Text(stringResource(R.string.git_register_note), style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(name, { name = it }, enabled = !state.busy, label = { Text(stringResource(R.string.git_register_name)) }, modifier = Modifier.fillMaxWidth())
                RemotePathField(directory, { directory = it }, R.string.git_register_path, RemotePathKind.Directory, modifier = Modifier.fillMaxWidth())
                OutlinedButton(onClick = { model.register(name, directory) }, enabled = owner.executionEligibility.available && !state.busy && name.isNotBlank() && directory.isNotBlank()) { Text(stringResource(R.string.git_register)) }
            }
        }
    }
    if (facts != null && section != "environment" && section != "repositories") {
        Surface(color = MaterialTheme.colorScheme.secondaryContainer, shape = MaterialTheme.shapes.medium, modifier = Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.git_branch_status, facts.status.branch, facts.status.ahead, facts.status.behind),
                modifier = Modifier.padding(Spacing.md), style = MaterialTheme.typography.labelLarge)
        }
        if (section == "workspace" && !ordinary && (facts.conflicts.operation != null || facts.conflicts.paths.isNotEmpty())) {
            TextButton(onClick = { onSelectSection("conflicts") }) { Text(stringResource(R.string.gw_conflicts)) }
        }
        if (section == "branches") Text(stringResource(R.string.gw_upstream, facts.status.upstream ?: stringResource(R.string.gw_no_upstream)))
        if (facts.status.detached) Text(stringResource(R.string.gw_detached), color = MaterialTheme.colorScheme.error)
        if (section == "branches") Text(stringResource(R.string.gw_actions_note), style = MaterialTheme.typography.bodySmall)
        BoxWithConstraints {
            val branches: @Composable () -> Unit = {
                Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    GitPanel(stringResource(R.string.git_sync)) {
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            OutlinedButton(onClick = { model.prepare(GitMutation(GitAction.Fetch)) }, enabled = ordinary) { Text(stringResource(R.string.gw_fetch)) }
                            OutlinedButton(onClick = { model.prepare(GitMutation(GitAction.Push)) }, enabled = ordinary && !facts.status.detached && facts.status.upstream != null) { Text(stringResource(R.string.git_push)) }
                        }
                        GitPanel(stringResource(R.string.gw_pull_strategy) + " · " + stringResource(when (strategy) { "merge" -> R.string.gw_merge; "rebase" -> R.string.gw_rebase; else -> R.string.gw_ff_only }), collapsible = true, initiallyExpanded = false) {
                            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                listOf("ff-only" to R.string.gw_ff_only, "merge" to R.string.gw_merge, "rebase" to R.string.gw_rebase).forEach { (value, label) ->
                                    FilterChip(strategy == value, { strategy = value }, enabled = ordinary, label = { Text(stringResource(label)) })
                                }
                            }
                        }
                        OutlinedButton(onClick = { model.prepare(GitMutation(GitAction.Pull, strategy = strategy)) }, enabled = ordinary && !facts.status.detached && facts.status.upstream != null) { Text(stringResource(R.string.gw_pull)) }
                    }
                    GitPanel(stringResource(R.string.gw_branches)) {
                        OutlinedTextField(branchFilter, { branchFilter = it }, label = { Text(stringResource(R.string.git_branch_filter)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            FilterChip(!remoteBranches, { remoteBranches = false }, label = { Text(stringResource(R.string.git_local_branches)) })
                            FilterChip(remoteBranches, { remoteBranches = true }, label = { Text(stringResource(R.string.git_remote_branches)) })
                            TextButton(onClick = { createBranch = true }, enabled = ordinary) { Text(stringResource(R.string.gw_create_branch)) }
                        }
                        val rows = facts.branches.filter { it.remote == remoteBranches && it.name.contains(branchFilter, ignoreCase = true) }
                        if (rows.isEmpty()) Text(stringResource(R.string.git_no_results))
                        rows.forEach { row ->
                            OutlinedCard(Modifier.fillMaxWidth()) {
                                Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                    Text(row.name, style = MaterialTheme.typography.titleSmall)
                                    if (row.current) Text(stringResource(R.string.git_current_branch), color = MaterialTheme.colorScheme.primary)
                                    row.tracking?.let { GitCode(it) }
                                    if (row.tracking != null) Text(stringResource(R.string.gw_divergence, row.ahead, row.behind), style = MaterialTheme.typography.bodySmall)
                                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                        if (!row.current) OutlinedButton(onClick = { model.prepare(GitMutation(GitAction.Checkout, branch = row.name)) }, enabled = ordinary) { Text(stringResource(R.string.gw_checkout)) }
                                        if (!row.remote && !row.current) TextButton(onClick = { model.prepare(GitMutation(GitAction.DeleteBranch, branch = row.name)) }, enabled = ordinary) { Text(stringResource(R.string.gw_delete_branch)) }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            val changes: @Composable () -> Unit = {
                GitAdaptiveColumns(showSecond = facts.status.staged.isNotEmpty() || model.commitMessage.isNotBlank(), first = {
                        if (facts.status.staged.isEmpty() && facts.status.unstaged.isEmpty() && facts.status.untracked.isEmpty() && facts.conflicts.paths.isEmpty()) {
                            GitPanel(stringResource(R.string.git_workspace_clean)) {
                                TextButton(onClick = { onSelectSection("history") }) { Text(stringResource(R.string.workspace_history)) }
                        }
                    }
                        if (facts.status.unstaged.isNotEmpty()) GitChanges(R.string.gw_unstaged, facts.status.unstaged, false, ordinary, !state.busy, model, { diffSection = "workspace" }) { editor = it }
                        if (facts.status.untracked.isNotEmpty()) GitChanges(R.string.gw_untracked, facts.status.untracked, false, ordinary, !state.busy, model, { diffSection = "workspace" }) { editor = it }
                        if (facts.status.staged.isNotEmpty()) GitChanges(R.string.gw_staged, facts.status.staged, true, ordinary, !state.busy, model, { diffSection = "workspace" }) { editor = it }
                }, second = {
                        GitPanel(stringResource(R.string.git_commit)) {
                            Text(stringResource(R.string.git_file_count, facts.status.staged.size), style = MaterialTheme.typography.labelLarge)
                            OutlinedTextField(model.commitMessage, { model.commitMessage = it }, enabled = ordinary, label = { Text(stringResource(R.string.git_commit_message)) }, modifier = Modifier.fillMaxWidth(),
                            isError = model.commitMessage.length > 16384, supportingText = { if (model.commitMessage.length > 16384) Text(stringResource(R.string.gw_message_limit)) })
                            Button(onClick = { model.prepare(GitMutation(GitAction.Commit, message = model.commitMessage)) }, enabled = ordinary && facts.status.staged.isNotEmpty() && model.commitMessage.isNotBlank() && model.commitMessage.length <= 16384,
                            modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.git_commit)) }
                    }
                })
            }
            WorkspaceSection(section == "branches") { branches() }
            WorkspaceSection(section == "workspace") { changes() }
        }
        WorkspaceSection(section == "conflicts") {
            GitPanel(stringResource(R.string.gw_conflicts)) {
                if (facts.conflicts.operation != null || facts.conflicts.paths.isNotEmpty()) {
                    Text(stringResource(R.string.gw_conflicts), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.error)
                    Text(stringResource(operationLabel(facts.conflicts.operation)))
                    Text(stringResource(R.string.git_file_count, facts.conflicts.paths.size), style = MaterialTheme.typography.labelLarge)
                    facts.conflicts.paths.forEach { target -> OutlinedCard(Modifier.fillMaxWidth()) {
                            Column(Modifier.padding(Spacing.md)) {
                                GitCode(target)
                                TextButton(onClick = { model.conflict(target) }, enabled = ready) { Text(stringResource(R.string.gw_resolve)) }
                            }
                        } }
                    facts.conflicts.operation?.let { operation ->
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            Button(onClick = { model.prepare(GitMutation(GitAction.Continue, operation = operation)) }, enabled = ready && facts.conflicts.paths.isEmpty()) { Text(stringResource(R.string.gw_continue)) }
                            OutlinedButton(onClick = { model.prepare(GitMutation(GitAction.Abort, operation = operation)) }, enabled = ready) { Text(stringResource(R.string.gw_abort)) }
                        }
                    }
                } else {
                    Text(stringResource(R.string.git_no_conflicts))
                }
            }
        }
        WorkspaceSection(section == "history") {
            GitPanel(stringResource(R.string.gw_history)) {
                OutlinedTextField(search, { search = it }, enabled = !state.busy, label = { Text(stringResource(R.string.gw_history_search)) }, modifier = Modifier.fillMaxWidth())
                OutlinedButton(onClick = { model.history(0, search) }, enabled = !state.busy) { Text(stringResource(R.string.gw_history_load)) }
                state.commits.forEach { commit ->
                    OutlinedCard(onClick = { model.detail(commit.sha) }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            Text(commit.subject, style = MaterialTheme.typography.titleSmall)
                            GitCode(commit.shortSha)
                            Text("${commit.author} · ${commit.date}", style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
                if (state.historyLoaded) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    OutlinedButton(onClick = { model.history((state.skip - 50).coerceAtLeast(0), state.search) }, enabled = !state.busy && state.skip > 0) { Text(stringResource(R.string.gw_previous)) }
                    OutlinedButton(onClick = { model.history(state.skip + 50, state.search) }, enabled = !state.busy && state.commits.size == 50) { Text(stringResource(R.string.gw_next)) }
                    if (state.commits.isEmpty()) Text(stringResource(R.string.gw_history_empty))
                }
            }
            if (section == "history") state.detail?.let { detail ->
                GitDetailDialog(detail.subject, model::dismissDetail) {
                    SelectionContainer { Text("${detail.sha}\n${detail.author} · ${detail.date}\n${detail.subject}\n${detail.body.orEmpty()}") }
                    SelectionContainer { Text(stringResource(R.string.gw_parents, detail.parents.joinToString(" · "))) }
                    detail.changedFiles.forEach { file -> TextButton(onClick = { diffSection = "history"; model.showDiff(file.path, false, detail.sha) }, enabled = !state.busy) { Text(file.path) } }
                }
            }
        }
        if (section == diffSection) state.diff?.let { diff ->
            GitDetailDialog(stringResource(R.string.git_preview_diff), model::dismissDiff) { GitPatch(diff) }
        }
    }
    if (state.conflict == null) state.preview?.let { GitConfirmation(it, model) }
    state.conflict?.let { GitConflictDialog(it, model, ready) }
    editor?.let { target -> TextEditorDialog(owner, target, state.selectedId, onSaved = { model.refresh() }, onClose = { editor = null }) }
    if (createBranch) AlertDialog(onDismissRequest = { createBranch = false },
        title = { Text(stringResource(R.string.gw_create_branch)) },
        text = { OutlinedTextField(branch, { branch = it }, singleLine = true, label = { Text(stringResource(R.string.gw_branch_name)) }) },
        confirmButton = { TextButton(onClick = { createBranch = false; model.prepare(GitMutation(GitAction.CreateBranch, branch = branch)) }, enabled = ordinary && GitWorkspacePolicy.branch(branch)) { Text(stringResource(R.string.gw_create_branch)) } },
        dismissButton = { TextButton(onClick = { createBranch = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (openFile) AlertDialog(onDismissRequest = { openFile = false },
        title = { Text(stringResource(R.string.git_open_file)) },
        text = { OutlinedTextField(path, { path = it }, label = { Text(stringResource(R.string.git_file_path)) }, singleLine = true, modifier = Modifier.fillMaxWidth()) },
        confirmButton = { TextButton(onClick = { openFile = false; editor = path }, enabled = ordinary && GitWorkspacePolicy.path(path)) { Text(stringResource(R.string.git_open_file)) } },
        dismissButton = { TextButton(onClick = { openFile = false }) { Text(stringResource(R.string.common_cancel)) } })
    adoption?.let { marker -> AlertDialog(onDismissRequest = { adoption = null }, title = { Text(stringResource(R.string.gw_adopt)) },
            text = { Text(stringResource(R.string.gw_adopt_note)) }, confirmButton = { TextButton(onClick = { adoption = null; model.accept(marker) }) { Text(stringResource(R.string.gw_adopt)) } },
            dismissButton = { TextButton(onClick = { adoption = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}

@Composable
private fun GitChanges(title: Int, files: List<GitChange>, staged: Boolean, writable: Boolean, readable: Boolean, model: GitWorkspaceViewModel, onDiff: () -> Unit, edit: (String) -> Unit) {
    val paths = files.flatMap { listOfNotNull(it.oldPath, it.path) }.distinct()
    GitPanel(stringResource(title) + " · " + files.size) {
        OutlinedButton(onClick = { model.prepare(GitMutation(if (staged) GitAction.Unstage else GitAction.Stage,
                paths = paths)) }, enabled = writable && paths.isNotEmpty() && paths.size <= 5000) {
            Text(stringResource(if (staged) R.string.git_unstage_all else R.string.git_stage_all))
        }
        files.forEach { file ->
            OutlinedCard(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    GitCode(file.oldPath?.let { "$it → ${file.path}" } ?: file.path)
                    Text(file.status, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.secondary)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        TextButton(onClick = { onDiff(); model.showDiff(file.path, staged) }, enabled = readable) { Text(stringResource(R.string.git_preview_diff)) }
                        TextButton(onClick = { model.prepare(GitMutation(if (staged) GitAction.Unstage else GitAction.Stage,
                                paths = listOfNotNull(file.oldPath, file.path).distinct())) }, enabled = writable) { Text(stringResource(if (staged) R.string.gw_unstage else R.string.gw_stage)) }
                        TextButton(onClick = { edit(file.path) }, enabled = writable && file.status != "deleted") { Text(stringResource(R.string.git_open_file)) }
                    }
                }
            }
        }
    }
}

@Composable
private fun GitPatch(diff: GitDiff) {
    Text(diff.path, style = MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.gw_diff_counts, diff.additions, diff.deletions))
    if (diff.binary) Text(stringResource(R.string.gw_binary))
    if (diff.truncated) Text(stringResource(R.string.gw_truncated), color = MaterialTheme.colorScheme.error)
    SelectionContainer { Text(diff.patch, style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace, modifier = Modifier.horizontalScroll(rememberScrollState())) }
}

@Composable
private fun GitConfirmation(preview: GitMutationPreview, model: GitWorkspaceViewModel) {
    AlertDialog(onDismissRequest = model::dismissPreview, title = { Text(stringResource(actionLabel(preview.change.action))) },
        text = { Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(model.state.repositories.firstOrNull { it.id == preview.facts.repositoryId }?.name ?: preview.facts.repositoryId)
                Text(stringResource(R.string.gw_confirm_note))
                Text(stringResource(R.string.git_branch_status, preview.facts.status.branch, preview.facts.status.ahead, preview.facts.status.behind))
                preview.change.branch?.let { Text(it) }
                if (preview.change.action == GitAction.Pull) Text(stringResource(when (preview.change.strategy) { "rebase" -> R.string.gw_rebase; "merge" -> R.string.gw_merge; else -> R.string.gw_ff_only }))
                preview.change.operation?.let { Text(stringResource(operationLabel(it))) }
                preview.change.conflict?.let { Text(it.path); Text(stringResource(choiceLabel(preview.change.choice))) }
                preview.change.message?.let { SelectionContainer { Text(it) } }
                if (preview.change.action == GitAction.Commit) Text(stringResource(R.string.gw_commit_note))
                if (preview.change.action == GitAction.Abort) Text(stringResource(R.string.gw_abort_note), color = MaterialTheme.colorScheme.error)
                if (preview.change.action == GitAction.Push) Text(stringResource(R.string.gw_push_note))
                if (preview.change.choice == "delete") Text(stringResource(R.string.gw_delete_note), color = MaterialTheme.colorScheme.error)
                preview.diffs.forEach { GitPatch(it) }
                if (preview.change.choice == "edited") SelectionContainer { Text(lineDiff(preview.change.conflict?.result.orEmpty(), preview.change.content.orEmpty())) }
        } }, confirmButton = { Button(onClick = model::confirm, enabled = !model.state.busy) { Text(stringResource(R.string.gw_confirm)) } },
        dismissButton = { TextButton(onClick = model::dismissPreview, enabled = !model.state.busy) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable
private fun GitConflictDialog(file: GitConflictFile, model: GitWorkspaceViewModel, writable: Boolean) {
    var confirmClose by remember { mutableStateOf(false) }
    var query by remember { mutableStateOf("") }
    var replacement by remember { mutableStateOf("") }
    val dirty = model.conflictDraft.text != file.result.orEmpty()
    fun close() { if (!model.state.busy) { if (dirty) confirmClose = true else model.closeConflict() } }
    Dialog(onDismissRequest = ::close, properties = DialogProperties(usePlatformDefaultWidth = false, dismissOnClickOutside = false)) {
        Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().safeDrawingPadding().imePadding().padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                ScreenHeader(stringResource(R.string.gw_conflict_editor), onBack = ::close, subtitle = file.path)
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(stringResource(R.string.gw_conflict_note), style = MaterialTheme.typography.bodySmall)
                    listOf(R.string.gw_base to file.base, R.string.gw_ours to file.ours, R.string.gw_theirs to file.theirs).forEach { (title, content) ->
                        Text(stringResource(title), style = MaterialTheme.typography.titleMedium)
                        SelectionContainer { Text(content ?: stringResource(R.string.gw_no_text), style = MaterialTheme.typography.bodySmall) }
                    }
                    if (file.canEdit) {
                        Text(stringResource(R.string.gw_result), style = MaterialTheme.typography.titleMedium)
                        OutlinedTextField(query, { query = it }, label = { Text(stringResource(R.string.editor_find)) }, modifier = Modifier.fillMaxWidth(), enabled = !model.state.busy)
                        OutlinedTextField(replacement, { replacement = it }, label = { Text(stringResource(R.string.editor_replace)) }, modifier = Modifier.fillMaxWidth(), enabled = !model.state.busy)
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            TextButton(onClick = {
                                    val value = model.conflictDraft; val start = value.text.indexOf(query, value.selection.max).takeIf { it >= 0 } ?: value.text.indexOf(query)
                                    if (start >= 0) model.editConflict(value.copy(selection = TextRange(start, start + query.length)))
                            }, enabled = !model.state.busy && query.isNotEmpty()) { Text(stringResource(R.string.editor_find_next)) }
                            TextButton(onClick = { model.editConflict(TextFieldValue(model.conflictDraft.text.replace(query, replacement))) }, enabled = !model.state.busy && query.isNotEmpty()) { Text(stringResource(R.string.editor_replace_all)) }
                        }
                        CodeTextField(model.conflictDraft, model::editConflict, !model.state.busy)
                    } else Text(stringResource(R.string.gw_binary_conflict))
                    TextButton(onClick = { model.conflict(file.path) }, enabled = !model.state.busy) { Text(stringResource(R.string.gw_reload_revision)) }
                }
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    listOf("ours", "theirs", "delete", "edited").forEach { choice ->
                        val change = GitMutation(GitAction.Resolve, conflict = file, choice = choice, content = if (choice == "edited") model.conflictDraft.text else null)
                        val valid = runCatching { GitWorkspacePolicy.validate(change) }.isSuccess
                        OutlinedButton(onClick = { model.prepare(change) }, enabled = writable && valid) { Text(stringResource(choiceLabel(choice))) }
                    }
                }
            }
        }
        model.state.preview?.let { GitConfirmation(it, model) }
        if (confirmClose) AlertDialog(onDismissRequest = { confirmClose = false }, title = { Text(stringResource(R.string.editor_unsaved)) }, text = { Text(stringResource(R.string.editor_unsaved_note)) },
            confirmButton = { TextButton(onClick = { model.closeConflict() }) { Text(stringResource(R.string.editor_discard_changes)) } },
            dismissButton = { TextButton(onClick = { confirmClose = false }) { Text(stringResource(R.string.editor_continue_editing)) } })
    }
}

@Composable
private fun GitInstallation(state: GitWorkspaceState, model: GitWorkspaceViewModel, owner: SessionState.Active) {
    var confirm by remember(owner) { mutableStateOf(false) }
    var originalId by rememberSaveable(owner) { mutableStateOf("") }
    var identify by remember(owner) { mutableStateOf(false) }
    val allowed = owner.privilegedOperations && owner.serverPlatform.equals("linux", true)
    if (allowed && state.engine?.let { !it.available && it.canInstall && it.problemCode == "not_installed" } == true && !state.pendingInstallation && state.installation?.state?.active != true) {
        OutlinedButton(onClick = { confirm = true }, enabled = !state.busy && state.pending.isEmpty()) { Text(stringResource(R.string.gw_install)) }
    }
    if (state.pendingInstallation) Text(stringResource(R.string.gw_install_pending), color = MaterialTheme.colorScheme.error)
    if (allowed && state.pendingInstallation) OutlinedButton(onClick = { confirm = true }, enabled = !state.busy && state.pending.isEmpty()) { Text(stringResource(R.string.gw_retry_install)) }
    state.installation?.let { operation ->
        Text(stringResource(R.string.gw_install_task, operation.operationId))
        Text(app.relaxkonos.mobile.ui.manage.operations.installationStateLabel(operation.state))
        Text(app.relaxkonos.mobile.ui.manage.operations.installationStageLabel(operation.stage))
        operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
        operation.problemCode?.let { Text(app.relaxkonos.mobile.ui.manage.operations.installationProblemLabel(it), color = MaterialTheme.colorScheme.error) }
        if (!state.installationVerified) Text(stringResource(R.string.gw_install_unverified))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            TextButton(onClick = model::pollInstall, enabled = !state.busy) { ActionLabel(R.string.common_refresh) }
            OutlinedButton(onClick = model::cancelInstall, enabled = !state.busy && state.installationVerified && operation.state.active && operation.cancellable) { Text(stringResource(R.string.gw_cancel_install)) }
        }
    }
    if (allowed) {
        GitPanel(stringResource(R.string.gw_recover_install), collapsible = true, initiallyExpanded = state.pendingInstallation) {
            OutlinedTextField(originalId, { originalId = it }, label = { Text(stringResource(R.string.gw_original_id)) }, modifier = Modifier.fillMaxWidth(), enabled = !state.busy)
            OutlinedButton(onClick = { if (state.pendingInstallation) identify = true else model.recoverInstall(originalId, false) }, enabled = !state.busy && runCatching { InstallationRoutes.canonicalId(originalId.trim()) }.isSuccess) { Text(stringResource(R.string.gw_recover_install)) }
        }
    }
    if (confirm) AlertDialog(onDismissRequest = { confirm = false }, title = { Text(stringResource(if (state.pendingInstallation) R.string.gw_retry_install else R.string.gw_install)) },
        text = { Text(stringResource(if (state.pendingInstallation) R.string.gw_retry_install_note else R.string.gw_install_note)) },
        confirmButton = { TextButton(onClick = { confirm = false; model.install() }) { Text(stringResource(R.string.gw_confirm)) } }, dismissButton = { TextButton(onClick = { confirm = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (identify) AlertDialog(onDismissRequest = { identify = false }, title = { Text(stringResource(R.string.gw_recover_install)) }, text = { Text(stringResource(R.string.gw_identify_note, originalId)) },
        confirmButton = { TextButton(onClick = { identify = false; model.recoverInstall(originalId, true) }) { Text(stringResource(R.string.gw_confirm)) } }, dismissButton = { TextButton(onClick = { identify = false }) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable
private fun GitWorkspaceProblem(code: String?) {
    if (code == null) { OperationMessageDialog(null); return }
    val label = when (code) {
        "git.workspace.credentials" -> R.string.gw_credentials
        "git.workspace.facts_changed", "git.conflict.revision_mismatch" -> R.string.gw_facts_changed
        "git.workspace.conflicts" -> R.string.gw_conflicts
        "git.workspace.rejected" -> R.string.gw_rejected
        "git.workspace.installation_active" -> R.string.gw_install_pending
        "git.workspace.identity_unavailable" -> R.string.gw_identity_unavailable
        else -> R.string.gw_unverified
    }
    OperationMessageDialog(stringResource(label))
}
private fun actionLabel(action: GitAction) = when (action) {
    GitAction.Checkout -> R.string.gw_checkout; GitAction.CreateBranch -> R.string.gw_create_branch; GitAction.DeleteBranch -> R.string.gw_delete_branch
    GitAction.Fetch -> R.string.gw_fetch; GitAction.Pull -> R.string.gw_pull; GitAction.Stage -> R.string.gw_stage; GitAction.Unstage -> R.string.gw_unstage
    GitAction.Commit -> R.string.git_commit; GitAction.Push -> R.string.git_push; GitAction.Resolve -> R.string.gw_resolve; GitAction.Continue -> R.string.gw_continue; GitAction.Abort -> R.string.gw_abort
}
private fun operationLabel(operation: String?) = when (operation) { "merge" -> R.string.gw_merge; "rebase" -> R.string.gw_rebase; "revert" -> R.string.gw_revert; "cherry-pick" -> R.string.gw_cherry_pick; else -> R.string.gw_conflicts }
private fun choiceLabel(choice: String?) = when (choice) { "ours" -> R.string.gw_choose_ours; "theirs" -> R.string.gw_choose_theirs; "delete" -> R.string.gw_choose_delete; else -> R.string.gw_choose_edited }
