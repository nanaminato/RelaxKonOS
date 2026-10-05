package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.deploymentFixture
import app.relaxkonos.mobile.data.matchesReceipt
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class DeploymentDefinitionDraftTest {
    private val baseline = deploymentFixture()
    @Test fun `editing a name preserves every untouched field exactly including byte ceiling paths and secret reference`() {
        val draft = DeploymentDefinitionDraft(baseline)
        draft.name = "renamed"
        val request = draft.requestOrNull()!!
        assertEquals(baseline.updatedAt, request.expectedUpdatedAt)
        assertEquals(baseline.limits, request.limits)
        assertEquals(baseline.volumes, request.volumes)
        assertEquals(baseline.configuration, request.configuration)
        assertEquals(baseline.healthCheckPath, request.healthCheckPath)
        assertEquals(baseline.siteId, request.siteId)
        assertEquals(baseline.hostPort, request.hostPort)
        assertEquals("renamed", request.name)
    }
    @Test fun `ordinary values stay exact and saved secret values remain editable`() {
        val draft = DeploymentDefinitionDraft(baseline)
        assertTrue(draft.putConfiguration("EMPTY", "", false))
        assertTrue(draft.putConfiguration("TOKEN", "", true))
        assertEquals(7, draft.configuration.first { it.name == "TOKEN" }.secretVersion)
        assertNull(draft.configuration.first { it.name == "TOKEN" }.value)
        assertTrue(draft.putConfiguration("TOKEN", "new-secret", true))
        assertFalse(draft.requestOrNull()!!.toString().contains("new-secret"))
        assertEquals("new-secret", draft.requestOrNull()!!.configuration.first { it.name == "TOKEN" }.value)
        assertFalse(draft.putConfiguration("NEW", "", true))
        assertTrue(draft.putConfiguration("NEW", "new-secret", true))
        assertEquals("new-secret", draft.requestOrNull()!!.configuration.first { it.name == "NEW" }.value)
    }
    @Test fun `removing volumes and config never changes source or implicitly deploys`() {
        val draft = DeploymentDefinitionDraft(baseline)
        draft.volumes.clear(); draft.configuration.clear(); draft.siteId = ""
        val request = draft.requestOrNull()!!
        assertTrue(request.volumes.isEmpty()); assertTrue(request.configuration.isEmpty()); assertNull(request.siteId)
        assertTrue(request.matchesReceipt(baseline.copy(volumes = emptyList(), configuration = emptyList(), siteId = null, updatedAt = "2026-10-01T00:00:01+00:00"), baseline))
    }
    @Test fun `resource overflows nonfinite values invalid controls paths and HTTP worker combinations are refused`() {
        val edits: List<(DeploymentDefinitionDraft) -> Unit> = listOf(
            { it.cpuCores = "NaN" }, { it.cpuCores = "Infinity" }, { it.memoryBytes = Long.MAX_VALUE.toString() },
            { it.pidsLimit = "0" }, { it.hostPort = "" }, { it.bindAddress = "192.0.2.1" }, { it.healthPath = "/a\nb" },
            { it.workload = "worker" }, { it.siteId = "../site" }, { it.volumes += DeploymentVolume("escape", "/app/../etc", false) },
        )
        edits.forEach { edit -> val draft = DeploymentDefinitionDraft(baseline); edit(draft); assertNull(draft.requestOrNull()) }
        val draft = DeploymentDefinitionDraft(baseline)
        assertFalse(draft.putConfiguration("KEY", "line\nbreak", false))
        assertFalse(draft.putVolume("data", "/app/../etc", false))
        assertTrue(draft.putVolume("data", "/app/data:live", false))
        assertFalse(draft.volumes.single().readOnly)
    }
    @Test fun `process readiness clears HTTP path while retaining precise limits and valid site association`() {
        val draft = DeploymentDefinitionDraft(baseline)
        draft.workload = "worker"; draft.readiness = "process"
        assertNull(draft.requestOrNull()!!.healthCheckPath)
        draft.hostPort = ""; draft.siteId = ""
        assertNotNull(draft.requestOrNull())
    }
}
