package app.relaxkonos.mobile.servercenter

import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.io.InputStream

enum class ServerHostPlatform { Linux, Windows }

/** A source that can be reopened for validation and SFTP upload. */
class ServerCenterUploadAsset(
    val contentLength: Long?,
    private val openStream: () -> InputStream,
) {
    fun open(): InputStream = openStream()

    companion object {
        fun bytes(value: ByteArray): ServerCenterUploadAsset =
            ServerCenterUploadAsset(value.size.toLong()) { ByteArrayInputStream(value) }

        fun file(value: File): ServerCenterUploadAsset =
            ServerCenterUploadAsset(value.length()) { value.inputStream() }
    }
}

/** The remote staging identity retained until the authoritative operation receipt is read. */
data class ServerCenterStagedOperation(
    val operationId: String,
    val platform: ServerHostPlatform,
    val remoteDirectory: String,
)

/**
 * Android deployment operation layer. It binds local signed-release verification to the built-in
 * SSH/SFTP transport and invokes only the fixed launcher actions.
 */
class ServerCenterDeploymentClient(private val transport: ServerCenterSshTransport) {

    suspend fun stage(
        request: ServerDeploymentRequest,
        platform: ServerHostPlatform,
        launcher: ServerCenterUploadAsset,
        verifier: ServerCenterUploadAsset,
        signedArchive: File? = null,
        expectedRuntime: ServerRuntimeIdentifier? = null,
        trustedKeyId: String? = null,
        trustedPublicKeyPem: String? = null,
        uploadProgress: ((Double) -> Unit)? = null,
    ): ServerCenterStagedOperation {
        check(transport.isConnected) { "A trusted SSH session is required." }
        val requestBytes = ServerDeploymentWire.writeRequest(request)
        val needsArchive = request.kind == ServerDeploymentKind.Install || request.kind == ServerDeploymentKind.Upgrade
        if (needsArchive) {
            val options = requireNotNull(request.options) { "Deployment options are required." }
            val archive = requireNotNull(signedArchive) { "A signed release is required." }
            val runtime = requireNotNull(expectedRuntime) { "The expected release RID is required." }
            val keyId = requireNotNull(trustedKeyId?.takeIf { it.isNotBlank() }) { "A trusted key ID is required." }
            val publicKey = requireNotNull(trustedPublicKeyPem?.takeIf { it.isNotBlank() }) {
                "A trusted public key is required."
            }
            require(archive.isFile && TRUSTED_KEY_ID.matches(keyId) &&
                ServerDeploymentInputRules.isSafeStagedPackageName(options.stagedPackageName) &&
                ServerDeploymentInputRules.isSha256(options.packageDigest)) {
                "A signed, staged release and a trusted key are required."
            }
            require(modeMatchesPlatform(options.mode, platform)) { "Installation mode does not match the host platform." }
            require(runtimeMatchesPlatform(runtime, platform)) { "Release RID does not match the host platform." }

            val expectedKind = if (options.mode == ServerInstallMode.LinuxUser) {
                ServerReleasePackageKind.UserServer
            } else ServerReleasePackageKind.Server
            val checkedRelease = ServerReleaseArchiveVerifier.verify(
                archiveFile = archive,
                expectedKind = expectedKind,
                expectedRuntime = runtime,
                trustedPublicKeys = mapOf(keyId to publicKey),
                expectedArchiveSha256 = options.packageDigest,
            )
            if (!checkedRelease.verified) {
                throw IOException("${checkedRelease.problemCode}: release verification failed before upload.")
            }
        }

        val directory = createPrivateDirectory(platform)
        val staged = ServerCenterStagedOperation(request.operationId.lowercase(), platform, directory)
        upload(verifier, staged, if (platform == ServerHostPlatform.Windows) "release-verifier.exe" else "release-verifier")
        upload(launcher, staged, if (platform == ServerHostPlatform.Windows) "RelaxKonOS-Deploy.ps1" else "relaxkonos-deploy.sh")
        if (needsArchive) {
            upload(
                ServerCenterUploadAsset.bytes(trustedPublicKeyPem!!.toByteArray(Charsets.UTF_8)),
                staged,
                "release-public.pem",
            )
            upload(
                ServerCenterUploadAsset.bytes(trustedKeyId!!.toByteArray(Charsets.US_ASCII)),
                staged,
                "release-key-id.txt",
            )
            upload(
                ServerCenterUploadAsset.file(signedArchive!!),
                staged,
                request.options!!.stagedPackageName!!,
                uploadProgress,
            )
        }
        upload(ServerCenterUploadAsset.bytes(requestBytes), staged, "request.json")
        if (platform == ServerHostPlatform.Linux) {
            val chmod = transport.run(
                "chmod 700 '$directory/release-verifier' '$directory/relaxkonos-deploy.sh'",
            )
            if (!chmod.succeeded) throw IOException("Unable to mark the staged deployment tools executable.")
        }
        return staged
    }

