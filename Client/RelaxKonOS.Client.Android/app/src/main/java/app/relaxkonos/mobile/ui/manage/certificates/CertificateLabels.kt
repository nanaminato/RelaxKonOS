package app.relaxkonos.mobile.ui.manage.certificates

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*

@Composable fun certificateStatusLabel(status: CertificateStatus) = stringResource(when (status) {
    CertificateStatus.Pending -> R.string.certificates_status_pending
    CertificateStatus.Validating -> R.string.certificates_status_validating
    CertificateStatus.Issued -> R.string.certificates_status_issued
    CertificateStatus.Active -> R.string.certificates_status_active
    CertificateStatus.Renewing -> R.string.certificates_status_renewing
    CertificateStatus.Failed -> R.string.certificates_status_failed
    CertificateStatus.Expired -> R.string.certificates_status_expired
    CertificateStatus.Revoked -> R.string.certificates_status_revoked
})
@Composable fun certificateActionLabel(action: CertificateAction) = stringResource(when (action) {
    CertificateAction.Issue -> R.string.certificates_issue
    CertificateAction.SelfSigned -> R.string.certificates_self_signed
    CertificateAction.Renew -> R.string.certificates_renew
    CertificateAction.Revoke -> R.string.certificates_revoke
    CertificateAction.Delete -> R.string.common_delete
    CertificateAction.DeployKestrel -> R.string.certificates_deploy
})
@Composable fun certificateChallengeLabel(challenge: CertificateChallenge) = stringResource(when (challenge) {
    CertificateChallenge.DirectHttp01 -> R.string.certificates_challenge_direct
    CertificateChallenge.WebRootHttp01 -> R.string.certificates_challenge_webroot
    CertificateChallenge.Dns01 -> R.string.certificates_challenge_dns
})
@Composable fun certificateKeyLabel(key: CertificateKey) = stringResource(if (key == CertificateKey.EcdsaP256) R.string.certificates_key_ecdsa else R.string.certificates_key_rsa)
@Composable fun certificateOperationStateLabel(state: CertificateOperationState) = stringResource(when (state) {
    CertificateOperationState.Queued -> R.string.certificates_state_queued
    CertificateOperationState.Running -> R.string.certificates_state_running
    CertificateOperationState.Succeeded -> R.string.certificates_state_succeeded
    CertificateOperationState.Failed -> R.string.certificates_state_failed
    CertificateOperationState.Cancelled -> R.string.certificates_state_cancelled
})
@Composable fun certificateStageLabel(stage: String) = stringResource(when (stage) {
    "queued" -> R.string.certificates_state_queued
    "running" -> R.string.certificates_state_running
    "succeeded" -> R.string.certificates_state_succeeded
    "failed" -> R.string.certificates_state_failed
    "cancelled" -> R.string.certificates_state_cancelled
    "interrupted" -> R.string.certificates_stage_interrupted
    "validation" -> R.string.certificates_stage_validation
    else -> R.string.certificates_stage_unknown
})
@Composable fun certificateProblemLabel(code: String) = stringResource(when (code) {
    "certificate.admin_required", "certificate.port80_elevation_required", "certificate.deployment_elevation_required" -> R.string.certificates_admin_required
    "certificate.domains_invalid", "certificate.request_invalid", "certificate.key_algorithm_invalid", "certificate.validity_days_invalid", "certificate.challenge_mode_invalid", "certificate.wildcard_requires_dns01" -> R.string.certificates_problem_input
    "certificate.terms_not_accepted" -> R.string.certificates_problem_terms
    "certificate.contact_invalid", "certificate.contact_unavailable" -> R.string.certificates_problem_contact
    "certificate.challenge_mode_not_available" -> R.string.certificates_dns_unavailable
    "certificate.direct_http01_unsupported" -> R.string.certificates_problem_input
    "certificate.original_request_pending" -> R.string.certificates_restore_form_note
    "certificate.dns_lookup_failed", "certificate.dns_no_records" -> R.string.certificates_problem_dns
    "certificate.port80_unavailable" -> R.string.certificates_problem_port
    "certificate.public_reachability_confirmation_required" -> R.string.certificates_problem_public
    "certificate.material_unavailable", "certificate.unsafe_path", "certificate.challenge_unsafe_path" -> R.string.certificates_problem_material
    "certificate.self_signed_not_renewable", "certificate.self_signed_not_revocable" -> R.string.certificates_problem_self_signed
    "certificate.revoked" -> R.string.certificates_problem_revoked
    "certificate.operation_not_found" -> R.string.certificates_operation_missing
    "certificate.not_found" -> R.string.certificates_problem_missing
    "certificate.operation_cancelled" -> R.string.certificates_state_cancelled
    "certificate.operation_interrupted" -> R.string.certificates_problem_interrupted
    "certificate.not_usable" -> R.string.certificates_binding_status
    "certificate.deployment_not_ready" -> R.string.certificates_deployment_not_ready
    "certificate.kestrel_https_not_configured", "certificate.kestrel_activation_failed" -> R.string.certificates_problem_kestrel
    "certificate.validation_failed", "certificate.validation_timeout", "certificate.http01_not_offered", "certificate.acme_directory_invalid", "certificate.acme_response_invalid", "certificate.acme_request_failed", "certificate.revocation_failed", "certificate.challenge_write_failed", "certificate.challenge_invalid" -> R.string.certificates_problem_acme
    else -> R.string.certificates_problem_unknown
})
