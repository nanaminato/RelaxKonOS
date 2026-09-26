package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.problemMessage

internal fun deploymentLabel(value: String): Int = when (value) {
    "Image" -> R.string.deployment_image
    "JavaJar" -> R.string.deployment_java
    "DotNetPublish" -> R.string.deployment_dotnet
    "PythonProject" -> R.string.deployment_python
    "Web" -> R.string.deployment_web
    "Worker" -> R.string.deployment_worker
    "Process" -> R.string.deployment_process
    "Http" -> R.string.deployment_http
    "Unknown" -> R.string.deployment_unknown
    "Missing" -> R.string.deployment_missing
    "Stopped" -> R.string.deployment_stopped
    "Starting" -> R.string.deployment_starting
    "Running" -> R.string.deployment_running
    "Stopping" -> R.string.deployment_stopping
    "Failed" -> R.string.deployment_failed
    "Queued" -> R.string.deployment_queued
    "Succeeded" -> R.string.deployment_succeeded
    "Cancelled" -> R.string.deployment_cancelled
    "Interrupted" -> R.string.deployment_interrupted
    "Deploy" -> R.string.deployment_deploy
    "Rollback" -> R.string.deployment_rollback
    "Start" -> R.string.deployment_start
    "Stop" -> R.string.deployment_stop
    "Restart" -> R.string.deployment_restart
    "Delete" -> R.string.deployment_delete
    "Preflight" -> R.string.deployment_preflight
    "Preparing" -> R.string.deployment_preparing
    "Pulling" -> R.string.deployment_pulling
    "Building" -> R.string.deployment_building
    "Creating" -> R.string.deployment_creating
    "HealthChecking" -> R.string.deployment_healthchecking
    "Activating" -> R.string.deployment_activating
    "Deactivating" -> R.string.deployment_deactivating
    "CleaningUp" -> R.string.deployment_cleaningup
    "RollingBack" -> R.string.deployment_rollingback
    "Completed" -> R.string.deployment_completed
    else -> R.string.deployment_unknown
}

internal fun deploymentProblem(code: String): UiMessage = when (code) {
    "docker.not_installed", "application-deployment.engine_not_installed" -> UiMessage(R.string.deployments_engine_missing)
    "docker.permission_denied" -> UiMessage(R.string.deployments_engine_permission)
    "docker.api_incompatible" -> UiMessage(R.string.deployments_engine_incompatible)
    "docker.unavailable", "docker.operation_failed", "docker.operation_timeout",
    "application-deployment.engine_unavailable" -> UiMessage(R.string.deployments_engine_unavailable)
    "application-deployment.permission_denied" -> UiMessage(R.string.deployments_permission)
    "application-deployment.application_not_found" -> UiMessage(R.string.deployments_not_found)
    "application-deployment.store_unavailable" -> UiMessage(R.string.deployments_store_unavailable)
    "application-deployment.platform_unsupported" -> UiMessage(R.string.deployments_platform_unsupported)
    else -> problemMessage(code)
}

internal fun ApiResult<*>.deploymentFailure(): UiMessage = when (this) {
    is ApiResult.Problem -> when (status) {
        403 -> UiMessage(R.string.deployments_permission)
        404 -> UiMessage(R.string.deployments_not_found)
        401 -> UiMessage(R.string.error_unauthorized)
        else -> deploymentProblem(code)
    }
    else -> UiMessage(R.string.deployments_unverified)
}

internal fun ApiResult<*>.runtimeFailure(): UiMessage = when {
    this is ApiResult.Problem && status == 403 -> UiMessage(R.string.deployments_engine_permission)
    this is ApiResult.Problem && status == 404 -> UiMessage(R.string.deployments_runtime_missing)
    else -> deploymentFailure()
}