    suspend fun execute(staged: ServerCenterStagedOperation): ServerDeploymentOperation {
        transport.run(launcherCommand(staged, query = false))
        // SSH exit status is not proof of success. The persistent receipt is authoritative.
        return query(staged)
    }

    suspend fun query(staged: ServerCenterStagedOperation): ServerDeploymentOperation {
        val result = transport.run(launcherCommand(staged, query = true))
        if (!result.succeeded) throw IOException("The remote operation receipt could not be read.")
        return ServerDeploymentWire.readOperation(result.standardOutput.trim(), staged.operationId)
    }

    private suspend fun createPrivateDirectory(platform: ServerHostPlatform): String {
        val command = when (platform) {
            ServerHostPlatform.Linux -> "umask 077; mktemp -d -p /tmp relaxkonos-deploy.XXXXXXXX"
            ServerHostPlatform.Windows -> {
                val script = "\$p=Join-Path \$env:TEMP ('relaxkonos-deploy-'+[guid]::NewGuid().ToString('N'));" +
                    "[IO.Directory]::CreateDirectory(\$p)|Out-Null;" +
                    "\$u=[Security.Principal.WindowsIdentity]::GetCurrent().Name;" +
                    "& icacls \$p /inheritance:r /grant:r (\$u+':(OI)(CI)F') 'SYSTEM:(OI)(CI)F' " +
                    "'Administrators:(OI)(CI)F' *> \$null;if(\$LASTEXITCODE -ne 0){exit 1};" +
                    "[Console]::WriteLine(\$p)"
                "powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
                    Base64Codec.encode(script.toByteArray(Charsets.UTF_16LE))
            }
        }
        val result = transport.run(command)
        if (!result.succeeded) throw IOException("The remote private staging directory could not be created.")
        val path = result.standardOutput.trim()
        val valid = '\n' !in path && '\r' !in path && when (platform) {
            ServerHostPlatform.Linux -> LINUX_STAGING_PATH.matches(path)
            ServerHostPlatform.Windows -> WINDOWS_STAGING_PATH.matches(path)
        }
        if (!valid) throw IOException("The remote staging path is invalid.")
        return path
    }

    private suspend fun upload(
        source: ServerCenterUploadAsset,
        staged: ServerCenterStagedOperation,
        fileName: String,
        progress: ((Double) -> Unit)? = null,
    ) {
        source.open().use { content ->
            transport.upload(content, source.contentLength, remotePath(staged, fileName), progress)
        }
    }

    private fun launcherCommand(staged: ServerCenterStagedOperation, query: Boolean): String {
        if (staged.platform == ServerHostPlatform.Linux) {
            require(LINUX_STAGING_PATH.matches(staged.remoteDirectory)) { "Invalid staging directory." }
            val action = if (query) " --query ${staged.operationId}" else " --run"
            return "bash '${staged.remoteDirectory}/relaxkonos-deploy.sh'$action"
        }
        require(WINDOWS_STAGING_PATH.matches(staged.remoteDirectory)) { "Invalid staging directory." }
        val scriptPath = staged.remoteDirectory.trimEnd('\\', '/') + "\\RelaxKonOS-Deploy.ps1"
        val command = "& '${scriptPath.replace("'", "''")}'" +
            if (query) " -QueryOperationId '${staged.operationId}'" else ""
        return "powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
            Base64Codec.encode(command.toByteArray(Charsets.UTF_16LE))
    }

    private fun remotePath(staged: ServerCenterStagedOperation, fileName: String): String =
        staged.remoteDirectory.trimEnd('/', '\\').replace('\\', '/') + "/" + fileName

    private fun modeMatchesPlatform(mode: ServerInstallMode?, platform: ServerHostPlatform): Boolean = when (platform) {
        ServerHostPlatform.Linux -> mode == ServerInstallMode.LinuxSystem || mode == ServerInstallMode.LinuxUser
        ServerHostPlatform.Windows -> mode == ServerInstallMode.WindowsSystem
    }

    private fun runtimeMatchesPlatform(runtime: ServerRuntimeIdentifier, platform: ServerHostPlatform): Boolean =
        when (platform) {
            ServerHostPlatform.Linux -> runtime == ServerRuntimeIdentifier.LinuxX64 ||
                runtime == ServerRuntimeIdentifier.LinuxArm64
            ServerHostPlatform.Windows -> runtime == ServerRuntimeIdentifier.WinX64 ||
                runtime == ServerRuntimeIdentifier.WinArm64
        }

    private companion object {
        val TRUSTED_KEY_ID = Regex("^[A-Za-z0-9._-]{1,128}$")
        val LINUX_STAGING_PATH = Regex("^/tmp/relaxkonos-deploy\\.[A-Za-z0-9]{8,32}$")
        val WINDOWS_STAGING_PATH = Regex("^[A-Za-z]:[\\\\/].*[\\\\/]relaxkonos-deploy-[0-9a-f]{32}$")
    }
}
