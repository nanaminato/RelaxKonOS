package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.components.ComponentDependencyAction
import app.relaxkonos.mobile.ui.manage.proxy.proxyOperationLabel
import app.relaxkonos.mobile.ui.manage.proxy.proxyStageLabel
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

private data class ProxyServerConfirmation(val action: () -> Unit)
private data class CredentialDraft(val credential: ProxyServerCredential?)
private data class InboundDraft(val configuration: ProxyServerConfiguration, val inbound: ProxyServerInbound?)
private data class PrincipalDraft(val principal: ProxyServerPrincipal?)

private const val OVERVIEW = "overview"
private const val INBOUNDS = "inbounds"
private const val ACCESS = "access"
private const val CONNECT = "connect"
private const val SUBSCRIPTIONS = "subscriptions"
private const val RECORDS = "records"

/** Proxy Server management, inbound drafts, access credentials and exports. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ProxyServerScreen(onBack: () -> Unit, modifier: Modifier = Modifier, onOpenComponents: () -> Unit = {}) {
    val container = appContainer()
    val model: ProxyServerViewModel = viewModel(factory = viewModelFactory {
        initializer { ProxyServerViewModel(container.session, container.proxyServer, container.operationIndex) }
    })
    ProxyServerContent(model, container.activeSession, onBack, modifier, onOpenComponents)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun ProxyServerContent(model: ProxyServerViewModel, owner: SessionState.Active?,
    onBack: () -> Unit, modifier: Modifier = Modifier, onOpenComponents: () -> Unit = {}) {
    val state = model.state
    val disclosureVersion = model.disclosureVersion
    val available = owner?.capabilities?.contains(ServerCapabilities.PROXY_SERVER) == true
    val canManage = available && owner?.privilegedOperations == true
    val canExport = owner?.privilegedOperations == true
    val epoch = model.sessionEpoch
    var section by rememberSaveable(owner, epoch) { mutableStateOf(OVERVIEW) }
    var confirm by remember(owner, epoch) { mutableStateOf<ProxyServerConfirmation?>(null) }
    var credentialDraft by remember(owner, epoch) { mutableStateOf<CredentialDraft?>(null) }
    var principalDraft by remember(owner, epoch) { mutableStateOf<PrincipalDraft?>(null) }
    var inboundDraft by remember(owner, epoch) { mutableStateOf<InboundDraft?>(null) }
    var baseUrlDraft by remember(owner, epoch) { mutableStateOf<ProxyServerConfiguration?>(null) }
    var recovering by remember(owner, epoch) { mutableStateOf(false) }
    var recoverId by remember(owner, epoch) { mutableStateOf("") }

    val facts = state.facts
    val editorOpen = credentialDraft != null || principalDraft != null || inboundDraft != null || baseUrlDraft != null

    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(model, owner, lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE) model.clearSensitiveOutputs()
        }
        lifecycle.addObserver(observer)
        onDispose {
            lifecycle.removeObserver(observer)
            model.clearSensitiveOutputs()
        }
    }
    LaunchedEffect(owner, epoch) { if (available) model.refresh() }
    LaunchedEffect(owner, epoch, state.operation?.operationId, state.operation?.state?.wire, state.busy, editorOpen) {
        if (!editorOpen) model.observeOperation()
    }
    OperationMessageDialog(
        if (state.busy || editorOpen) null
        else state.problemCode?.let { proxyServerProblemLabel(it) }
            ?: if (state.uncertain) stringResource(R.string.proxy_server_uncertain) else null,
        tone = if (state.problemCode == null) StatusTone.Warning else StatusTone.Danger)

    WorkspaceColumn(
        stringResource(R.string.proxy_server_title), onBack,
        listOf(
            WorkspaceDestination(OVERVIEW, R.string.workspace_overview),
            WorkspaceDestination("runtime", R.string.proxy_server_runtime_title),
            WorkspaceDestination(INBOUNDS, R.string.proxy_server_inbounds_title),
            WorkspaceDestination(ACCESS, R.string.proxy_server_access_title),
            WorkspaceDestination(CONNECT, R.string.proxy_server_connect_title),
            WorkspaceDestination(SUBSCRIPTIONS, R.string.proxy_server_subscriptions_title),
            WorkspaceDestination(RECORDS, R.string.workspace_records)),
        section, { if (section != it) model.clearSensitiveOutputs(); section = it }, modifier, stateKey = owner to epoch,
        contentSpacing = Spacing.sm, contentPadding = PaddingValues(Spacing.lg)) {
        if (!available) {
            Text(stringResource(R.string.error_capability_missing))
            return@WorkspaceColumn
        }

        WorkspaceSection(section == OVERVIEW) {
            ManagementCard {
                Text(stringResource(R.string.proxy_server_intro))
                if (facts == null) {
                    Text(stringResource(R.string.proxy_server_unavailable))
                } else {
                    ExecutionStatusChip(proxyServerServiceLabel(facts.status), proxyServerServiceWire(facts.status), task = false)
                    FactRow(R.string.proxy_server_status_version, facts.status.version ?: stringResource(R.string.proxy_server_state_unknown))
                    FactRow(R.string.proxy_server_status_revisions,
                        stringResource(R.string.proxy_server_revisions_value,
                            facts.configuration.revision,
                            facts.publishedRevision?.toString() ?: "-",
                            facts.previousRevision?.toString() ?: "-"))
                    FactRow(R.string.proxy_server_status_applied, proxyServerAppliedLabel(facts.status.applied))
                    FactRow(R.string.proxy_server_status_boot, proxyServerBootLabel(facts.status.bootEnabled))
                    FactRow(R.string.proxy_server_status_statistics, proxyServerStatisticsLabel(facts.capabilities.statistics))
                    if (facts.status.recoveryRequired) Text(stringResource(R.string.proxy_server_recovery_hint))
                    facts.status.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyServerProblemLabel(it)) }
                }
                PageActionRow(refresh = {
                    TextButton(enabled = !state.busy, onClick = model::refresh) { ActionLabel(R.string.common_refresh) }
                }, actions = {
                    ComponentDependencyAction(Routes.MANAGE_PROXY_SERVER, onOpenComponents)
                })
                RefreshProgressIndicator(visible = state.busy)
                state.observedAtMillis?.let {
                    Text(stringResource(R.string.proxy_server_observed, DateFormat.getDateTimeInstance().format(Date(it))),
                        style = MaterialTheme.typography.bodySmall)
                }
            }
            if (facts != null && canManage) ManagementCard {
                Text(stringResource(R.string.proxy_server_runtime_title), style = MaterialTheme.typography.titleMedium)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    if (facts.status.installed) {
                        ProxyServerRuntimeAction.entries.forEach { action ->
                            OutlinedButton(enabled = state.ready && !facts.status.recoveryRequired, onClick = {
                                if (action == ProxyServerRuntimeAction.Restart ||
                                    (action == ProxyServerRuntimeAction.Stop && facts.status.running)) {
                                    confirm = ProxyServerConfirmation { model.runtime(action) }
                                } else model.runtime(action)
                            }) { Text(proxyServerRuntimeActionLabel(action)) }
                        }
                        if (facts.status.recoveryRequired) {
                            OutlinedButton(enabled = state.ready, onClick = { confirm = ProxyServerConfirmation(model::recover) }) {
                                Text(stringResource(R.string.proxy_server_action_recover))
                            }
                        }
                    }
                }
            }
            if (facts != null && canManage) ManagementCard {
                Text(stringResource(R.string.proxy_server_publication_title), style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.proxy_server_publication_hint), style = MaterialTheme.typography.bodySmall)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    OutlinedButton(enabled = state.ready, onClick = model::validate) { Text(stringResource(R.string.proxy_server_action_validate)) }
                    OutlinedButton(enabled = state.ready, onClick = {
                        confirm = ProxyServerConfirmation(model::publish)
                    }) { Text(stringResource(R.string.proxy_server_action_publish)) }
                    if (facts.previousRevision != null) {
                        OutlinedButton(enabled = state.ready, onClick = {
                            confirm = ProxyServerConfirmation(model::rollback)
                        }) { Text(stringResource(R.string.proxy_server_action_rollback)) }
                    }
                }
                state.validation?.let { validation ->
                    Text(stringResource(if (validation.valid) R.string.proxy_server_validation_ok else R.string.proxy_server_validation_failed),
                        color = if (validation.valid) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error)
                    validation.problems.forEach { Text(proxyServerProblemLabel(it)) }
                    validation.preview?.takeIf(String::isNotBlank)?.let {
                        Text(it, style = MaterialTheme.typography.bodySmall, modifier = Modifier.heightIn(max = 220.dp).verticalScroll(rememberScrollState()))
                    }
                }
            }
        }

        WorkspaceSection(section == "runtime") { if (section == "runtime") SingBoxRuntimePanel() }

        WorkspaceSection(section == INBOUNDS) {
            ManagementCard {
                Text(stringResource(R.string.proxy_server_inbounds_title), style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.proxy_server_inbound_draft_note), style = MaterialTheme.typography.bodySmall)
                if (canManage && facts != null) {
                    TextButton(enabled = state.ready, onClick = { inboundDraft = InboundDraft(facts.configuration, null) }) { Text(stringResource(R.string.proxy_server_inbound_add)) }
                    TextButton(enabled = state.ready, onClick = { baseUrlDraft = facts.configuration }) { Text(stringResource(R.string.proxy_server_subscription_base)) }
                }
                val inbounds = facts?.configuration?.inbounds.orEmpty()
                if (inbounds.isEmpty()) Text(stringResource(R.string.proxy_server_inbounds_empty))
                inbounds.forEach { inbound ->
                    HorizontalDivider()
                    Text(inbound.name, style = MaterialTheme.typography.titleSmall)
                    Text(proxyServerProtocolLabel(inbound.protocol))
                    Text(stringResource(R.string.proxy_server_inbound_listen, inbound.listenAddress, inbound.listenPort),
                        style = MaterialTheme.typography.bodySmall)
                    Text(stringResource(R.string.proxy_server_inbound_public, inbound.publicHost, inbound.publicPort),
                        style = MaterialTheme.typography.bodySmall)
                    if (canManage && facts != null) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        TextButton(enabled = state.ready, onClick = { inboundDraft = InboundDraft(facts.configuration, inbound) }) { Text(stringResource(R.string.common_edit)) }
                        TextButton(enabled = state.ready && facts.credentials.none { inbound.id in it.inboundIds }, onClick = {
                            val config = facts.configuration
                            confirm = ProxyServerConfirmation { model.saveConfiguration(SaveProxyServerConfiguration(config.revision, config.inbounds.filter { it.id != inbound.id }, config.subscriptionBaseUrl)) }
                        }) { Text(stringResource(R.string.common_delete)) }
                    }
                    if (!inbound.enabled) Text(stringResource(R.string.proxy_server_inbound_disabled), style = MaterialTheme.typography.bodySmall)
                }
            }
        }

        WorkspaceSection(section == ACCESS) {
            ManagementCard {
                Text(stringResource(R.string.proxy_server_access_principals), style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.proxy_server_principal_hint), style = MaterialTheme.typography.bodySmall)
                val principals = facts?.principals.orEmpty()
                if (principals.isEmpty()) Text(stringResource(R.string.proxy_server_principals_empty))
                principals.forEach { principal ->
                    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Column(Modifier.weight(1f)) {
                            Text(principal.displayName)
                            Text(stringResource(R.string.proxy_server_principal_credential_count, principal.credentialCount),
                                style = MaterialTheme.typography.bodySmall)
                        }
                        if (canManage) TextButton(onClick = { principalDraft = PrincipalDraft(principal) }) { Text(stringResource(R.string.common_edit)) }
                        if (canManage) TextButton(onClick = {
                            confirm = ProxyServerConfirmation { model.deletePrincipal(principal.id) }
                        }) { ActionLabel(R.string.common_delete) }
                    }
                }
                if (canManage) OutlinedButton(enabled = state.ready, onClick = { principalDraft = PrincipalDraft(null) }) {
                    Text(stringResource(R.string.proxy_server_principal_add))
                }
            }
            ManagementCard {
                Text(stringResource(R.string.proxy_server_access_credentials), style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.proxy_server_credential_hint), style = MaterialTheme.typography.bodySmall)
                val credentials = facts?.credentials.orEmpty()
                if (credentials.isEmpty()) Text(stringResource(R.string.proxy_server_credentials_empty))
                credentials.forEach { credential ->
                    HorizontalDivider()
                    Text(credential.name, style = MaterialTheme.typography.titleSmall)
                    Text(credential.expiresAtMillis?.let {
                        stringResource(R.string.proxy_server_credential_expires, DateFormat.getDateInstance().format(Date(it)))
                    } ?: stringResource(R.string.proxy_server_credential_no_expiry), style = MaterialTheme.typography.bodySmall)
                    if (!credential.applied) Text(stringResource(R.string.proxy_server_credential_pending), style = MaterialTheme.typography.bodySmall)
                    if (!credential.enabled) Text(stringResource(R.string.proxy_server_credential_disabled), style = MaterialTheme.typography.bodySmall)
                    if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        TextButton(onClick = { credentialDraft = CredentialDraft(credential) }) { Text(stringResource(R.string.common_edit)) }
                        TextButton(enabled = state.ready, onClick = {
                            confirm = ProxyServerConfirmation { model.rotateCredential(credential.id, credential.enabled, credential.expiresAtMillis) }
                        }) { Text(stringResource(R.string.proxy_server_credential_rotate)) }
                        TextButton(enabled = state.ready, onClick = {
                            confirm = ProxyServerConfirmation { model.deleteCredential(credential.id) }
                        }) { ActionLabel(R.string.common_delete) }
                    }
                }
                if (canManage) OutlinedButton(enabled = state.ready && !facts?.principals.isNullOrEmpty(), onClick = {
                    credentialDraft = CredentialDraft(null)
                }) { Text(stringResource(R.string.proxy_server_credential_add)) }
            }
        }

        WorkspaceSection(section == CONNECT) {
            ManagementCard {
                Text(stringResource(R.string.proxy_server_connect_intro))
                if (!canExport) Text(stringResource(R.string.proxy_server_connect_requires_permission), color = MaterialTheme.colorScheme.error)
                val credentials = facts?.credentials.orEmpty()
                if (credentials.isEmpty()) Text(stringResource(R.string.proxy_server_credentials_empty))
                credentials.forEach { credential ->
                    RadioOption(credential.id == model.selectedCredentialId, credential.name, canExport) {
                        model.selectCredential(credential.id)
                        model.loadExportFormats(credential.id)
                    }
                }
                if (model.selectedCredentialId != null) {
                    RefreshProgressIndicator(visible = state.exporting)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        ProxyServerExportFormats.all.forEach { format ->
                            val option = state.exportOptions.firstOrNull { it.format == format }
                            OutlinedButton(enabled = canExport && state.ready && option?.supported == true, onClick = { model.export(owner, disclosureVersion, format) }) {
                                Text(proxyServerFormatLabel(format))
                            }
                        }
                    }
                    state.exportOptions.filterNot { it.supported }.forEach { option ->
                        Text(stringResource(R.string.proxy_server_connect_format_unavailable,
                            proxyServerFormatLabel(option.format), option.reason ?: ""), style = MaterialTheme.typography.bodySmall)
                    }
                }
                state.export?.let { export ->
                    val qr = remember(export) { ProxyServerQr.encode(export.format, export.content)?.let { matrix ->
                        val pixels = IntArray(matrix.width * matrix.height) { index -> if (matrix[index % matrix.width, index / matrix.width]) android.graphics.Color.BLACK else android.graphics.Color.WHITE }
                        android.graphics.Bitmap.createBitmap(pixels, matrix.width, matrix.height, android.graphics.Bitmap.Config.ARGB_8888).asImageBitmap()
                    } }
                    qr?.let { androidx.compose.foundation.Image(it, stringResource(R.string.proxy_server_connect_qr), Modifier.size(256.dp)) }
                    val clipboard = LocalClipboardManager.current
                    SelectionContainer {
                        Text(export.content, style = MaterialTheme.typography.bodySmall, modifier = Modifier.heightIn(max = 240.dp).verticalScroll(rememberScrollState()))
                    }
                    export.warning?.takeIf(String::isNotBlank)?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        OutlinedButton(onClick = { clipboard.setText(AnnotatedString(export.content)) }) {
                            Text(stringResource(R.string.proxy_server_connect_copy))
                        }
                        TextButton(onClick = model::clearExport) { Text(stringResource(R.string.proxy_server_connect_clear)) }
                    }
                }
                Text(stringResource(R.string.proxy_server_connect_secret_note), style = MaterialTheme.typography.bodySmall)
            }
        }

        WorkspaceSection(section == SUBSCRIPTIONS) {
            ManagementCard {
                Text(stringResource(R.string.proxy_server_subscriptions_title), style = MaterialTheme.typography.titleMedium)
                Text(stringResource(R.string.proxy_server_subscription_hint), style = MaterialTheme.typography.bodySmall)
                val credentials = facts?.credentials.orEmpty()
                credentials.forEach { credential ->
                    SubscriptionIssuer(credential, canExport && state.ready, model, owner, disclosureVersion)
                }
                state.issued?.let { issued ->
                    HorizontalDivider()
                    Text(stringResource(R.string.proxy_server_subscription_token_once), style = MaterialTheme.typography.titleSmall)
                    Text(stringResource(R.string.proxy_server_subscription_token_note), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                    SelectionContainer { Text(issued.token, style = MaterialTheme.typography.bodySmall) }
                    if (issued.urls.isNotEmpty()) SelectionContainer { Text(issued.urls.joinToString("\n"), style = MaterialTheme.typography.bodySmall) }
                    val clipboard = LocalClipboardManager.current
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        OutlinedButton(onClick = { clipboard.setText(AnnotatedString(issued.urls.joinToString("\n").ifBlank { issued.token })) }) {
                            Text(stringResource(R.string.proxy_server_connect_copy))
                        }
                        TextButton(onClick = model::clearIssued) { Text(stringResource(R.string.proxy_server_connect_clear)) }
                    }
                }
            }
            ManagementCard {
                Text(stringResource(R.string.proxy_server_subscription_list), style = MaterialTheme.typography.titleMedium)
                val subscriptions = facts?.subscriptions.orEmpty()
                if (subscriptions.isEmpty()) Text(stringResource(R.string.proxy_server_subscription_empty))
                subscriptions.forEach { subscription ->
                    HorizontalDivider()
                    Text(subscription.credentialId, style = MaterialTheme.typography.bodySmall)
                    Text(subscription.expiresAtMillis?.let {
                        stringResource(R.string.proxy_server_subscription_expires, DateFormat.getDateInstance().format(Date(it)))
                    } ?: stringResource(R.string.proxy_server_credential_no_expiry), style = MaterialTheme.typography.bodySmall)
                    if (subscription.revoked) Text(stringResource(R.string.proxy_server_subscription_revoked), color = MaterialTheme.colorScheme.error)
                    if (canManage && !subscription.revoked) TextButton(enabled = state.ready, onClick = {
                        confirm = ProxyServerConfirmation { model.revokeSubscription(subscription.id) }
                    }) { Text(stringResource(R.string.proxy_server_subscription_revoke)) }
                }
            }
        }

        WorkspaceSection(section == RECORDS) {
            val operation = state.operation
            if (operation == null) {
                ManagementCard { Text(stringResource(R.string.proxy_server_records_empty)) }
            } else {
                ManagementCard {
                    Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
                    ExecutionStatusChip(if (state.operationVerified) proxyOperationLabel(operation.state) else stringResource(R.string.proxy_server_uncertain),
                        if (state.operationVerified) operation.state.wire else null)
                    if (state.operationVerified) {
                        Text(proxyStageLabel(operation.stage))
                        operation.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyServerProblemLabel(it)) }
                    }
                    PageActionRow(refresh = {
                        TextButton(enabled = !state.busy, onClick = model::pollOperation) { ActionLabel(R.string.common_refresh) }
                    })
                }
            }
            if (canManage) ManagementCard {
                Text(stringResource(R.string.proxy_server_records_recover_hint), style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(recoverId, { recoverId = it }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    label = { Text(stringResource(R.string.proxy_server_records_operation_id)) })
                TextButton(enabled = !state.busy && recoverId.isNotBlank(), onClick = { recovering = true }) {
                    Text(stringResource(R.string.proxy_server_records_recover))
                }
            }
        }
    }

    inboundDraft?.let { draft -> key(owner, epoch, draft) {
        ProxyServerInboundEditor(draft.configuration, draft.inbound, state.busy, { request -> model.saveConfiguration(request); inboundDraft = null }) { inboundDraft = null }
    } }
    baseUrlDraft?.let { config -> key(owner, epoch, config) {
        ProxyServerBaseUrlEditor(config, state.busy, { request -> model.saveConfiguration(request); baseUrlDraft = null }) { baseUrlDraft = null }
    } }
    credentialDraft?.let { draft -> key(owner, epoch, draft) {
        CredentialEditor(draft, facts, state.busy, { id, principalId, name, inboundIds, enabled, expiresMillis ->
            model.saveCredential(id, principalId, name, inboundIds, enabled, expiresMillis)
            credentialDraft = null
        }) { credentialDraft = null }
    } }
    principalDraft?.let { draft -> key(owner, epoch, draft) {
        PrincipalEditor(draft, state.busy, { id, name, userId ->
            model.savePrincipal(id, name, userId)
            principalDraft = null
        }) { principalDraft = null }
    } }
    confirm?.let { request ->
        AlertDialog(onDismissRequest = { confirm = null },
            title = { Text(stringResource(R.string.proxy_server_confirm_title)) },
            text = { Text(stringResource(R.string.proxy_server_confirm_note)) },
            confirmButton = {
                Button(enabled = !state.busy, onClick = { confirm = null; request.action() }) { Text(stringResource(R.string.proxy_server_confirm_title)) }
            },
            dismissButton = { TextButton(onClick = { confirm = null }) { ActionLabel(R.string.common_cancel) } })
    }
    if (recovering) AlertDialog(onDismissRequest = { recovering = false },
        title = { Text(stringResource(R.string.proxy_server_records_recover)) },
        text = { Text(stringResource(R.string.proxy_server_records_recover_hint)) },
        confirmButton = {
            Button(enabled = !state.busy && recoverId.isNotBlank(), onClick = {
                model.recoverOperation(recoverId); recovering = false
            }) { ActionLabel(R.string.common_refresh) }
        },
        dismissButton = { TextButton(onClick = { recovering = false }) { ActionLabel(R.string.common_cancel) } })
}

@Composable private fun FactRow(label: Int, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(stringResource(label), style = MaterialTheme.typography.bodySmall)
        Text(value, style = MaterialTheme.typography.bodySmall)
    }
}

@Composable private fun proxyServerRuntimeActionLabel(action: ProxyServerRuntimeAction) = stringResource(when (action) {
    ProxyServerRuntimeAction.Start -> R.string.proxy_server_action_start
    ProxyServerRuntimeAction.Stop -> R.string.proxy_server_action_stop
    ProxyServerRuntimeAction.Restart -> R.string.proxy_server_action_restart
    ProxyServerRuntimeAction.Enable -> R.string.proxy_server_action_boot_enable
    ProxyServerRuntimeAction.Disable -> R.string.proxy_server_action_boot_disable
})

/** One credential's subscription form. Local state, so an abandoned form leaves nothing behind. */
@Composable private fun SubscriptionIssuer(credential: ProxyServerCredential, enabled: Boolean, model: ProxyServerViewModel, owner: SessionState.Active?, disclosureVersion: Long) {
    var expanded by remember(credential.id) { mutableStateOf(false) }
    var formats by remember(credential.id) { mutableStateOf(setOf(ProxyServerExportFormats.URI)) }
    TextButton(enabled = enabled, onClick = { expanded = !expanded }) {
        Text(stringResource(R.string.proxy_server_subscription_issue_for, credential.name))
    }
    LaunchedEffect(owner, disclosureVersion) { expanded = false }
    if (expanded) WorkspaceEditorDialog(stringResource(R.string.proxy_server_subscription_issue), model.state.busy, { expanded = false }) {
        ProxyServerExportFormats.all.forEach { format ->
            CheckboxOption(formats.contains(format), proxyServerFormatLabel(format), enabled) { checked ->
                formats = if (checked) formats + format else formats - format
            }
        }
        Button(enabled = enabled && formats.isNotEmpty(), onClick = {
            model.issueSubscription(owner, disclosureVersion, credential.id, ProxyServerExportFormats.all.filter { it in formats }, null)
            expanded = false
        }) { Text(stringResource(R.string.proxy_server_subscription_issue)) }
    }
}

