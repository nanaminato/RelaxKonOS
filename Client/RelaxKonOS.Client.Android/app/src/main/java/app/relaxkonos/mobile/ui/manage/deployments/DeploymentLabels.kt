package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.problemMessage

internal fun deploymentLabel(value: String): Int = when (value) {
    "image" -> R.string.deployment_image
    "javaJar" -> R.string.deployment_java
    "dotNetPublish" -> R.string.deployment_dotnet
    "pythonProject" -> R.string.deployment_python
    "web" -> R.string.deployment_web
    "worker" -> R.string.deployment_worker
    "process" -> R.string.deployment_process
    "http" -> R.string.deployment_http
    "unknown" -> R.string.deployment_unknown
    "missing" -> R.string.deployment_missing
    "stopped" -> R.string.deployment_stopped
    "starting" -> R.string.deployment_starting
    "running" -> R.string.deployment_running
    "stopping" -> R.string.deployment_stopping
    "failed" -> R.string.deployment_failed
    "queued" -> R.string.deployment_queued
    "succeeded" -> R.string.deployment_succeeded
    "cancelled" -> R.string.deployment_cancelled
    "interrupted" -> R.string.deployment_interrupted
    "deploy" -> R.string.deployment_deploy
    "rollback" -> R.string.deployment_rollback
    "start" -> R.string.deployment_start
    "stop" -> R.string.deployment_stop
    "restart" -> R.string.deployment_restart
    "delete" -> R.string.deployment_delete
    "preflight" -> R.string.deployment_preflight
    "preparing" -> R.string.deployment_preparing
    "pulling" -> R.string.deployment_pulling
    "building" -> R.string.deployment_building
    "creating" -> R.string.deployment_creating
    "healthChecking" -> R.string.deployment_healthchecking
    "activating" -> R.string.deployment_activating
    "deactivating" -> R.string.deployment_deactivating
    "cleaningUp" -> R.string.deployment_cleaningup
    "rollingBack" -> R.string.deployment_rollingback
    "completed" -> R.string.deployment_completed
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
    "application-catalog.already_current" -> UiMessage(R.string.catalog_update_current, tone = app.relaxkonos.mobile.ui.common.StatusTone.Info)
    "application-catalog.definition_incompatible" -> UiMessage(R.string.catalog_update_definition)
    "application-catalog.template_version_unavailable" -> UiMessage(R.string.catalog_update_unavailable)
    "application-deployment.revision_unknown" -> UiMessage(R.string.deployments_revision_unknown)
    "application-deployment.definition_conflict" -> UiMessage(R.string.deployments_definition_conflict)
    "application-deployment.resource_conflict" -> UiMessage(R.string.deployments_definition_busy)
    "application-deployment.secret_version_missing" -> UiMessage(R.string.deployments_secret_missing)
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
