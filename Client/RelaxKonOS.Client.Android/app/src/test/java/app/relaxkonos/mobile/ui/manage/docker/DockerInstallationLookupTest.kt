package app.relaxkonos.mobile.ui.manage.docker

import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DockerControlFacts
import app.relaxkonos.mobile.core.net.DockerStatus
import org.junit.Assert.*
import org.junit.Test

class DockerInstallationLookupTest {
    @Test fun `missing receipt does not become a current page error or resolve pending submission`() {
        val facts = DockerControlFacts(DockerStatus(true, "", "29.8.2", "linux", "amd64"), emptyList())
        val before = DockerControlState(facts = facts, pendingInstallation = true, installationVerified = true,
            checkedAtMillis = 123L, problem = null)
        val after = before.installationLookupFailure("original-id", ApiResult.Problem(404, "", null))
        assertNull(after.problem)
        assertEquals(before.facts, after.facts)
        assertEquals(before.checkedAtMillis, after.checkedAtMillis)
        assertTrue(after.pendingInstallation)
        assertFalse(after.installationVerified)
        assertTrue(after.installationLookupMissing)
        assertEquals("original-id", after.installationLookupId)
    }

    @Test fun `receipt transport failure preserves independent engine error`() {
        val after = DockerControlState(problem = "docker.connection_failed")
            .installationLookupFailure("original-id", ApiResult.Transport("timeout"))
        assertEquals("docker.connection_failed", after.problem)
        assertTrue(after.installationLookupFailed)
        assertFalse(after.installationLookupMissing)
    }
}
