package app.relaxkonos.mobile.servercenter

import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.Assert.assertFalse
import org.junit.Assert.fail

class ServerCenterDeploymentClientTest {
    @Test fun `clear uses fixed action and rejects invalid identifiers`() = runTest {
        val transport = FakeDeploymentTransport()
        val client = ServerCenterDeploymentClient(transport)
        val lookup = client.stageLookup(ServerHostPlatform.Linux, ServerCenterUploadAsset.bytes(byteArrayOf(1)))
        val id = UUID.randomUUID().toString()
        client.clearOperation(lookup, id)
        assertTrue(transport.commands.last().endsWith(" --clear-operation $id"))
        val count = transport.commands.size
        try { client.clearOperation(lookup, "../../bad"); fail("Invalid id accepted") } catch (_: IllegalArgumentException) { }
        assertEquals(count, transport.commands.size)
        transport.clearExitStatus = 75
        try { client.clearOperation(lookup, id); fail("Clear failure ignored") } catch (_: java.io.IOException) { }
    }
    @Test fun `details read fixed diagnostics action with bounded redacted text`() = runTest {
        val transport = FakeDeploymentTransport()
        val client = ServerCenterDeploymentClient(transport)
        val lookup = client.stageLookup(ServerHostPlatform.Linux, ServerCenterUploadAsset.bytes(byteArrayOf(1)))
        val id = UUID.randomUUID().toString()
        val log = client.diagnostics(lookup, id)
        assertTrue(transport.commands.last().endsWith(" --diagnostics $id"))
        assertTrue(log.contains("[redacted]"))
        assertFalse(log.contains("private-value"))
        assertTrue(log.length <= 65536)
        val count = transport.commands.size
        try { client.diagnostics(lookup, "../../bad"); fail("Invalid id accepted") } catch (_: IllegalArgumentException) { }
        assertEquals(count, transport.commands.size)
    }

    @Test
    fun sudoPasswordUsesStdinAndAccountJournal() = runTest {
        val transport = FakeDeploymentTransport()
        val client = ServerCenterDeploymentClient(transport)
        val staged = client.stage(ServerDeploymentRequest(1, UUID.randomUUID().toString(), ServerDeploymentKind.Probe),
            ServerHostPlatform.Linux, ServerCenterUploadAsset.bytes("launcher".toByteArray()))
        client.execute(staged, "sudo-secret")
        assertEquals(listOf("sudo-secret"), transport.inputLines)
        assertTrue(transport.commands.any { it.endsWith(" --run-with-sudo") })
        assertTrue(transport.commands.none { "sudo-secret" in it })
        assertTrue(transport.uploaded.values.none { "sudo-secret" in it.toString(Charsets.UTF_8) })
        assertTrue(transport.commands.last().contains(" --query " + staged.operationId))
        client.execute(staged, "")
        assertEquals("", transport.inputLines.last())
    }

    @Test
    fun `probe stages fixed assets and reads authoritative receipt`() = runTest {
        val transport = FakeDeploymentTransport()
        val client = ServerCenterDeploymentClient(transport)
        val operationId = UUID.randomUUID().toString()
        val request = ServerDeploymentRequest(
            ServerDeploymentProtocol.VERSION,
            operationId,
            ServerDeploymentKind.Probe,
            ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback),
        )

        val staged = client.stage(
            request = request,
            platform = ServerHostPlatform.Linux,
            launcher = ServerCenterUploadAsset.bytes("#!/bin/sh\n".toByteArray()),

        )

        assertEquals(operationId, staged.operationId)
        assertEquals(
            setOf(
                "/tmp/relaxkonos-deploy.abcdefgh/relaxkonos-deploy.sh",
                "/tmp/relaxkonos-deploy.abcdefgh/request.json",
            ),
            transport.uploaded.keys,
        )
        assertTrue(transport.commands.any { it.startsWith("chmod 700 ") })
        assertEquals("request.json", transport.uploadOrder.last().substringAfterLast('/'))