/** Credential form. Required: a name, a proxy user and at least one authorized inbound. */
@Composable private fun CredentialEditor(draft: CredentialDraft, facts: ProxyServerSnapshot?, busy: Boolean,
    submit: (String?, String, String, List<String>, Boolean, Long?) -> Unit, dismiss: () -> Unit) {
    val credential = draft.credential
    var name by remember { mutableStateOf(credential?.name.orEmpty()) }
    var principalId by remember { mutableStateOf(credential?.principalId ?: facts?.principals?.firstOrNull()?.id.orEmpty()) }
    var grants by remember { mutableStateOf(credential?.inboundIds?.toSet().orEmpty()) }
    var enabled by remember { mutableStateOf(credential?.enabled ?: true) }
    var expiry by remember { mutableStateOf(credential?.expiresAtMillis?.let { IsoInstant.fromEpochMillis(it).take(10) }.orEmpty()) }
    var error by remember { mutableStateOf<String?>(null) }
    val inbounds = facts?.configuration?.inbounds.orEmpty()
    val expiryError = stringResource(R.string.proxy_server_expiry_invalid)
    AlertDialog(onDismissRequest = { if (!busy) dismiss() }, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.proxy_server_credential_add)) },
        text = {
            Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                ProxyServerTextField(name, !busy, R.string.proxy_server_credential_name) { name = it }
                Text(stringResource(R.string.proxy_server_credential_owner))
                facts?.principals.orEmpty().forEach { principal ->
                    RadioOption(principal.id == principalId, principal.displayName, !busy) { principalId = principal.id }
                }
                Text(stringResource(R.string.proxy_server_credential_grants))
                Text(stringResource(R.string.proxy_server_credential_grants_hint), style = MaterialTheme.typography.bodySmall)
                inbounds.forEach { inbound ->
                    CheckboxOption(grants.contains(inbound.id), inbound.name, !busy) { checked ->
                        grants = if (checked) grants + inbound.id else grants - inbound.id
                    }
                }
                ProxyServerTextField(expiry, !busy, R.string.proxy_server_credential_expiry) { expiry = it }
                Text(stringResource(R.string.proxy_server_credential_expiry_hint), style = MaterialTheme.typography.bodySmall)
                CheckboxOption(enabled, stringResource(R.string.proxy_server_credential_enabled), !busy) { enabled = it }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            Button(enabled = !busy, onClick = {
                val expires = expiry.trim().takeIf(String::isNotEmpty)?.let {
                    // A date field means midnight UTC of that day; the shared wire parser validates it.
                    runCatching { IsoInstant.requireEpochMillis(it + "T00:00:00Z") }.getOrNull()
                }
                if (expiry.isNotBlank() && expires == null) {
                    error = expiryError
                    return@Button
                }
                if (name.isBlank() || principalId.isBlank() || grants.isEmpty()) return@Button
                submit(credential?.id, principalId, name.trim(), inbounds.filter { grants.contains(it.id) }.map { it.id }, enabled, expires)
            }) { ActionLabel(R.string.common_save) }
        },
        dismissButton = { TextButton(enabled = !busy, onClick = dismiss) { ActionLabel(R.string.common_cancel) } })
}

