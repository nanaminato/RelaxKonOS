package app.relaxkonos.mobile.core.net

import app.relaxkonos.mobile.ui.manage.components.AppComponentDependencies
import app.relaxkonos.mobile.ui.manage.components.ComponentCatalog
import app.relaxkonos.mobile.ui.nav.Routes
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TailscaleComponentWireTest {
    private fun plan(rid: String = "win-x64", arch: String = "amd64"): JSONObject {
        val installer = JSONObject().put("version", "1.104.4").put("rid", rid).put("format", "msi")
            .put("fileName", "tailscale-setup-1.104.4-$arch.msi")
            .put("downloadUrl", "https://pkgs.tailscale.com/stable/tailscale-setup-1.104.4-$arch.msi")
            .put("sha256", "a".repeat(64))
        return JSONObject().put("request", JSONObject().put("componentId", "tailscale").put("change", "install")
            .put("mode", "nativeInstaller").put("hostId", "windows-host").put("expectedRevision", 7)
            .put("idempotencyKey", "frozen-tail-key").put("keepData", true))
            .put("target", JSONObject().put("mode", "nativeInstaller").put("installer", installer))
    }

    @Test fun `Windows MSI plans retain the exact trusted source version and digest`() {
        listOf("win-x64" to "amd64", "win-arm64" to "arm64").forEach { (rid, arch) ->
            val parsed = ComponentWire.plan(plan(rid, arch).toString(), "tailscale", ComponentChange.Install)
            assertTrue(parsed.allowed)
            assertEquals("1.104.4", parsed.version)
            assertEquals("a".repeat(64), parsed.sha256)
            assertEquals("https://pkgs.tailscale.com/stable/tailscale-setup-1.104.4-$arch.msi", parsed.source)
            val body = JSONObject(InstallationWire.request(ComponentFrozenInstallationRequest(true,
                InstallationService.Tailscale, "tailscale", parsed.requestJson!!, parsed.targetJson!!,
                false, setOf(InstallationKind.Install)), InstallationKind.Install).toByteArray().decodeToString())
            assertEquals("frozen-tail-key", body.getJSONObject("frozenPackage").getJSONObject("request").getString("idempotencyKey"))
            assertEquals("msi", body.getJSONObject("frozenPackage").getJSONObject("expected").getJSONObject("installer").getString("format"))
            assertFalse(body.has("frozenArchive"))
            assertFalse(body.has("fileReferenceId"))
        }
    }

    @Test fun `MSI plans reject altered hosts digests architectures and archive targets`() {
        val altered = listOf<(JSONObject) -> Unit>(
            { it.getJSONObject("target").getJSONObject("installer").put("downloadUrl", "https://example.test/setup.msi") },
            { it.getJSONObject("target").getJSONObject("installer").put("sha256", "bad") },
            { it.getJSONObject("target").getJSONObject("installer").put("rid", "linux-x64") },
            { it.getJSONObject("target").getJSONObject("installer").put("format", "exe") },
            { it.getJSONObject("target").put("archive", JSONObject()) },
        )
        altered.forEach { change ->
            assertTrue(runCatching { val value = plan(); change(value); ComponentWire.plan(value.toString(), "tailscale", ComponentChange.Install) }.isFailure)
        }
    }

    @Test fun `official source setup and recovery use typed Tailscale installation maintenance`() {
        val original = "00112233-4455-6677-8899-aabbccddeeff"
        val request = ComponentMaintenanceInstallationRequest(true, InstallationService.Tailscale, "tailscale", original, "Stop")
        val body = JSONObject(InstallationWire.request(request, InstallationKind.Repair).toByteArray().decodeToString())
        assertEquals(original, body.getJSONObject("recovery").getString("operationId"))
        assertEquals("Stop", body.getJSONObject("recovery").getString("action"))
        assertEquals("hostNetworkChange", InstallationService.Tailscale.elevation)
        assertEquals("tailscale", InstallationService.Tailscale.elevationTarget)
        assertTrue(runCatching { InstallationWire.request(request.copy(action = "RunCommand"), InstallationKind.Repair) }.isFailure)
        assertTrue(runCatching { InstallationWire.request(request.copy(service = InstallationService.Nginx), InstallationKind.Repair) }.isFailure)
        assertEquals(listOf("tailscale"), AppComponentDependencies.componentsFor(Routes.MANAGE_TAILSCALE))
        assertTrue("tailscale" in ComponentCatalog.changeableIds)
        assertFalse("tailscale" in ComponentCatalog.fileInstallableIds)
    }
}
