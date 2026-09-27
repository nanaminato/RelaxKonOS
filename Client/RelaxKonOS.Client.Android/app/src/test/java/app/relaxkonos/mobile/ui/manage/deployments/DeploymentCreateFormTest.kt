package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.core.net.DeploymentTemplate
import org.junit.Assert.*
import org.junit.Test

class DeploymentCreateFormTest {
    private val image = DeploymentTemplate("image", "Image", null, false, true, false, 8080)
    private val python = DeploymentTemplate("pythonProject", "Python", "python:3.13-slim", true, false, false, 8000)

    @Test fun `wizard validates each step before advancing and submits the same definition fields`() {
        val form = DeploymentCreateForm(listOf(image, python))
        assertFalse(form.next())
        form.name = "website"
        assertTrue(form.next())
        form.image = "nginx"
        assertFalse(form.next())
        form.image = "nginx:1.27"
        assertTrue(form.next())
        assertFalse(form.next()) // HTTP readiness needs a published host port.
        form.hostPort = "18080"
        assertTrue(form.next())
        form.volumes = "data:/var/lib/data:ro"
        form.cpuCores = "1.5"
        form.memoryMegabytes = "512"
        form.pidsLimit = "100"
        assertTrue(form.next())
        form.siteId = "site42"
        assertTrue(form.next())
        val definition = form.imageDefinition()
        assertEquals(18080, definition.hostPort)
        assertEquals("site42", definition.siteId)
        assertEquals(1.5, definition.limits?.cpuCores)
        assertEquals(512L * 1024 * 1024, definition.limits?.memoryBytes)
        assertEquals("/var/lib/data", definition.volumes.single().containerPath)
        assertTrue(definition.volumes.single().readOnly)
    }

    @Test fun `archive choice and worker readiness stay on the source selected for submission`() {
        val form = DeploymentCreateForm(listOf(image, python))
        form.name = "worker"
        form.selectSource("pythonProject")
        form.selectWorkload("worker")
        assertEquals("process", form.readiness)
        assertTrue(form.next())
        form.programEntry = "worker.main"
        assertEquals("archive", form.problemAt(1))
        form.archiveName = "worker.zip"
        form.arguments = "--workers\n2"
        assertTrue(form.next())
        assertTrue(form.next()) // A process worker does not require a host port.
        val definition = form.archiveDefinition()
        assertNull(definition.healthCheckPath)
        assertEquals(listOf("--workers", "2"), definition.arguments)
    }
}
