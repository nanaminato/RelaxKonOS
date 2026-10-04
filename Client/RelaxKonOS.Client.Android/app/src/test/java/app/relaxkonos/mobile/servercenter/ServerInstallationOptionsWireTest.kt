package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.UUID

class ServerInstallationOptionsWireTest {
    @Test
    fun `advanced options preserve types and escaped paths for the host launcher`() {
        val options = ServerDeploymentOptions(ServerPackageSourceKind.DirectUrl, ServerNetworkProfile.Lan,
            mode = ServerInstallMode.LinuxSystem, packageUri = "https://example.invalid/server.zip",
            packageDigest = "a".repeat(64), serverPort = 5100, language = "ja-JP",
            releaseCatalogBaseUri = "https://example.invalid/releases", installRoot = "/srv/program",
            dataRoot = "/srv/data", fileAccess = "whitelist", fileRoots = listOf("/srv/shared space"),
            administratorFileAccess = "whitelist", administratorFileRoots = listOf("/srv/admin\"files"),
            rootFileAccess = "full", dockerAccess = true, allowUnsupportedSystem = true, addFirewallRule = true)
        val fields = wireOptions(options)
        assertEquals("directUrl", fields.getValue("source").asString())
        assertEquals(5100L, fields.getValue("serverPort").asLong())
        for ((key, expected) in mapOf("language" to "ja-JP", "installRoot" to "/srv/program",
            "dataRoot" to "/srv/data", "rootFileAccess" to "full",
            "releaseCatalogBaseUri" to "https://example.invalid/releases")) {
            assertEquals(expected, fields.getValue(key).asString())
        }
        assertEquals(listOf("/srv/shared space"), fields.getValue("fileRoots").asArray().map { it.asString() })
        assertEquals(listOf("/srv/admin\"files"), fields.getValue("administratorFileRoots").asArray().map { it.asString() })
        assertTrue(fields.getValue("addFirewallRule").asBoolean())
        assertTrue(fields.getValue("dockerAccess").asBoolean())
        assertTrue(fields.getValue("allowUnsupportedSystem").asBoolean())
    }

    @Test
    fun `user roots and Windows paths survive the same wire format`() {
        val fields = wireOptions(ServerDeploymentOptions(ServerPackageSourceKind.LocalBundle, ServerNetworkProfile.Loopback,
            dataRoot = "D:\\Data\\RelaxKonOS", configRoot = "/home/me/config",
            stateRoot = "/home/me/state", cacheRoot = "/home/me/cache"))
        for ((key, expected) in mapOf("dataRoot" to "D:\\Data\\RelaxKonOS", "configRoot" to "/home/me/config",
            "stateRoot" to "/home/me/state", "cacheRoot" to "/home/me/cache")) {
            assertEquals(expected, fields.getValue(key).asString())
        }
        assertFalse(fields.getValue("addFirewallRule").asBoolean())
        assertFalse(fields.getValue("dockerAccess").asBoolean())
    }

    private fun wireOptions(options: ServerDeploymentOptions): Map<String, ServerCenterJsonValue> {
        val bytes = ServerDeploymentWire.writeRequest(ServerDeploymentRequest(1, UUID.randomUUID().toString(),
            ServerDeploymentKind.Install, options))
        return ServerCenterJson.parse(bytes.toString(Charsets.UTF_8), ServerDeploymentWire.MAXIMUM_REQUEST_BYTES)
            .asObject().getValue("options").asObject()
    }
}
