package app.relaxkonos.mobile.ui.manage.certificates

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.PendingCertificateRequest
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date
import kotlinx.coroutines.delay

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun CertificatesScreen(onBack: () -> Unit, initialOperationId: String? = null, serverHttpsOnly: Boolean = false, modifier: Modifier = Modifier) {
    val model: CertificatesViewModel = viewModel()
    val state = model.state
    val epoch = model.sessionEpoch
    val owner = appContainer().activeSession
    var section by rememberSaveable(owner, epoch) { mutableStateOf(if (initialOperationId != null) "operations" else if (serverHttpsOnly) "certificates" else "overview") }
    OperationMessageDialog(if (state.busy) null else state.problemCode?.let { certificateProblemLabel(it) } ?: if (state.uncertain) stringResource(R.string.certificates_uncertain) else null, tone = if (state.problemCode == null) StatusTone.Warning else StatusTone.Danger)
    LaunchedEffect(initialOperationId) { if (initialOperationId != null) section = "operations" }
    val available = owner?.capabilities?.contains(ServerCapabilities.CERTIFICATES) == true
    val canManage = available && owner?.privilegedOperations == true
    var confirm by remember(owner, epoch) { mutableStateOf<Pair<Int, () -> Unit>?>(null) }
    var recovery by remember(owner, epoch) { mutableStateOf(false) }
    var recoverPending by remember(owner, epoch) { mutableStateOf<PendingCertificateRequest?>(null) }
    var recoverId by remember(owner, epoch) { mutableStateOf("") }
    LaunchedEffect(owner, epoch, initialOperationId) { if (available) { if (initialOperationId != null) model.recover(initialOperationId) else model.refresh() } }
    LaunchedEffect(owner, epoch, state.operation?.operationId, state.operation?.state, state.busy) {
        if (state.operation?.state?.active == true && !state.busy) { delay(1500); model.poll() }
    }
    BackHandler(section == "certificates" && state.selectedId != null && state.draft == null && !state.busy) { model.select(null) }
    WorkspaceColumn(stringResource(if (serverHttpsOnly) R.string.server_https_title else R.string.certificates_title), onBack, listOf(*(if (serverHttpsOnly) emptyArray() else arrayOf(WorkspaceDestination("overview", R.string.workspace_overview))), WorkspaceDestination("certificates", R.string.workspace_certificates), WorkspaceDestination("operations", R.string.workspace_operations)), section, { section = it }, modifier, stateKey = owner to epoch) {
        if (!available) { Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn }
        Text(stringResource(if (serverHttpsOnly) R.string.server_https_description else R.string.certificates_https_location), style = MaterialTheme.typography.bodySmall)
        PageActionRow(refresh = {
            TextButton(onClick = model::refresh, enabled = !state.busy) { ActionLabel(R.string.common_refresh) }
        }, actions = {
            if (canManage && !serverHttpsOnly && section != "operations") {
                OutlinedButton(onClick = { model.create(false) }, enabled = !state.busy) { Text(stringResource(R.string.certificates_issue)) }
                OutlinedButton(onClick = { model.create(true) }, enabled = !state.busy) { Text(stringResource(R.string.certificates_self_signed)) }
            }
            if (section == "operations") TextButton(onClick = { recoverPending = null; recoverId = ""; recovery = true }, enabled = !state.busy) { Text(stringResource(R.string.certificates_recover)) }
        })
        RefreshProgressIndicator(visible = state.busy)

        if (section != "operations" && (state.pending.isNotEmpty() || state.operation != null)) {
            TextButton(onClick = { section = "operations" }) {
                Text(stringResource(R.string.workspace_operations) + " · " + (state.operation?.operationId ?: state.pending.size.toString()))
            }
        }
        WorkspaceSection(section == "overview") {
            val certificates = (state.list as? ApiResult.Success)?.value
            SectionCard(stringResource(R.string.workspace_certificates), subtitle = certificates?.size?.toString()) {
                Text(stringResource(R.string.certificates_intro))
            }
        }
        WorkspaceSection(section == "certificates") {
        BoxWithConstraints(Modifier.fillMaxWidth()) {
            if (maxWidth >= 600.dp) Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                Column(Modifier.weight(1f)) { CertificateList(state, model) }
                Column(Modifier.weight(2f)) { CertificateDetail(state, model, canManage, serverHttpsOnly) { message, action -> confirm = message to action } }
            } else Column {
                if (state.selectedId == null) CertificateList(state, model) else {
                    TextButton(onClick = { model.select(null) }, enabled = !state.busy) { Text(stringResource(R.string.common_back)) }
                    CertificateDetail(state, model, canManage, serverHttpsOnly) { message, action -> confirm = message to action }
                }
            }
        }
        }
        WorkspaceSection(section == "operations") {
        if (state.operation == null && state.pending.isEmpty()) ManagementCard { Text(stringResource(R.string.workspace_records_empty)) }
        state.operation?.let { operation ->
            ManagementCard {
            Text(certificateActionLabel(operation.kind), style = MaterialTheme.typography.titleSmall)
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            operation.certificateId?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            if (state.operationVerified) { ExecutionStatusChip(certificateOperationStateLabel(operation.state), operation.state.wire); Text(certificateStageLabel(operation.stage)) }
            else Text(stringResource(R.string.certificates_unknown), color = MaterialTheme.colorScheme.error)
            operation.problemCode.takeIf(String::isNotBlank)?.let { Text(certificateProblemLabel(it), color = MaterialTheme.colorScheme.error) }
            PageActionRow(refresh = {
                TextButton(onClick = model::poll, enabled = !state.busy) { ActionLabel(R.string.common_refresh) }
            })
            if (state.operationVerified && operation.state.active) TextButton(enabled = !state.busy, onClick = { confirm = R.string.certificates_cancel_confirm to model::cancel }) { Text(stringResource(R.string.common_cancel)) }
            Text(stringResource(R.string.certificates_cancel_note), style = MaterialTheme.typography.bodySmall)
            if (operation.kind == CertificateAction.DeployKestrel) operation.certificateId?.let { id ->
                KestrelFacts(state.deployments[id], (state.list as? ApiResult.Success)?.value?.firstOrNull { it.id == id })
                TextButton(enabled = !state.busy, onClick = { model.inspectDeployment(id) }) { Text(stringResource(R.string.certificates_deployment_refresh)) }
            }
            }
        }
        state.pending.forEach { pending ->
            ManagementCard {
            Text(stringResource(R.string.certificates_pending, certificateActionLabel(pending.action), pending.target ?: stringResource(R.string.certificates_new_target)), style = MaterialTheme.typography.bodySmall)
            if (pending.target != null && canManage) TextButton(enabled = !state.busy, onClick = {
                confirm = R.string.certificates_retry_confirm to { model.retry(pending) }
            }) { ActionLabel(R.string.common_retry) }
            TextButton(enabled = !state.busy, onClick = { recoverPending = pending; recoverId = pending.operationId.orEmpty(); recovery = true }) { Text(stringResource(R.string.certificates_recover)) }
            }
        }
        }
    }
    if (state.draft != null) key(owner, epoch) { CertificateEditor(state, model) }
    confirm?.let { request -> AlertDialog(onDismissRequest = { confirm = null }, title = { Text(stringResource(R.string.certificates_confirm)) },
        text = { Text(stringResource(request.first)) }, confirmButton = { Button(onClick = { confirm = null; request.second() }) { Text(stringResource(R.string.certificates_confirm)) } },
        dismissButton = { TextButton(onClick = { confirm = null }) { Text(stringResource(R.string.common_cancel)) } }) }
    if (recovery) AlertDialog(onDismissRequest = { recovery = false }, title = { Text(stringResource(R.string.certificates_recover)) },
        text = { Column { Text(stringResource(R.string.certificates_recover_note)); OutlinedTextField(recoverId, { recoverId = it }, singleLine = true, label = { Text(stringResource(R.string.certificates_operation_id)) }) } },
        confirmButton = { Button(enabled = runCatching { InstallationRoutes.operation(recoverId.trim()) }.isSuccess, onClick = {
            model.recover(recoverId, recoverPending); recovery = false
        }) { Text(stringResource(R.string.certificates_recover)) } }, dismissButton = { TextButton(onClick = { recovery = false }) { Text(stringResource(R.string.common_close)) } })
}

