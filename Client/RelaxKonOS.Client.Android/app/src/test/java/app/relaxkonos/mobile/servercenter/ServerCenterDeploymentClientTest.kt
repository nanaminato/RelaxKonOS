package app.relaxkonos.mobile.servercenter

import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.MGF1ParameterSpec
import java.security.spec.PSSParameterSpec
import java.util.UUID
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerCenterDeploymentClientTest {

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
            verifier = ServerCenterUploadAsset.bytes("verifier".toByteArray()),
        )

        assertEquals(operationId, staged.operationId)
        assertEquals(
            setOf(
                "/tmp/relaxkonos-deploy.abcdefgh/release-verifier",
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
    fun `signed install archive is fully verified before upload`() = runTest {
        val release = signedRelease("payload/linux/server/RelaxKonOS.Server", "server bytes".toByteArray())
        try {
            val transport = FakeDeploymentTransport()
            val client = ServerCenterDeploymentClient(transport)
            val operationId = UUID.randomUUID().toString()
            val request = installRequest(operationId, sha256(release.file.readBytes()))

            client.stage(
                request = request,
                platform = ServerHostPlatform.Linux,
                launcher = ServerCenterUploadAsset.bytes("launcher".toByteArray()),
                verifier = ServerCenterUploadAsset.bytes("verifier".toByteArray()),
                signedArchive = release.file,
                expectedRuntime = ServerRuntimeIdentifier.LinuxX64,
                trustedKeyId = release.keyId,
                trustedPublicKeyPem = release.publicKeyPem,
            )

            assertTrue(transport.uploaded.containsKey("/tmp/relaxkonos-deploy.abcdefgh/server.zip"))
            assertTrue(transport.uploaded.containsKey("/tmp/relaxkonos-deploy.abcdefgh/release-public.pem"))
            assertTrue(transport.uploaded.containsKey("/tmp/relaxkonos-deploy.abcdefgh/release-key-id.txt"))
        } finally {
            release.file.delete()
        }
    }

    @Test
    fun `tampered signed archive is rejected before remote staging`() = runTest {
        val release = signedRelease(
            "payload/linux/server/RelaxKonOS.Server",
            "tampered".toByteArray(),
            signedPayload = "original".toByteArray(),
        )
        try {
            val transport = FakeDeploymentTransport()
            val client = ServerCenterDeploymentClient(transport)
            val request = installRequest(UUID.randomUUID().toString(), sha256(release.file.readBytes()))
            var rejected = false
            try {
                client.stage(
                    request = request,
                    platform = ServerHostPlatform.Linux,
                    launcher = ServerCenterUploadAsset.bytes("launcher".toByteArray()),
                    verifier = ServerCenterUploadAsset.bytes("verifier".toByteArray()),
                    signedArchive = release.file,
                    expectedRuntime = ServerRuntimeIdentifier.LinuxX64,
                    trustedKeyId = release.keyId,
                    trustedPublicKeyPem = release.publicKeyPem,
                )
            } catch (_: java.io.IOException) {
                rejected = true
            }

            assertTrue(rejected)
            assertTrue(transport.commands.isEmpty())
            assertTrue(transport.uploaded.isEmpty())
        } finally {
            release.file.delete()
        }
    }

    @Test
    fun `wrong runtime is rejected before touching host`() = runTest {
        val release = signedRelease("payload/linux/server/RelaxKonOS.Server", "server".toByteArray())
        try {
            val transport = FakeDeploymentTransport()
            var rejected = false
            try {
                ServerCenterDeploymentClient(transport).stage(
                    request = installRequest(UUID.randomUUID().toString(), sha256(release.file.readBytes())),
                    platform = ServerHostPlatform.Linux,
                    launcher = ServerCenterUploadAsset.bytes(byteArrayOf(1)),
                    verifier = ServerCenterUploadAsset.bytes(byteArrayOf(2)),
                    signedArchive = release.file,
                    expectedRuntime = ServerRuntimeIdentifier.WinX64,
                    trustedKeyId = release.keyId,
                    trustedPublicKeyPem = release.publicKeyPem,
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

    private fun signedRelease(path: String, payload: ByteArray, signedPayload: ByteArray = payload): SignedRelease {
        val keyId = "test-key"
        val pair = KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()
        val manifest = """{"schemaVersion":1,"packageKind":"server","version":"0.1.0","runtime":"linux-x64","supportedSystems":["linux"],"payload":{"linux":{"server":"$path"}},"files":[{"path":"$path","length":${signedPayload.size},"sha256":"${sha256(signedPayload)}"}]}"""
            .toByteArray()
        val signer = Signature.getInstance("RSASSA-PSS")
        signer.setParameter(PSSParameterSpec("SHA-256", "MGF1", MGF1ParameterSpec.SHA256, 32, 1))
        signer.initSign(pair.private)
        signer.update(manifest)
        val signature = """{"schemaVersion":1,"keyId":"$keyId","algorithm":"rsa-pss-sha256","signedSha256":"${sha256(manifest)}","signature":"${Base64Codec.encode(signer.sign())}"}"""
            .toByteArray()
        val file = File.createTempFile("relaxkonos-android-release-", ".zip")
        ZipOutputStream(file.outputStream()).use { zip ->
            listOf("manifest.json" to manifest, "manifest.json.sig" to signature, path to payload).forEach { (name, bytes) ->
                zip.putNextEntry(ZipEntry(name))
                zip.write(bytes)
                zip.closeEntry()
            }
        }
        val publicKey = "-----BEGIN PUBLIC KEY-----\n${Base64Codec.encode(pair.public.encoded)}\n-----END PUBLIC KEY-----"
        return SignedRelease(file, keyId, publicKey)
    }

    private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
        .digest(bytes).joinToString("") { "%02x".format(it) }

    private data class SignedRelease(val file: File, val keyId: String, val publicKeyPem: String)
}

private class FakeDeploymentTransport : ServerCenterSshTransport {
    override val isConnected: Boolean = true
    override val observedHostKey: ServerCenterHostKeyObservation? = null
    val commands = mutableListOf<String>()
    val uploaded = linkedMapOf<String, ByteArray>()
    val uploadOrder = mutableListOf<String>()

    override suspend fun connect(
        endpoint: ServerCenterSshEndpoint,
        credential: SshCredential,
        hostKeyGuard: (ServerCenterHostKeyObservation) -> ServerHostKeyTrust,
    ) = error("not used")

    override suspend fun run(command: String): ServerCenterSshCommandResult {
        commands += command
        return when {
            command.contains("mktemp") -> ServerCenterSshCommandResult(
                0, "/tmp/relaxkonos-deploy.abcdefgh\n", "",
            )
            command.contains(" --query ") -> {
                val operationId = command.substringAfterLast(' ')
                ServerCenterSshCommandResult(
                    0,
                    """{"schemaVersion":1,"operationId":"$operationId","installationId":null,"kind":"probe","phase":"failed","state":"failed","sequence":1,"timestampUtc":"2026-09-25T00:00:00Z","progress":null,"problemCode":"server-deployment.failed","safeMessage":"failed","cancellable":false,"startedAtUtc":null,"completedAtUtc":null,"result":null,"snapshot":null,"probe":null}""",
                    "",
                )
            }
            else -> ServerCenterSshCommandResult(0, "", "")
        }
    }

    override suspend fun runWithInput(command: String, inputLine: String?): ServerCenterSshCommandResult =
        error("not used")

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

    override fun openLoopbackTunnel(remotePort: Int, basePath: String?): ServerCenterSshTunnel = error("not used")

    override fun close() = Unit
}
