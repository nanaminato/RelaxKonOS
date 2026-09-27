package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DockerWireTest {
    @Test fun `parses current Docker resource contract without diagnostics`() {
        val status = DockerWire.status("""{"isAvailable":true,"problemCode":"","serverVersion":"27.3","operatingSystem":"linux","architecture":"x64"}""")
        val containers = DockerWire.containers("""[{"id":"abc","names":"web","image":"nginx:alpine","state":"running","status":"Up 1m"}]""")
        val operation = DockerWire.operation("""{"success":true,"problemCode":"","messages":["web Started"]}""")

        assertTrue(status.available)
        assertEquals("27.3", status.serverVersion)
        assertEquals("web", containers.single().names)
        assertTrue(operation.success)
        assertEquals(listOf("web Started"), operation.messages)
    }

    @Test fun `container operation result accepts its bounded log shape`() {
        val operation = DockerWire.operation("""{"success":false,"problemCode":"docker.operation_failed","logLines":["hidden"]}""")

        assertFalse(operation.success)
        assertEquals("docker.operation_failed", operation.problemCode)
        assertTrue(operation.messages.isEmpty())
    }

    @Test fun `routes escape stack and container identities`() {
        assertEquals("/api/v1.0/docker/stacks/one+two/services", DockerRoutes.stackServices("one two"))
        assertEquals("/api/v1.0/docker/containers/a%2Fb/logs?tail=200", DockerRoutes.containerLogs("a/b", 200))
    }
}
