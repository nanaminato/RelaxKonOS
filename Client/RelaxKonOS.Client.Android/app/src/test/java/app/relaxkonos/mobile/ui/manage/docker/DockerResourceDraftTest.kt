package app.relaxkonos.mobile.ui.manage.docker
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test
class DockerResourceDraftTest {
    @Test fun `draft preserves complete fields and rejects invalid optional numbers`() {
        val draft = DockerResourceDraft(DockerResourceAction.CreateContainer, name = "web", image = "nginx", arguments = "serve\n--quiet", ports = "8080:80", environment = "TOKEN=secret", mounts = "data:/data", network = "bridge", restart = "always", labels = "team=mobile", cpu = "0.5", memory = "4096", pids = "20", logDriver = "local", logOptions = "max-size=10m")
        val request = draft.change(null)!!.container!!
        assertEquals(listOf("serve", "--quiet"), request.arguments); assertEquals(listOf("TOKEN=secret"), request.environment)
        assertEquals(DockerContainerResources(0.5, 4096, 20, "local", listOf("max-size=10m")), request.resources)
        assertNull(draft.copy(cpu = "wrong").change(null)); assertNull(draft.copy(cpu = "NaN").change(null)); assertNull(draft.copy(memory = "-1").change(null))
        assertNull(draft.copy(labels = "com.docker.compose.project=forged").change(null))
    }
}
