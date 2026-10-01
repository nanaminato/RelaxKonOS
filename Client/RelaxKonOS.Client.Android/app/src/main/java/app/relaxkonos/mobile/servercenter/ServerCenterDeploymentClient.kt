package app.relaxkonos.mobile.servercenter

import android.content.res.AssetManager
import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.util.Locale
import java.util.UUID

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

        fun launcher(assets: AssetManager, platform: ServerHostPlatform): ServerCenterUploadAsset {
            val name = if (platform == ServerHostPlatform.Windows) "RelaxKonOS-Deploy.ps1" else "relaxkonos-deploy.sh"
            return ServerCenterUploadAsset(null) {
                ByteArrayInputStream(assets.open(name).bufferedReader().use { it.readText() }.replace("\r\n", "\n").toByteArray())
            }
        }
    }
}

/** The remote staging identity retained until the authoritative operation receipt is read. */
data class ServerCenterStagedOperation(
    val operationId: String,
    val platform: ServerHostPlatform,
    val remoteDirectory: String,
)

/** A client-controlled launcher staged for receipt lookup only, without a deployment request. */
data class ServerCenterStagedLookup(val platform: ServerHostPlatform, val remoteDirectory: String)

class ServerDeploymentReceiptMissingException : IOException("The remote operation receipt does not exist.")

/**
 * Android deployment operation layer. It binds local release validation to the built-in
 * SSH/SFTP transport and invokes only the fixed launcher actions.
 */
class ServerCenterDeploymentClient(private val transport: ServerCenterSshTransport) {

    suspend fun stage(
        request: ServerDeploymentRequest,
        platform: ServerHostPlatform,
        launcher: ServerCenterUploadAsset,
        archiveFile: File? = null,
        expectedRuntime: ServerRuntimeIdentifier? = null,
        certificate: ServerCenterUploadAsset? = null,
        certificatePassword: String? = null,
        uploadProgress: ((Double) -> Unit)? = null,
    ): ServerCenterStagedOperation {
        check(transport.isConnected) { "A trusted SSH session is required." }
        val requestBytes = ServerDeploymentWire.writeRequest(request)
        val installing = request.kind == ServerDeploymentKind.Install || request.kind == ServerDeploymentKind.Upgrade
        val needsArchive = installing && request.options?.source == ServerPackageSourceKind.LocalBundle
        if (installing) {
            val options = requireNotNull(request.options) { "Deployment options are required." }
            val runtime = requireNotNull(expectedRuntime) { "The target RID is required." }
            require(modeMatchesPlatform(options.mode, platform)) { "Installation mode does not match the host platform." }
            require(runtimeMatchesPlatform(runtime, platform)) { "Runtime does not match the host platform." }
            if (needsArchive) require(archiveFile?.isFile == true &&
                ServerDeploymentInputRules.isSafeStagedPackageName(options.stagedPackageName)) { "A local ZIP is required." }
            if (options.source == ServerPackageSourceKind.RemoteBundle) require(
                options.remotePackagePath?.endsWith(".zip", ignoreCase = true) == true &&
                options.remotePackagePath.none { it.code < 32 }) { "A server ZIP path is required." }
            require(options.source != ServerPackageSourceKind.DirectUrl) { "Choose the official release or a ZIP file." }
            if (options.certificateMode == "custom") requireNotNull(certificate) { "A certificate is required." }
        }

        val directory = createPrivateDirectory(platform)
        val staged = ServerCenterStagedOperation(request.operationId.lowercase(), platform, directory)
        upload(launcher, staged, if (platform == ServerHostPlatform.Windows) "RelaxKonOS-Deploy.ps1" else "relaxkonos-deploy.sh")
        if (needsArchive) {
            upload(
                ServerCenterUploadAsset.file(archiveFile!!),
                staged,
                request.options!!.stagedPackageName!!,
                uploadProgress,
            )
        }
        if (installing && request.options?.certificateMode == "custom") {
            upload(certificate!!, staged, "certificate.pfx")
            upload(ServerCenterUploadAsset.bytes((certificatePassword ?: "").toByteArray()), staged, "certificate-password.txt")
        }
        upload(ServerCenterUploadAsset.bytes(requestBytes), staged, "request.json")
        if (platform == ServerHostPlatform.Linux) {
            val chmod = transport.run(
                "chmod 700 '$directory/relaxkonos-deploy.sh'",
            )
            if (!chmod.succeeded) throw IOException("Unable to mark the staged deployment tools executable.")
        }
        return staged
    }

    suspend fun execute(staged: ServerCenterStagedOperation, sudoPassword: String? = null): ServerDeploymentOperation {
        val command = launcherCommand(staged, action = LauncherAction.Run)
        if (sudoPassword != null) {
            require(staged.platform == ServerHostPlatform.Linux && sudoPassword.none { it == '\r' || it == '\n' })
            transport.runWithInput(command + "-with-sudo", sudoPassword)
        } else transport.run(command)
        // SSH exit status is not proof of success. The persistent receipt is authoritative.
        return query(staged)
    }

