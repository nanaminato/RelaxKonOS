package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ComponentWireTest {
    @Test
    fun `parses a managed component state`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"nginx","state":{"componentId":"nginx","hostId":"host-1","revision":7,
            "ownership":"managed","mode":"systemPackage",
            "target":{"os":"linux","architecture":"x64","distribution":"debian","libc":"glibc",
            "installMode":"SystemPackage","serviceHost":"systemd"},
            "activeVersion":"1.24.0","recoveryRequired":false},
            "modes":[{"mode":"systemPackage","platform":"linux-x64","available":true,"problemCode":null}],
            "problems":[],"pendingOperationId":null}
            """.trimIndent(),
            "nginx",
        )
        assertEquals("nginx", view.componentId)
        assertEquals("managed", view.ownership)
        assertEquals("1.24.0", view.version)
        assertTrue(view.installed)
        assertFalse(view.recoveryRequired)
        assertFalse(view.unsupported)
        assertEquals("linux / x64 / systemd", view.state?.target)
        assertEquals(7L, view.state?.revision)
    }

    @Test
    fun `parses a verified external package and its version change`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"squid","state":{"componentId":"squid","hostId":"host-1","revision":8,
            "ownership":"external","mode":"systemPackage",
            "target":{"os":"linux","architecture":"x64","serviceHost":"systemd"},
            "activeVersion":"6.13-1","recoveryRequired":false},
            "modes":[{"mode":"systemPackage","platform":"linux","available":true,"problemCode":null}],
            "problems":[],"pendingOperationId":null,
            "installedPackage":{"packageName":"squid","version":"6.13-1","architecture":"amd64",
            "executableSha256":"${"a".repeat(64)}"},
            "externalPackageChange":true,"recordedPackageVersion":"6.12-1"}
            """.trimIndent(),
            "squid",
        )
        assertEquals("squid", view.installedPackage?.packageName)
        assertEquals("6.13-1", view.installedPackage?.version)
        assertEquals("amd64", view.installedPackage?.architecture)
        assertEquals("a".repeat(64), view.installedPackage?.executableSha256)
        assertTrue(view.externalPackageChange)
        assertEquals("6.12-1", view.recordedPackageVersion)
        assertEquals("external", view.ownership)
    }

    @Test
    fun `parses an unsupported component with its declared modes`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"samba","state":null,
            "modes":[{"mode":"systemPackage","platform":"windows-x64","available":false,
            "problemCode":"component.platform_unsupported"}],
            "problems":["component.platform_unsupported"],"pendingOperationId":null}
            """.trimIndent(),
            "samba",
        )
        assertNull(view.state)
        assertTrue(view.unsupported)
        assertFalse(view.installed)
        assertEquals(listOf("component.platform_unsupported"), view.problems)
    }

    @Test
    fun `recovery is visible from a pending operation without a state`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"docker","state":null,"modes":[],"problems":[],
            "pendingOperationId":"11111111-1111-1111-1111-111111111111"}
            """.trimIndent(),
            "docker",
        )
        assertTrue(view.recoveryRequired)
    }

    @Test
    fun `rejects a response for another component`() {
        var failed = false
        try {
            ComponentWire.stateView(
                """{"componentId":"nginx","state":null,"modes":[],"problems":[],"pendingOperationId":null}""",
                "docker",
            )
        } catch (_: IllegalArgumentException) {
            failed = true
        }
        assertTrue(failed)
    }

    @Test
    fun `rejects an unknown ownership value`() {
        var failed = false
        try {
            ComponentWire.stateView(
                """
                {"componentId":"nginx","state":{"componentId":"nginx","hostId":"host-1","revision":1,
                "ownership":"borrowed","mode":"systemPackage",
                "target":{"os":"linux","architecture":"x64","serviceHost":"systemd"},
                "activeVersion":null,"recoveryRequired":false},
                "modes":[],"problems":[],"pendingOperationId":null}
                """.trimIndent(),
                "nginx",
            )
        } catch (_: IllegalArgumentException) {
            failed = true
        }
        assertTrue(failed)
    }

    @Test
    fun `parses the host pinned archive list`() {
        val view = ComponentWire.stateView(
            """
            {"componentId":"frpc","state":{"componentId":"frpc","hostId":"host-1","revision":9,
            "ownership":"absent","mode":"managedArchive",
            "target":{"os":"linux","architecture":"x64","serviceHost":"systemd"},
            "activeVersion":null,"recoveryRequired":false},
            "modes":[{"mode":"managedArchive","platform":"linux","available":true,"problemCode":null}],
            "problems":[],"pendingOperationId":null,
            "pinnedArchives":[{"releaseId":"rel-1","version":"v0.61.0","rid":"linux-x64",
            "sourceUri":"https://github.com/fatedier/frp/releases/download/v0.61.0/frp.tar.gz",
            "sha256":"${"a".repeat(64)}","format":"tar.gz","maximumBytes":1048576,
            "affectedComponents":["frpc","frps"]}],
            "pinRevision":42}
            """.trimIndent(),
            "frpc",
        )
        assertEquals(1, view.pinnedArchives.size)
        assertEquals("v0.61.0", view.pinnedArchives.single().version)
        assertEquals(42L, view.pinRevision)
        assertTrue(view.installed.not())
    }

    @Test
    fun `rejects a pinned archive that does not name this component`() {
        var failed = false
        try {
            ComponentWire.stateView(
                """
                {"componentId":"frpc","state":null,"modes":[],"problems":[],"pendingOperationId":null,
                "pinnedArchives":[{"releaseId":"rel-1","version":"v0.61.0","rid":"linux-x64",
                "sourceUri":"https://github.com/fatedier/frp/releases/download/v0.61.0/frp.tar.gz",
                "sha256":"${"a".repeat(64)}","format":"tar.gz","maximumBytes":1048576,
                "affectedComponents":["frps"]}],"pinRevision":42}
                """.trimIndent(),
                "frpc",
            )
        } catch (_: IllegalArgumentException) {
            failed = true
        }
        assertTrue(failed)
    }

    @Test
    fun `a pinned install names the host release and its pin revision`() {
        val state = ComponentState(
            componentId = "frpc", hostId = "host-1", revision = 9, ownership = "absent",
            mode = "managedArchive", target = "linux / x64 / systemd", activeVersion = null, recoveryRequired = false,
        )
        val pin = ComponentPinnedArchive(
            releaseId = "rel-1", version = "v0.61.0", rid = "linux-x64",
            sourceUri = "https://github.com/fatedier/frp/releases/download/v0.61.0/frp.tar.gz",
            sha256 = "a".repeat(64), format = "tar.gz", maximumBytes = 1048576,
            affectedComponents = listOf("frpc", "frps"),
        )
        val body = ComponentWire.planBody("frpc", ComponentChange.Install, state, true, "key", pin, 42)
            .toByteArray().toString(Charsets.UTF_8)
        assertTrue(body.contains("\"mode\":\"ManagedArchive\""))
        assertTrue(body.contains("\"sourceId\":\"host-pins\""))
        assertTrue(body.contains("\"expectedSourceRevision\":42"))
        assertTrue(body.contains("\"releaseId\":\"rel-1\""))
        assertTrue(body.contains("\"requestedVersion\":\"v0.61.0\""))
        assertTrue(body.contains("\"keepData\":true"))
    }

    @Test
    fun `a package install sends no pinned identity`() {
        val state = ComponentState(
            componentId = "docker", hostId = "host-1", revision = 3, ownership = "absent",
            mode = "systemPackage", target = "linux / x64 / systemd", activeVersion = null, recoveryRequired = false,
        )
        val body = ComponentWire.planBody("docker", ComponentChange.Install, state, true, "key")
            .toByteArray().toString(Charsets.UTF_8)
        assertTrue(body.contains("\"mode\":\"SystemPackage\""))
        assertFalse(body.contains("sourceId"))
        assertFalse(body.contains("releaseId"))
        assertTrue(body.contains("\"expectedSourceRevision\":0"))
    }
}
