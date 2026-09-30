package app.relaxkonos.mobile.ui.manage.operations

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*

@Composable
internal fun installationServiceLabel(value: InstallationService): String = stringResource(when (value) {
    InstallationService.Smb -> R.string.installation_service_smb
    InstallationService.Nginx -> R.string.installation_service_nginx
    InstallationService.Frp -> R.string.installation_service_frp
    InstallationService.Mihomo -> R.string.installation_service_mihomo
    InstallationService.Docker -> R.string.installation_service_docker
    InstallationService.Git -> R.string.installation_service_git
})

@Composable
internal fun installationKindLabel(value: InstallationKind): String = stringResource(when (value) {
    InstallationKind.Install -> R.string.installation_kind_install
    InstallationKind.Upgrade -> R.string.installation_kind_upgrade
    InstallationKind.Repair -> R.string.installation_kind_repair
    InstallationKind.Uninstall -> R.string.installation_kind_uninstall
})

@Composable
internal fun installationStateLabel(value: InstallationState): String = stringResource(when (value) {
    InstallationState.Queued -> R.string.installation_state_queued
    InstallationState.Running -> R.string.installation_state_running
    InstallationState.Succeeded -> R.string.installation_state_succeeded
    InstallationState.Failed -> R.string.installation_state_failed
    InstallationState.Cancelled -> R.string.installation_state_cancelled
    InstallationState.Interrupted -> R.string.installation_state_interrupted
})

@Composable
internal fun installationStageLabel(value: InstallationStage): String = stringResource(when (value) {
    InstallationStage.Queued -> R.string.installation_stage_queued
    InstallationStage.Preflight -> R.string.installation_stage_preflight
    InstallationStage.Preparing -> R.string.installation_stage_preparing
    InstallationStage.UpdatingPackageLists -> R.string.installation_stage_updatingpackagelists
    InstallationStage.Downloading -> R.string.installation_stage_downloading
    InstallationStage.Copying -> R.string.installation_stage_copying
    InstallationStage.Verifying -> R.string.installation_stage_verifying
    InstallationStage.Extracting -> R.string.installation_stage_extracting
    InstallationStage.Installing -> R.string.installation_stage_installing
    InstallationStage.Configuring -> R.string.installation_stage_configuring
    InstallationStage.Activating -> R.string.installation_stage_activating
    InstallationStage.HealthChecking -> R.string.installation_stage_healthchecking
    InstallationStage.RollingBack -> R.string.installation_stage_rollingback
    InstallationStage.Completed -> R.string.installation_stage_completed
    InstallationStage.Failed -> R.string.installation_stage_failed
    InstallationStage.Cancelled -> R.string.installation_stage_cancelled
    InstallationStage.Interrupted -> R.string.installation_stage_interrupted
})

@Composable
internal fun installationProblemLabel(value: String): String = stringResource(when (value) {
    InstallationProblemCodes.STORE_UNAVAILABLE -> R.string.installation_problem_store_unavailable
    InstallationProblemCodes.INVALID_REQUEST -> R.string.installation_problem_invalid_request
    InstallationProblemCodes.CONFIRMATION_REQUIRED -> R.string.installation_problem_confirmation_required
    InstallationProblemCodes.IDEMPOTENCY_REQUIRED -> R.string.installation_problem_idempotency_required
    InstallationProblemCodes.IDEMPOTENCY_CONFLICT -> R.string.installation_problem_idempotency_conflict
    InstallationProblemCodes.RESOURCE_CONFLICT -> R.string.installation_problem_resource_conflict
    InstallationProblemCodes.NOT_CANCELLABLE -> R.string.installation_problem_not_cancellable
    InstallationProblemCodes.INTERRUPTED -> R.string.installation_problem_interrupted
    InstallationProblemCodes.CANCELLED -> R.string.installation_problem_cancelled
    InstallationProblemCodes.HELPER_PROTOCOL -> R.string.installation_problem_helper_protocol
    InstallationProblemCodes.NOT_SUPPORTED -> R.string.installation_problem_not_supported
    InstallationProblemCodes.FAILED -> R.string.installation_problem_failed
    InstallationProblemCodes.RECOVERY_UNKNOWN -> R.string.installation_problem_recovery_unknown
    InstallationProblemCodes.HEALTH_CHECK_FAILED -> R.string.installation_problem_health_check_failed
    InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE -> R.string.installation_problem_file_reference_unavailable
    InstallationProblemCodes.PERMISSION_DENIED -> R.string.installation_problem_permission_denied
    else -> R.string.installation_unknown_problem
})

@Composable
internal fun installationReferenceLabel(value: String): String {
    val service = InstallationService.entries.firstOrNull { it.name == value }
    return if (service != null) installationServiceLabel(service) else stringResource(R.string.operations_installation)
}