    /** Stages the fixed launcher without a request, package or verifier; lookup never enters --run. */
    suspend fun stageLookup(
        platform: ServerHostPlatform,
        launcher: ServerCenterUploadAsset,
    ): ServerCenterStagedLookup {
        check(transport.isConnected) { "A trusted SSH session is required." }
        val directory = createPrivateDirectory(platform)
        val staged = ServerCenterStagedOperation(UUID(0, 0).toString(), platform, directory)
        upload(launcher, staged, if (platform == ServerHostPlatform.Windows) "RelaxKonOS-Deploy.ps1" else "relaxkonos-deploy.sh")
        if (platform == ServerHostPlatform.Linux) {
            val chmod = transport.run("chmod 700 '$directory/relaxkonos-deploy.sh'")
            if (!chmod.succeeded) throw IOException("Unable to mark the staged deployment tools executable.")
        }
        return ServerCenterStagedLookup(platform, directory)
    }

    suspend fun list(lookup: ServerCenterStagedLookup): List<String> {
        val staged = ServerCenterStagedOperation(UUID(0, 0).toString(), lookup.platform, lookup.remoteDirectory)
        val result = transport.run(launcherCommand(staged, action = LauncherAction.List))
        if (!result.succeeded) throw IOException("The remote operation list could not be read.")
        if (result.standardOutput.length > 4096) throw IOException("The remote operation list is too large.")
        val lines = result.standardOutput.lineSequence().filter(String::isNotBlank).toList()
        if (lines.size > 20 || lines.any { !OPERATION_FILE.matches(it) || !validOperationId(it.removeSuffix(".json")) }) {
            throw IOException("The remote operation list is invalid.")
        }
        return lines.map { it.removeSuffix(".json").lowercase(Locale.ROOT) }.distinct()
    }

    suspend fun query(lookup: ServerCenterStagedLookup, operationId: String): ServerDeploymentOperation {
        require(validOperationId(operationId)) { "A canonical operation id is required." }
        return query(ServerCenterStagedOperation(operationId.lowercase(Locale.ROOT), lookup.platform, lookup.remoteDirectory))
    }

    suspend fun diagnostics(lookup: ServerCenterStagedLookup, operationId: String): String {
        require(validOperationId(operationId))
        val staged = ServerCenterStagedOperation(operationId.lowercase(Locale.ROOT), lookup.platform, lookup.remoteDirectory)
        val result = transport.run(launcherCommand(staged, LauncherAction.Diagnostics))
        if (!result.succeeded) throw IOException("The remote deployment log could not be read.")
        return result.standardOutput.take(65536).replace(
            Regex("(?im)(password|secret|token|authorization)(\\s*[:=]\\s*)[^\\r\\n]+"), "$1$2[redacted]")
    }

    suspend fun clearOperation(lookup: ServerCenterStagedLookup, operationId: String) {
        require(validOperationId(operationId))
        val staged = ServerCenterStagedOperation(operationId.lowercase(Locale.ROOT), lookup.platform, lookup.remoteDirectory)
        if (!transport.run(launcherCommand(staged, LauncherAction.Clear)).succeeded) {
            throw IOException("The remote operation could not be cleared.")
        }
    }

    suspend fun query(staged: ServerCenterStagedOperation): ServerDeploymentOperation {
        val result = transport.run(launcherCommand(staged, action = LauncherAction.Query))
        if (result.exitStatus == 66) throw ServerDeploymentReceiptMissingException()
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

    private fun launcherCommand(staged: ServerCenterStagedOperation, action: LauncherAction): String {
        require(OPERATION_ID.matches(staged.operationId)) { "Invalid operation id." }
        if (staged.platform == ServerHostPlatform.Linux) {
            require(LINUX_STAGING_PATH.matches(staged.remoteDirectory)) { "Invalid staging directory." }
            val argument = when (action) {
                LauncherAction.Run -> " --run"
                LauncherAction.Query -> " --query ${staged.operationId}"
                LauncherAction.List -> " --list"
                LauncherAction.Diagnostics -> " --diagnostics ${staged.operationId}"
                LauncherAction.Clear -> " --clear-operation ${staged.operationId}"
            }
            return "bash '${staged.remoteDirectory}/relaxkonos-deploy.sh'$argument"
        }
        require(WINDOWS_STAGING_PATH.matches(staged.remoteDirectory)) { "Invalid staging directory." }
        val scriptPath = staged.remoteDirectory.trimEnd('\\', '/') + "\\RelaxKonOS-Deploy.ps1"
        val command = "& '${scriptPath.replace("'", "''")}'" +
            when (action) {
                LauncherAction.Run -> ""
                LauncherAction.Query -> " -QueryOperationId '${staged.operationId}'"
                LauncherAction.List -> " -ListOperations"
                LauncherAction.Diagnostics -> " -DiagnosticsOperationId '${staged.operationId}'"
                LauncherAction.Clear -> " -ClearOperationId '${staged.operationId}'"
            }
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

    private enum class LauncherAction { Run, Query, List, Diagnostics, Clear }

    private companion object {
        val OPERATION_ID = Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
        val OPERATION_FILE = Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\.json$")
        fun validOperationId(value: String): Boolean = OPERATION_ID.matches(value) && UUID.fromString(value) != UUID(0, 0)
        val LINUX_STAGING_PATH = Regex("^/tmp/relaxkonos-deploy\\.[A-Za-z0-9]{8,32}$")
        val WINDOWS_STAGING_PATH = Regex("^[A-Za-z]:[\\\\/].*[\\\\/]relaxkonos-deploy-[0-9a-f]{32}$")
    }
}
