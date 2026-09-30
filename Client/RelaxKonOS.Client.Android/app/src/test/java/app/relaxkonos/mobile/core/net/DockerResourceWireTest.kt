package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
class DockerResourceWireTest {
    @Test fun `network and volume ownership labels are required current facts`() {
        val json = """{"id":"abc123","name":"custom","driver":"bridge","scope":"local","containers":[],"labels":{"com.docker.compose.project":"web"}}"""
        val network = DockerResourceWire.network(json)
        assertTrue(DockerResourceTarget(DockerResourceKind.Networks, network.id, network = network).managed)
        assertEquals("web", network.labels["com.docker.compose.project"])
        assertTrue(runCatching { DockerResourceWire.network(JSONObject(json).apply { remove("labels") }.toString()) }.isFailure)
        val volume = DockerResourceWire.volume("""{"name":"data","driver":"local","mountpoint":"/data","usedBy":["stopped"],"labels":{}}""")
        assertEquals(listOf("stopped"), volume.usedBy)
        assertTrue(runCatching { DockerResourceWire.volume("""{"name":"data","driver":"local","mountpoint":"/data","usedBy":[]}""") }.isFailure)
    }
    @Test fun `details stats and identities retain full fields and reject missing fields`() {
        val json = """{"id":"abc123fff","name":"web","image":"nginx","created":"now","state":"running","status":"Up","command":"serve","workingDirectory":"/work","restartPolicy":"always","ports":["80/tcp"],"mounts":["/data"],"networks":["bridge"],"environment":["TOKEN=secret"],"labels":{}}"""
        assertEquals(listOf("TOKEN=secret"), DockerResourceWire.container(json).environment)
        assertTrue(runCatching { DockerResourceWire.container(JSONObject(json).apply { remove("environment") }.toString()) }.isFailure)
        val stats = DockerResourceWire.stats("""{"containerId":"abc123","cpuPercent":"2%","memoryUsage":"3MiB / 10MiB","networkIo":"0B / 0B","blockIo":"0B / 0B"}""")
        assertEquals("2%", stats.cpuPercent)
        assertTrue(runCatching { DockerWire.logs("""{"truncated":false}""") }.isFailure)
        assertEquals(listOf("stderr"), DockerWire.logs("""{"lines":["stderr"],"truncated":false}""").lines)
        assertTrue(DockerResourceValidation.identity("abc123", "abc123fff")); assertFalse(DockerResourceValidation.identity("web", "web-other"))
    }
    @Test fun `full create preserves typed constraints and rejects invalid values or forged ownership`() {
        val create = DockerContainerCreate("web", "nginx:alpine", listOf("serve", "--flag"), listOf("8080:80"), listOf("KEY=value"), listOf("data:/data"), "bridge", "always", listOf("team=研发"), DockerContainerResources(0.5, 4096, 20, "local", listOf("max-size=10m")))
        assertTrue(DockerResourceValidation.create(create))
        val body = JSONObject(create.body().toByteArray().decodeToString()); assertEquals(0.5, body.getJSONObject("resources").getDouble("cpuCores"), 0.0)
        assertEquals(4096L, body.getJSONObject("resources").getLong("memoryBytes"))
        assertFalse(DockerResourceValidation.create(create.copy(resources = DockerContainerResources(Double.NaN))))
        assertFalse(DockerResourceValidation.create(create.copy(resources = DockerContainerResources(memoryBytes = -1))))
        assertFalse(DockerResourceValidation.create(create.copy(labels = listOf("relaxkonos.owner=application-deployment"))))
        assertFalse(DockerResourceValidation.create(create.copy(arguments = listOf("bad\nvalue"))))
        assertFalse(DockerResourceValidation.image("repo@sha256:123")); assertFalse(DockerResourceValidation.name("-flag"))
    }
}