        val receipt = client.execute(staged)
        assertEquals(operationId, receipt.operationId)
        assertEquals(ServerDeploymentState.Failed, receipt.state)
        assertTrue(transport.commands[transport.commands.lastIndex - 1].contains(" --run"))
        assertTrue(transport.commands.last().contains(" --query $operationId"))
    }

    @Test
    fun `reconnect lists and queries with launcher only and never reruns the operation`() = runTest {
        val transport = FakeDeploymentTransport()
        val client = ServerCenterDeploymentClient(transport)
        val operationId = UUID.randomUUID().toString()
        transport.listedOperationId = operationId
        val staged = client.stageLookup(ServerHostPlatform.Linux,
            ServerCenterUploadAsset.bytes("launcher".toByteArray()))
        assertEquals(setOf(
            "/tmp/relaxkonos-deploy.abcdefgh/relaxkonos-deploy.sh",
        ), transport.uploaded.keys)
        assertTrue(transport.commands.none { it.contains(" --run") })
        assertEquals(listOf(operationId), client.list(staged))
        assertEquals(operationId, client.query(staged, operationId).operationId)
        assertTrue(transport.commands.none { it.contains(" --run") })
    }

    @Test
    fun `malformed recovery id cannot become a remote command`() = runTest {
        val transport = FakeDeploymentTransport()
        var rejected = false
        try {
            ServerCenterDeploymentClient(transport).query(
                ServerCenterStagedLookup(ServerHostPlatform.Linux, "/tmp/relaxkonos-deploy.abcdefgh"),
                "invalid; touch /tmp/wrong")
        } catch (_: IllegalArgumentException) { rejected = true }
        assertTrue(rejected)
        assertTrue(transport.commands.isEmpty())
        assertTrue(transport.uploaded.isEmpty())
    }

    @Test
    fun `malformed remote list is not accepted as operation references`() = runTest {
        val transport = FakeDeploymentTransport()
        transport.listedOperationId = "../../wrong"
        val client = ServerCenterDeploymentClient(transport)
        val staged = client.stageLookup(ServerHostPlatform.Linux, ServerCenterUploadAsset.bytes(byteArrayOf(1)))
        var rejected = false
        try { client.list(staged) } catch (_: java.io.IOException) { rejected = true }
        assertTrue(rejected)
        assertTrue(transport.commands.none { it.contains(" --run") })

        transport.listedOperationId = UUID(0, 0).toString()
        rejected = false
        try { client.list(staged) } catch (_: java.io.IOException) { rejected = true }
        assertTrue(rejected)
    }

    @Test
    fun `missing receipt is distinct from an unavailable SSH query`() = runTest {
        val transport = FakeDeploymentTransport()
        val id = UUID.randomUUID().toString()
        transport.missingOperationId = id
        val client = ServerCenterDeploymentClient(transport)
        val staged = client.stageLookup(ServerHostPlatform.Linux, ServerCenterUploadAsset.bytes(byteArrayOf(1)))
        var missing = false
        try { client.query(staged, id) } catch (_: ServerDeploymentReceiptMissingException) { missing = true }
        assertTrue(missing)
        assertTrue(transport.commands.none { it.contains(" --run") })
    }

    @Test
    fun `local install archive needs no official checksum before upload`() = runTest {
        val release = releaseArchive("payload/linux/server/RelaxKonOS.Server", "server bytes".toByteArray())
        try {
            val transport = FakeDeploymentTransport()
            val client = ServerCenterDeploymentClient(transport)
            val operationId = UUID.randomUUID().toString()
            val request = installRequest(operationId, sha256(release.file.readBytes())).let { it.copy(options = it.options!!.copy(packageDigest = null)) }

            client.stage(
                request = request,
                platform = ServerHostPlatform.Linux,
                launcher = ServerCenterUploadAsset.bytes("launcher".toByteArray()),

                archiveFile = release.file,
                expectedRuntime = ServerRuntimeIdentifier.LinuxX64,
            )

            assertTrue(transport.uploaded.containsKey("/tmp/relaxkonos-deploy.abcdefgh/server.zip"))
        } finally {
            release.file.delete()
        }
    }

    @Test
    fun `official and server sources stage no package or verifier`() = runTest {
        for (source in listOf(ServerPackageSourceKind.OfficialStable, ServerPackageSourceKind.RemoteBundle)) {
            val transport = FakeDeploymentTransport()
            val options = ServerDeploymentOptions(source, ServerNetworkProfile.Loopback,
                mode = ServerInstallMode.LinuxUser,
                remotePackagePath = if (source == ServerPackageSourceKind.RemoteBundle) "/home/alice/server.zip" else null)
            ServerCenterDeploymentClient(transport).stage(
                ServerDeploymentRequest(1, UUID.randomUUID().toString(), ServerDeploymentKind.Install, options),
                ServerHostPlatform.Linux, ServerCenterUploadAsset.bytes("launcher".toByteArray()),
                expectedRuntime = ServerRuntimeIdentifier.LinuxX64)
            assertEquals(setOf("relaxkonos-deploy.sh", "request.json"),
                transport.uploaded.keys.map { it.substringAfterLast('/') }.toSet())
        }
    }

    @Test
    fun `wrong runtime is rejected before touching host`() = runTest {
        val release = releaseArchive("payload/linux/server/RelaxKonOS.Server", "server".toByteArray())
        try {
            val transport = FakeDeploymentTransport()
            var rejected = false
            try {
                ServerCenterDeploymentClient(transport).stage(
                    request = installRequest(UUID.randomUUID().toString(), sha256(release.file.readBytes())),
                    platform = ServerHostPlatform.Linux,
                    launcher = ServerCenterUploadAsset.bytes(byteArrayOf(1)),

                    archiveFile = release.file,
                    expectedRuntime = ServerRuntimeIdentifier.WinX64,
                )
            } catch (_: IllegalArgumentException) {
                rejected = true
            }
            assertTrue(rejected)
            assertTrue(transport.commands.isEmpty())
        } finally {
            release.file.delete()
        }
    }

    private fun installRequest(operationId: String, digest: String) = ServerDeploymentRequest(
        schemaVersion = ServerDeploymentProtocol.VERSION,
        operationId = operationId,
        kind = ServerDeploymentKind.Install,
        options = ServerDeploymentOptions(
            source = ServerPackageSourceKind.LocalBundle,
            network = ServerNetworkProfile.Loopback,
            mode = ServerInstallMode.LinuxSystem,
            version = "0.1.0",
            stagedPackageName = "server.zip",
            packageDigest = digest,
            confirmed = true,
        ),
    )

    private fun releaseArchive(path: String, payload: ByteArray, listedPayload: ByteArray = payload): ReleaseArchive {
        val manifest = """{"schemaVersion":1,"packageKind":"server","version":"0.1.0","runtime":"linux-x64","supportedSystems":["linux"],"payload":{"linux":{"server":"$path"}},"files":[{"path":"$path","length":${listedPayload.size},"sha256":"${sha256(listedPayload)}"}]}"""
            .toByteArray()
        val file = File.createTempFile("relaxkonos-android-release-", ".zip")
        ZipOutputStream(file.outputStream()).use { zip ->
            listOf("manifest.json" to manifest, path to payload).forEach { (name, bytes) ->
                zip.putNextEntry(ZipEntry(name))
                zip.write(bytes)
                zip.closeEntry()
            }
        }
        return ReleaseArchive(file)
    }

    private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
        .digest(bytes).joinToString("") { "%02x".format(it) }

    private data class ReleaseArchive(val file: File)
}

