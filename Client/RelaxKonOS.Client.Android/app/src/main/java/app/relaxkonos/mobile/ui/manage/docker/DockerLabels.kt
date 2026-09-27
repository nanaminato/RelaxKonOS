package app.relaxkonos.mobile.ui.manage.docker

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.problemMessage

/**
 * The sentence for a code from the Docker domain.
 *
 * Compose refusals are the whole point of the import subset: an operator has to be able to act on one.
 * Falling through to the shared mapping would show "the server refused the request" for
 * `docker.stack_idempotency_conflict` — true, but it leaves the operator retrying the same thing. Every
 * code this screen can provoke is named here; only a code this build has never heard of falls through.
 */
internal fun dockerProblem(code: String): UiMessage = when (code) {
    "docker.stack_invalid_name" -> UiMessage(R.string.docker_problem_invalid_name)
    "docker.stack_invalid_compose" -> UiMessage(R.string.docker_problem_invalid_compose)
    "docker.compose_feature_unsupported" -> UiMessage(R.string.docker_problem_feature_unsupported)
    "docker.compose_variable_unresolved" -> UiMessage(R.string.docker_problem_variable_unresolved)
    "docker.stack_definition_changed" -> UiMessage(R.string.docker_problem_definition_changed)
    "docker.confirmation_required" -> UiMessage(R.string.docker_problem_confirmation_required)
    "docker.stack_not_found", "docker.stack_operation_not_found" -> UiMessage(R.string.docker_not_found)
    "docker.stack_operation_conflict" -> UiMessage(R.string.docker_problem_operation_conflict)
    "docker.stack_idempotency_required" -> UiMessage(R.string.docker_problem_idempotency_required)
    "docker.stack_idempotency_conflict" -> UiMessage(R.string.docker_problem_idempotency_conflict)
    "docker.stack_not_cancellable" -> UiMessage(R.string.docker_problem_not_cancellable)
    "docker.stack_store_unavailable" -> UiMessage(R.string.docker_problem_store_unavailable)
    "docker.unavailable", "docker.operation_failed", "docker.operation_timeout", "docker.compose_failed" ->
        UiMessage(R.string.docker_problem_engine_unavailable)
    else -> problemMessage(code)
}

/**
 * The message for a failed Docker call, or `null` when it succeeded.
 *
 * A 403 is the account lacking the manage permission and a 404 is a resource this server no longer has;
 * both arrive without a namable code from the authorization middleware, so the HTTP verdict is what the
 * screen can honestly report. A transport failure stays a transport failure: no Docker refusal is ever
 * inferred from an unanswered request.
 */
internal fun ApiResult<*>.dockerFailure(): UiMessage? = when (this) {
    is ApiResult.Success -> null
    is ApiResult.Problem -> when (status) {
        403 -> UiMessage(R.string.docker_permission_denied)
        404 -> UiMessage(R.string.docker_not_found)
        else -> dockerProblem(code)
    }
    is ApiResult.Transport -> UiMessage(R.string.error_connectivity)
}