@Composable
private fun CertificateList(state: CertificatesState, model: CertificatesViewModel) {
    when (val result = state.list) {
        is ApiResult.Success -> {
            if (result.value.isEmpty()) Text(stringResource(R.string.certificates_empty))
            result.value.forEach { certificate ->
                OutlinedCard(enabled = !state.busy, onClick = { model.select(certificate.id) }, modifier = Modifier.fillMaxWidth()) { Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(certificate.primaryDomain, style = MaterialTheme.typography.titleSmall)
                    Text(certificateStatusLabel(certificate.status))
                    certificate.notAfterMillis?.let { Text(stringResource(R.string.certificates_expires, date(it)), style = MaterialTheme.typography.bodySmall) }
                } }
            }
        }
        else -> Text(stringResource(R.string.certificates_unavailable), color = MaterialTheme.colorScheme.error)
    }
}
@Composable
private fun CertificateDetail(state: CertificatesState, model: CertificatesViewModel, canManage: Boolean, serverHttpsOnly: Boolean, confirm: (Int, () -> Unit) -> Unit) {
    if (state.selectedId == null) { Text(stringResource(R.string.certificates_select)); return }
    val certificate = (state.detail as? ApiResult.Success)?.value
    if (certificate == null) {
        Text(stringResource(R.string.certificates_detail_unavailable))
        KestrelFacts(state.deployments[state.selectedId], null)
        TextButton(enabled = !state.busy, onClick = { model.inspectDeployment(requireNotNull(state.selectedId)) }) { Text(stringResource(R.string.certificates_deployment_refresh)) }
        return
    }
    Text(certificate.primaryDomain, style = MaterialTheme.typography.titleMedium)
    Text(certificate.id, style = MaterialTheme.typography.bodySmall)
    Text(certificate.subjectAlternativeNames.joinToString(", "))
    Text(certificateStatusLabel(certificate.status))
    Text(stringResource(if (certificate.kind == CertificateKind.SelfSigned) R.string.certificates_self_signed_note else R.string.certificates_acme))
    Text(certificateChallengeLabel(certificate.challengeType))
    Text(certificateKeyLabel(certificate.keyAlgorithm))
    certificate.issuer?.let { Text(stringResource(R.string.certificates_issuer, it)) }
    certificate.serialNumber?.let { Text(stringResource(R.string.certificates_serial, it)) }
    certificate.fingerprintSha256?.let { Text(stringResource(R.string.certificates_fingerprint, it), style = MaterialTheme.typography.bodySmall) }
    certificate.thumbprint?.let { Text(stringResource(R.string.certificates_thumbprint, it), style = MaterialTheme.typography.bodySmall) }
    certificate.notBeforeMillis?.let { Text(stringResource(R.string.certificates_not_before, date(it))) }
    certificate.notAfterMillis?.let { expiry ->
        Text(stringResource(R.string.certificates_expires, date(expiry)))
        if (expiry <= System.currentTimeMillis()) Text(stringResource(R.string.certificates_expired_warning), color = MaterialTheme.colorScheme.error)
        else if (expiry - System.currentTimeMillis() <= 30L * 86400000) Text(stringResource(R.string.certificates_expiring_warning), color = MaterialTheme.colorScheme.error)
    }
    certificate.renewalWindowStartMillis?.let { Text(stringResource(R.string.certificates_renewal_start, date(it))) }
    certificate.renewalWindowEndMillis?.let { Text(stringResource(R.string.certificates_renewal_end, date(it))) }
    certificate.lastRenewalAtMillis?.let { Text(stringResource(R.string.certificates_last_renewal, date(it))) }
    certificate.lastRenewalProblemCode?.takeIf(String::isNotBlank)?.let { Text(certificateProblemLabel(it), color = MaterialTheme.colorScheme.error) }
    RenewalHistory(certificate)
    Text(stringResource(R.string.certificates_created, date(certificate.createdAtMillis)), style = MaterialTheme.typography.bodySmall)
    Text(stringResource(R.string.certificates_updated, date(certificate.updatedAtMillis)), style = MaterialTheme.typography.bodySmall)
    if (serverHttpsOnly) {
        KestrelFacts(state.deployments[certificate.id], certificate)
        TextButton(enabled = !state.busy, onClick = { model.inspectDeployment(certificate.id) }) { Text(stringResource(R.string.certificates_deployment_refresh)) }
    }
    certificate.usageProblem()?.let { Text(certificateUsageLabel(it), color = MaterialTheme.colorScheme.error) }
    if (canManage) {
        val enabled = !state.busy && state.pending.none { it.target == certificate.id } && !(state.operation?.certificateId == certificate.id && state.operation.state.active)
        if (serverHttpsOnly) OutlinedButton(enabled = enabled && certificate.usageProblem() == null && (state.deployments[certificate.id] as? ApiResult.Success)?.value?.httpsConfigured == true,
            onClick = { confirm(R.string.certificates_deploy_confirm) { model.action(certificate, CertificateAction.DeployKestrel) } }) { Text(stringResource(R.string.certificates_deploy)) }
        if (!serverHttpsOnly && certificate.kind == CertificateKind.Acme && certificate.status != CertificateStatus.Revoked) {
            OutlinedButton(enabled = enabled, onClick = { confirm(R.string.certificates_renew_confirm) { model.action(certificate, CertificateAction.Renew) } }) { Text(stringResource(R.string.certificates_renew)) }
            OutlinedButton(enabled = enabled, onClick = { confirm(R.string.certificates_revoke_confirm) { model.action(certificate, CertificateAction.Revoke) } }) { Text(stringResource(R.string.certificates_revoke)) }
        }
        if (!serverHttpsOnly) TextButton(enabled = enabled, onClick = { confirm(R.string.certificates_delete_confirm) { model.action(certificate, CertificateAction.Delete) } }) { ActionLabel(R.string.common_delete) }
    }
}
@Composable
private fun RenewalHistory(certificate: ManagedCertificate) {
    var expanded by rememberSaveable(certificate.id) { mutableStateOf(false) }
    TextButton(onClick = { expanded = !expanded }) { Text(stringResource(R.string.certificates_renewal_title)) }
    if (!expanded) return
    if (certificate.kind == CertificateKind.SelfSigned) { Text(stringResource(R.string.certificates_renewal_self_signed)); return }
    val renewal = certificate.renewal
    if (renewal == null) { Text(stringResource(R.string.certificates_renewal_unavailable)); return }
    Text(stringResource(if (renewal.automaticEnabled) R.string.certificates_renewal_enabled else R.string.certificates_renewal_disabled))
    if (renewal.retryExhausted) Text(stringResource(R.string.certificates_renewal_exhausted, renewal.consecutiveFailures.toString()), color = MaterialTheme.colorScheme.error)
    else renewal.retryAfterMillis?.let { Text(stringResource(R.string.certificates_renewal_retry, date(it))) }
    if (renewal.attempts.isEmpty()) Text(stringResource(R.string.certificates_renewal_empty))
    renewal.attempts.forEach { attempt ->
        val operation = attempt.operation
        HorizontalDivider()
        Text(stringResource(R.string.certificates_renewal_record,
            stringResource(if (attempt.automatic) R.string.certificates_renewal_automatic else R.string.certificates_renewal_manual),
            certificateOperationStateLabel(operation.state), operation.startedAtMillis?.let(::date) ?: "—", operation.completedAtMillis?.let(::date) ?: "—"))
        if (operation.problemCode.isNotBlank()) Text(certificateProblemLabel(operation.problemCode), color = MaterialTheme.colorScheme.error)
        Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
    }
}
@Composable
private fun KestrelFacts(result: ApiResult<KestrelCertificateDeployment>?, certificate: ManagedCertificate?) {
    Text(stringResource(R.string.certificates_deployment_title), style = MaterialTheme.typography.titleSmall)
    val facts = (result as? ApiResult.Success)?.value
    if (facts == null) { Text(stringResource(R.string.certificates_binding_unverified)); return }
    Text(stringResource(if (facts.httpsConfigured) R.string.certificates_deployment_https else R.string.certificates_problem_kestrel))
    Text(stringResource(if (facts.registered) R.string.certificates_deployment_registered else R.string.certificates_deployment_absent))
    if (!facts.certificateExists) Text(stringResource(R.string.certificates_problem_missing), color = MaterialTheme.colorScheme.error)
    if (facts.registered) {
        Text(stringResource(if (facts.isDefault) R.string.certificates_deployment_default else R.string.certificates_deployment_sni))
        Text(stringResource(R.string.certificates_deployment_hosts, facts.hostNames.joinToString(", ").ifBlank { "—" }))
        facts.fingerprintSha256?.let { Text(stringResource(R.string.certificates_fingerprint, it), style = MaterialTheme.typography.bodySmall) }
        facts.notBeforeMillis?.let { Text(stringResource(R.string.certificates_not_before, date(it))) }
        facts.notAfterMillis?.let { Text(stringResource(R.string.certificates_expires, date(it))) }
        if (certificate != null) Text(stringResource(if (facts.matches(certificate)) R.string.certificates_deployment_match else R.string.certificates_deployment_mismatch))
    }
    Text(stringResource(R.string.certificates_checked, date(facts.observedAtMillis)), style = MaterialTheme.typography.bodySmall)
    Text(stringResource(R.string.certificates_deployment_evidence), style = MaterialTheme.typography.bodySmall)
}
@Composable
private fun CertificateEditor(state: CertificatesState, model: CertificatesViewModel) {
    val draft = state.draft ?: return
    val locked = state.busy || state.draftLocked
    var discard by remember { mutableStateOf(false) }
    var submit by remember { mutableStateOf(false) }
    fun close() { if (!state.busy) { if (draft != state.initialDraft) discard = true else model.closeDraft() } }
    AlertDialog(onDismissRequest = ::close, modifier = Modifier.imePadding(), title = { Text(stringResource(if (draft.selfSigned) R.string.certificates_self_signed else R.string.certificates_issue)) },
        text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(if (draft.selfSigned) R.string.certificates_self_signed_note else R.string.certificates_challenge_note))
            if (state.pending.any { it.target == null }) Text(stringResource(R.string.certificates_restore_form_note))
            OutlinedTextField(draft.domainsText, { model.update(draft.copy(domainsText = it)) }, enabled = !locked, label = { Text(stringResource(R.string.certificates_domains)) }, modifier = Modifier.fillMaxWidth())
            CertificateKey.entries.forEach { key -> Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { RadioButton(draft.key == key, { model.update(draft.copy(key = key)) }, enabled = !locked); Text(certificateKeyLabel(key)) } }
            if (draft.selfSigned) OutlinedTextField(draft.validityDays, { model.update(draft.copy(validityDays = it)) }, enabled = !locked, singleLine = true, label = { Text(stringResource(R.string.certificates_validity_days)) })
            else {
                CertificateChallenge.entries.forEach { challenge -> Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    RadioButton(draft.challenge == challenge, { model.update(draft.copy(challenge = challenge)) }, enabled = !locked)
                    Text(certificateChallengeLabel(challenge))
                } }
                if (draft.challenge == CertificateChallenge.Dns01) Text(stringResource(R.string.certificates_dns_unavailable), color = MaterialTheme.colorScheme.error)
                OutlinedTextField(draft.email, { model.update(draft.copy(email = it)) }, enabled = !locked, singleLine = true, label = { Text(stringResource(R.string.certificates_email)) }, modifier = Modifier.fillMaxWidth())
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(draft.acceptedTerms, { model.update(draft.copy(acceptedTerms = it)) }, enabled = !locked); Text(stringResource(R.string.certificates_terms)) }
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(draft.reachable, { model.update(draft.copy(reachable = it)) }, enabled = !locked); Text(stringResource(R.string.certificates_public_confirm)) }
                OutlinedButton(enabled = !state.busy && draft.domains() != null, onClick = model::preflight) { Text(stringResource(R.string.certificates_preflight)) }
                val preflight = (state.preflight as? ApiResult.Success)?.value
                preflight?.let { facts ->
                    Text(stringResource(if (facts.canProceed) R.string.certificates_preflight_ready else R.string.certificates_preflight_blocked))
                    Text(stringResource(R.string.certificates_port80, stringResource(when (facts.port80Available) { true -> R.string.certificates_available; false -> R.string.certificates_unavailable_short; null -> R.string.certificates_unknown })))
                    if (facts.requiresAdministrator) Text(stringResource(R.string.certificates_admin_required))
                    if (facts.requiresPublicReachabilityConfirmation) Text(stringResource(R.string.certificates_reachability_note))
                    facts.problemCode.takeIf(String::isNotBlank)?.let { Text(certificateProblemLabel(it), color = MaterialTheme.colorScheme.error) }
                    facts.domains.forEach { domain ->
                        Text(domain.domain, style = MaterialTheme.typography.titleSmall)
                        Text(stringResource(R.string.certificates_dns_addresses, (domain.ipv4 + domain.ipv6).joinToString(", ")))
                        domain.problemCode.takeIf(String::isNotBlank)?.let { Text(certificateProblemLabel(it), color = MaterialTheme.colorScheme.error) }
                    }
                }
                state.preflightAtMillis?.let { Text(stringResource(R.string.certificates_checked, date(it)), style = MaterialTheme.typography.bodySmall) }
            }
            if (draft.body() == null) Text(stringResource(R.string.certificates_validation), color = MaterialTheme.colorScheme.error)

            RefreshProgressIndicator(visible = state.busy)
        } }, confirmButton = { Button(enabled = !state.busy && draft.body() != null && (draft.selfSigned || (state.preflight as? ApiResult.Success)?.value?.canProceed == true), onClick = { submit = true }) {
            Text(stringResource(if (state.draftLocked) R.string.common_retry else R.string.certificates_submit))
        } }, dismissButton = { TextButton(onClick = ::close, enabled = !state.busy) { Text(stringResource(R.string.common_close)) } })
    if (discard || submit) AlertDialog(onDismissRequest = { discard = false; submit = false }, title = { Text(stringResource(R.string.certificates_confirm)) },
        text = { Text(stringResource(if (submit) R.string.certificates_submit_confirm else R.string.certificates_discard_confirm)) },
        confirmButton = { Button(onClick = { if (submit) model.submitDraft() else model.closeDraft(); submit = false; discard = false }) { Text(stringResource(R.string.certificates_confirm)) } },
        dismissButton = { TextButton(onClick = { discard = false; submit = false }) { Text(stringResource(R.string.common_cancel)) } })
}
private fun date(value: Long): String = DateFormat.getDateTimeInstance().format(Date(value))