private class FakeDeploymentTransport : ServerCenterSshTransport {
    override val isConnected: Boolean = true
    override val observedHostKey: ServerCenterHostKeyObservation? = null
    val commands = mutableListOf<String>()
    val inputLines = mutableListOf<String?>()
    val uploaded = linkedMapOf<String, ByteArray>()
    val uploadOrder = mutableListOf<String>()
    var listedOperationId: String? = null
    var missingOperationId: String? = null
    var clearExitStatus = 0

    override suspend fun connect(
        endpoint: ServerCenterSshEndpoint,
        credential: SshCredential,
        hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
    ) = error("not used")

    override suspend fun run(command: String): ServerCenterSshCommandResult {
        commands += command
        return when {
            command.contains(" --clear-operation ") -> ServerCenterSshCommandResult(clearExitStatus, "", "")
            command.contains(" --diagnostics ") -> ServerCenterSshCommandResult(0, "password=private-value\n" + "x".repeat(70000), "")
            command.contains("mktemp") -> ServerCenterSshCommandResult(
                0, "/tmp/relaxkonos-deploy.abcdefgh\n", "",
            )
            command.contains(" --query ") -> {
                val operationId = command.substringAfterLast(' ')
                if (operationId == missingOperationId) ServerCenterSshCommandResult(66, "", "missing")
                else ServerCenterSshCommandResult(
                    0,
                    """{"schemaVersion":1,"operationId":"$operationId","installationId":null,"kind":"probe","phase":"failed","state":"failed","sequence":1,"timestampUtc":"2026-09-25T00:00:00Z","progress":null,"problemCode":"server-deployment.failed","safeMessage":"failed","cancellable":false,"startedAtUtc":null,"completedAtUtc":null,"result":null,"snapshot":null,"probe":null}""",
                    "",
                )
            }
            command.contains(" --list") -> ServerCenterSshCommandResult(
                0, listedOperationId?.let { "$it.json\n" }.orEmpty(), "",
            )
            else -> ServerCenterSshCommandResult(0, "", "")
        }
    }

    override suspend fun runWithInput(command: String, inputLine: String?): ServerCenterSshCommandResult {
        inputLines += inputLine
        return run(command)
    }

    override suspend fun openTerminal(): ServerCenterSshTerminal = error("not used")

    override suspend fun upload(
        content: InputStream,
        contentLength: Long?,
        remotePath: String,
        progress: ((Double) -> Unit)?,
    ) {
        uploaded[remotePath] = content.readBytes()
        uploadOrder += remotePath
        progress?.invoke(1.0)
    }

    override suspend fun download(remotePath: String, destination: OutputStream) = error("not used")

    override suspend fun listDirectory(remotePath: String): List<SshFileEntry> = error("not used")

    override suspend fun createDirectory(remotePath: String) = error("not used")

    override suspend fun delete(remotePath: String, recursive: Boolean) = error("not used")

    override suspend fun rename(sourcePath: String, destinationPath: String) = error("not used")

    override fun openLocalForward(remotePort: Int, preferredLocalPort: Int?): ServerCenterSshTunnel = error("not used")
    override suspend fun testLoopbackPort(remotePort: Int): Boolean = error("not used")
    override suspend fun fileInfo(remotePath: String): SshFileEntry? = error("not used")
    override suspend fun uploadNew(content: InputStream, contentLength: Long?, remotePath: String) = error("not used")
    override suspend fun copyFile(sourcePath: String, destinationPath: String, maximumBytes: Long) = error("not used")
    override fun openLoopbackTunnel(remotePort: Int, basePath: String?): ServerCenterSshTunnel = error("not used")

    override fun close() = Unit
}
