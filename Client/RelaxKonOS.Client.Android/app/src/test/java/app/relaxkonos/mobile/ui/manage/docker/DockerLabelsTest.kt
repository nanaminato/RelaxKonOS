package app.relaxkonos.mobile.ui.manage.docker

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DockerLabelsTest {
    /**
     * The Compose refusals are the import subset's whole point: each one has to reach the operator as
     * something to act on, so none of them may fall through to the generic "the server refused it".
     */
    @Test fun `every compose refusal has its own explanation`() {
        val refusals = listOf(
            "docker.stack_invalid_name",
            "docker.stack_invalid_compose",
            "docker.compose_feature_unsupported",
            "docker.compose_variable_unresolved",
            "docker.stack_definition_changed",
            "docker.confirmation_required",
            "docker.stack_not_found",
            "docker.stack_operation_not_found",
            "docker.stack_operation_conflict",
            "docker.stack_idempotency_required",
            "docker.stack_idempotency_conflict",
            "docker.stack_not_cancellable",
            "docker.stack_store_unavailable",
        )
        val messages = refusals.map { dockerProblem(it).resId }
        assertTrue("a refusal fell through to the generic sentence", messages.none { it == R.string.error_generic })
        // Not all distinct: the two "not found" codes intentionally share one sentence. What must not
        // happen is a refusal being indistinguishable from an unrelated one.
        assertEquals(12, messages.toSet().size)
    }

    @Test fun `a code this build does not know still reads as a refusal`() {
        assertEquals(R.string.error_generic, dockerProblem("docker.from-a-newer-server").resId)
    }

    @Test fun `denial missing resource and disconnection stay three different answers`() {
        assertEquals(R.string.docker_permission_denied, ApiResult.Problem(403, "", null).dockerFailure()!!.resId)
        assertEquals(R.string.docker_not_found, ApiResult.Problem(404, "", null).dockerFailure()!!.resId)
        assertEquals(R.string.error_connectivity, ApiResult.Transport("secret body").dockerFailure()!!.resId)
        // A transport failure must never be reported as a Docker refusal, and must not echo the body.
        assertNull(ApiResult.Transport("secret body").dockerFailure()!!.debugDetail)
    }

    @Test fun `a refused code with a 4xx status is read as that code, not as the status`() {
        assertEquals(R.string.docker_problem_feature_unsupported,
            ApiResult.Problem(400, "docker.compose_feature_unsupported", null).dockerFailure()!!.resId)
        assertEquals(R.string.docker_problem_store_unavailable,
            ApiResult.Problem(503, "docker.stack_store_unavailable", null).dockerFailure()!!.resId)
    }

    @Test fun `success has no message`() {
        assertNull(ApiResult.Success(Unit).dockerFailure())
    }
}
