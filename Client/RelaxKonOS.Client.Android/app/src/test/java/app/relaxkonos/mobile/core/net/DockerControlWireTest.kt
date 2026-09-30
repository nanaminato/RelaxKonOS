package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

class DockerControlWireTest {
    private val default = """{"id":"00000000-0000-0000-0000-000000000000","target":"docker","name":"Default","endpoint":"","isSelected":true}"""
    @Test fun `engine current result distinguishes accepted stop from availability`() {
        val result = DockerControlWire.engine("""{"success":true,"problemCode":"","status":{"isAvailable":false,"problemCode":"docker.daemon_unavailable","serverVersion":null,"operatingSystem":null,"architecture":null}}""")
        assertTrue(result.success); assertFalse(result.status!!.available)
        assertNull(DockerControlWire.engine("""{"success":false,"problemCode":"docker.engine.problem.confirmation_required","status":null}""").status)
        assertTrue(runCatching { DockerControlWire.engine("""{"success":true}""") }.isFailure)
        assertTrue(runCatching { DockerWire.status("{}") }.isFailure)
    }
    @Test fun `mirror list requires current target unique identities default and one selection`() {
        val mirrors = DockerControlWire.mirrors("[$default]"); assertTrue(mirrors.single().default)
        listOf("[]", "[$default,$default]", "[$default]".replace("docker", "Docker"), "[$default]".replace("true", "false")).forEach {
            assertTrue(runCatching { DockerControlWire.mirrors(it) }.isFailure)
        }
        assertTrue(runCatching { DockerControlRoutes.mirror("bad/id") }.isFailure)
    }
    @Test fun `mirror validation rejects credential paths query and option injection`() {
        listOf("mirror.example:443", "https://mirror.example/", "https://[::1]:8443").forEach { assertTrue(DockerMirrorValidation.valid(DockerMirrorRequest("Mirror", it))) }
        listOf("http://mirror.example", "https://user:secret@host", "https://host/path", "https://host?secret=1", "https://host#x", "-host", "host\nnext", "https://host:99999").forEach {
            assertFalse(it, DockerMirrorValidation.valid(DockerMirrorRequest("Mirror", it)))
        }
        assertFalse(DockerMirrorValidation.valid(DockerMirrorRequest(" ", "host")))
        assertFalse(DockerMirrorValidation.valid(DockerMirrorRequest("x".repeat(81), "host")))
    }
    @Test fun `legacy operation payload is rejected instead of parsed in parallel`() {
        assertTrue(runCatching { DockerWire.operation("""{"success":true,"problemCode":"","messages":["old"]}""") }.isFailure)
        val value = DockerWire.operation("""{"success":true,"problemCode":"","logLines":null,"logTruncated":false}""")
        assertTrue(value.logLines.isEmpty()); assertFalse(value.logTruncated)
    }
}
