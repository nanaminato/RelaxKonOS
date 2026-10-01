package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class DeploymentRevisionSourceTest {
    @Test fun `source body preserves argument boundaries and never includes definition or secrets`() {
        val input = DeploymentRevisionSource(imageReference = "registry.test:5000/site:v1", arguments = listOf("", " value with spaces ", "x=y"))
        assertTrue(input.validFor("image"))
        val body = JSONObject(input.json())
        assertEquals("", body.getJSONArray("arguments").getString(0)); assertEquals(" value with spaces ", body.getJSONArray("arguments").getString(1))
        assertFalse(body.has("name")); assertFalse(body.has("configuration")); assertFalse(body.has("volumes")); assertFalse(body.has("hostPort"))
    }
    @Test fun `current server source vocabulary rejects floating invalid or mixed inputs`() {
        for (image in listOf("nginx", "nginx:latest", "nginx:Latest", "registry:5000/nginx", "nginx:1 2", "nginx@sha256:" + "a".repeat(64)))
            assertFalse(DeploymentRevisionSource(imageReference = image).validFor("image"))
        assertFalse(DeploymentRevisionSource(imageReference = "nginx:1", archiveReferenceId = "archive").validFor("image"))
        assertFalse(DeploymentRevisionSource(imageReference = "nginx:1", arguments = listOf("a\nb")).validFor("image"))
        assertFalse(DeploymentRevisionSource(imageReference = "nginx:1", arguments = List(65) { "" }).validFor("image"))
    }
    @Test fun `archive source respects runtime boundaries and requires python entry`() {
        val input = DeploymentRevisionSource(archiveReferenceId = "opaque", baseImage = "python:3.12-slim", programEntry = "worker.py")
        assertTrue(input.validFor("pythonProject")); assertFalse(input.copy(programEntry = null).validFor("pythonProject"))
        assertFalse(input.copy(selfContained = true).validFor("javaJar")); assertTrue(input.copy(selfContained = true).validFor("dotNetPublish"))
        assertFalse(input.validFor("unknown")); assertFalse(input.copy(archiveReferenceId = null).validFor("javaJar"))
    }
}
