package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import org.junit.Assert.*
import org.junit.Test

class DeploymentLabelsTest {
    @Test fun `camel case protocol values have labels and unknown enum stays unknown`() {
        for (value in listOf("", "Unknown", "future-state", "Running")) {
            assertEquals(R.string.deployment_unknown, deploymentLabel(value))
        }
        assertEquals(R.string.deployment_image, deploymentLabel("image"))
        assertEquals(R.string.deployment_web, deploymentLabel("web"))
        assertEquals(R.string.deployment_running, deploymentLabel("running"))
        assertEquals(R.string.deployment_http, deploymentLabel("http"))
        assertEquals(R.string.deployment_interrupted, deploymentLabel("interrupted"))
    }

    @Test fun `engine installation permissions and reachability have distinct explanations`() {
        val messages = listOf("docker.not_installed", "docker.permission_denied", "docker.unavailable", "docker.api_incompatible")
            .map { deploymentProblem(it).resId }
        assertEquals(4, messages.toSet().size)
    }

    @Test fun `denial missing record and disconnection do not collapse into one message`() {
        assertEquals(R.string.deployments_permission, ApiResult.Problem(403, "", null).deploymentFailure().resId)
        assertEquals(R.string.deployments_not_found, ApiResult.Problem(404, "", null).deploymentFailure().resId)
        assertEquals(R.string.deployments_unverified, ApiResult.Transport("secret body").deploymentFailure().resId)
        assertNull(ApiResult.Transport("secret body").deploymentFailure().debugDetail)
        assertEquals(R.string.deployments_runtime_missing, ApiResult.Problem(404, "", null).runtimeFailure().resId)
        assertEquals(R.string.deployments_engine_permission, ApiResult.Problem(403, "", null).runtimeFailure().resId)
    }
}