/** Proxy user form. The linked account is a record of ownership, never an access grant. */
@Composable private fun PrincipalEditor(draft: PrincipalDraft, busy: Boolean,
    submit: (String?, String, String?) -> Unit, dismiss: () -> Unit) {
    val principal = draft.principal
    var name by remember { mutableStateOf(principal?.displayName.orEmpty()) }
    var userId by remember { mutableStateOf(principal?.userId.orEmpty()) }
    AlertDialog(onDismissRequest = { if (!busy) dismiss() }, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.proxy_server_principal_add)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                ProxyServerTextField(name, !busy, R.string.proxy_server_principal_name) { name = it }
                ProxyServerTextField(userId, !busy, R.string.proxy_server_principal_user) { userId = it }
            }
        },
        confirmButton = {
            Button(enabled = !busy && name.isNotBlank(), onClick = {
                submit(principal?.id, name.trim(), userId.trim().takeIf(String::isNotEmpty))
            }) { ActionLabel(R.string.common_save) }
        },
        dismissButton = { TextButton(enabled = !busy, onClick = dismiss) { ActionLabel(R.string.common_cancel) } })
}

@Composable private fun ProxyServerTextField(value: String, enabled: Boolean, label: Int, change: (String) -> Unit) {
    OutlinedTextField(value, change, enabled = enabled, singleLine = true, modifier = Modifier.fillMaxWidth(),
        label = { Text(stringResource(label)) })
}
