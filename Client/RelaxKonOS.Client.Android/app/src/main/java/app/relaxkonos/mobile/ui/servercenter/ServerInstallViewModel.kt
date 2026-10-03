package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.*
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.KeyFactory
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.spec.PKCS8EncodedKeySpec
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.bouncycastle.asn1.DERNull
import org.bouncycastle.asn1.pkcs.PKCSObjectIdentifiers
import org.bouncycastle.asn1.pkcs.PrivateKeyInfo
import org.bouncycastle.asn1.pkcs.RSAPrivateKey
import org.bouncycastle.asn1.x509.AlgorithmIdentifier

internal data class ServerInstallState(val busy: Boolean = false, val message: Int? = null,
    val installed: Boolean = false, val transfer: app.relaxkonos.mobile.ui.common.TransferProgress? = null)

internal data class ServerInstallSelection(
    val hostId: String, val source: String, val bundle: Uri?, val remotePath: String,
    val mode: String, val network: String, val fileAccess: String,
    val certificateMode: String, val certificateFormat: String,
    val certificate: Uri?, val privateKey: Uri?, val password: String, val identities: String,
    val sudoPassword: String,
    val advanced: ServerInstallAdvancedOptions = ServerInstallAdvancedOptions(),
)

internal data class ServerInstallAdvancedOptions(
    val serverPort: String = "5000",
    val packageUri: String = "",
    val packageDigest: String = "",
    val releaseCatalogBaseUri: String = "",
    val installRoot: String = "",
    val dataRoot: String = "",
    val configRoot: String = "",
    val stateRoot: String = "",
    val cacheRoot: String = "",
    val fileRoots: String = "",
    val administratorFileAccess: String = "restricted",
    val administratorFileRoots: String = "",
    val rootFileAccess: String = "restricted",
    val rootFileRoots: String = "",
    val dockerAccess: Boolean = false,
    val allowUnsupportedSystem: Boolean = false,
) : java.io.Serializable

internal class ServerInstallViewModel(application: Application) : AndroidViewModel(application) {
    private val container = (application as RelaxKonApplication).container
    private val mutableState = MutableStateFlow(ServerInstallState())
    val state = mutableState.asStateFlow()

    fun install(selection: ServerInstallSelection) {
        if (mutableState.value.busy || mutableState.value.installed) return
        mutableState.value = ServerInstallState(true, R.string.server_progress_connecting)
        viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(selection.hostId)
            if (secret == null) {
                mutableState.value = ServerInstallState(message = R.string.ssh_workspace_deploy_verify)
                return@launch
            }
            try {
                withContext(Dispatchers.IO) { execute(selection, secret) }
                mutableState.value = ServerInstallState(message = R.string.ssh_workspace_deploy_success, installed = true)
            } catch (cancelled: CancellationException) {
                mutableState.value = ServerInstallState(message = R.string.ssh_workspace_deploy_failed)
                throw cancelled
            } catch (_: ServerInstallSudoException) {
                mutableState.value = ServerInstallState(message = R.string.ssh_workspace_deploy_sudo_failed)
            } catch (_: Exception) {
                mutableState.value = ServerInstallState(message = R.string.ssh_workspace_deploy_failed)
            } finally { secret.fill('\u0000') }
        }
    }

    private fun progress(message: Int) {
        mutableState.value = mutableState.value.copy(message = message, transfer = null)
    }

    private suspend fun execute(selection: ServerInstallSelection, secret: CharArray) {
        val credential = SshCredential(SshCredentialKind.Password, secret, null)
        var localZip: File? = null
        try {
            container.serverCenterConnections.connect(selection.hostId, credential, System.currentTimeMillis()).use { session ->
                val key = requireNotNull(session.observedHostKey)
                val transport = session.sshTransport
                val platform = if (transport.run("uname -s").let { it.succeeded && it.standardOutput.trim() == "Linux" })
                    ServerHostPlatform.Linux else ServerHostPlatform.Windows
                val launcher = ServerCenterUploadAsset.launcher(getApplication<Application>().assets, platform)
                val client = ServerCenterDeploymentClient(transport)
                suspend fun read(kind: ServerDeploymentKind, options: ServerDeploymentOptions? = null, sudoPassword: String? = null): ServerDeploymentOperation {
                    val staged = client.stage(ServerDeploymentRequest(ServerDeploymentProtocol.VERSION,
                        UUID.randomUUID().toString(), kind, options), platform, launcher)
                    return client.execute(staged, sudoPassword)
                }
                progress(R.string.server_progress_checking)
                var probe = requireNotNull(read(ServerDeploymentKind.Probe).probe)
                check(probe.osSupported || platform == ServerHostPlatform.Linux && selection.advanced.allowUnsupportedSystem)
                val runtime = requireNotNull(probe.runtimeIdentifier)
                val mode = when (selection.mode) {
                    "linuxUser" -> ServerInstallMode.LinuxUser
                    "linuxSystem" -> ServerInstallMode.LinuxSystem
                    "windowsSystem" -> ServerInstallMode.WindowsSystem
                    else -> if (platform == ServerHostPlatform.Windows) ServerInstallMode.WindowsSystem
                        else if (probe.elevated) ServerInstallMode.LinuxSystem else ServerInstallMode.LinuxUser
                }
                check(mode == ServerInstallMode.LinuxUser || probe.elevated || mode == ServerInstallMode.LinuxSystem && probe.sudoAvailable)
                check(mode != ServerInstallMode.LinuxUser || !probe.elevated && selection.certificateMode != "custom")
                val sudoPassword = if (mode == ServerInstallMode.LinuxSystem && !probe.elevated)
                    selection.sudoPassword.ifEmpty { String(secret) } else null
                if (sudoPassword != null) {
                    val elevatedProbe = read(ServerDeploymentKind.Probe, sudoPassword = sudoPassword)
                    if (elevatedProbe.state != ServerDeploymentState.Succeeded || elevatedProbe.probe == null)
                        throw ServerInstallSudoException()
                    probe = elevatedProbe.probe
                }
                val source = when (selection.source) {
                    "local" -> ServerPackageSourceKind.LocalBundle
                    "remote" -> ServerPackageSourceKind.RemoteBundle
                    "url" -> ServerPackageSourceKind.DirectUrl
                    else -> ServerPackageSourceKind.OfficialStable
                }
                progress(R.string.server_progress_preparing)
                if (source == ServerPackageSourceKind.LocalBundle) {
                    localZip = File.createTempFile("server-install-", ".zip", getApplication<Application>().cacheDir)
                    getApplication<Application>().contentResolver.openInputStream(requireNotNull(selection.bundle)).use { input ->
                        requireNotNull(input).use { data -> localZip!!.outputStream().use { data.copyTo(it) } }
                    }
                }
                val certificate = if (selection.certificateMode == "custom") certificateAsset(selection) else null
                val operationId = UUID.randomUUID().toString()
                val kind = if (probe.existingInstalled) ServerDeploymentKind.Upgrade else ServerDeploymentKind.Install
                if (probe.existingInstalled) check(ServerInstallationId.isValid(probe.existingInstallationId))
                val options = ServerDeploymentOptions(source,
                    if (selection.network == "lan") ServerNetworkProfile.Lan else ServerNetworkProfile.Loopback,
                    mode = mode, stagedPackageName = if (localZip != null) "server.zip" else null,
                    remotePackagePath = if (source == ServerPackageSourceKind.RemoteBundle) selection.remotePath else null,
                    expectedInstallationId = if (probe.existingInstalled) probe.existingInstallationId else null,
                    serverPort = selection.advanced.serverPort.toInt(),
                    packageUri = selection.advanced.packageUri.takeIf { source == ServerPackageSourceKind.DirectUrl },
                    packageDigest = selection.advanced.packageDigest.takeIf { source == ServerPackageSourceKind.DirectUrl },
                    language = when (java.util.Locale.getDefault().language) { "zh" -> "zh-CN"; "ja" -> "ja-JP"; else -> "en-US" },
                    releaseCatalogBaseUri = selection.advanced.releaseCatalogBaseUri.trim().takeIf { it.isNotEmpty() },
                    installRoot = selection.advanced.installRoot.trim().takeIf { it.isNotEmpty() && mode != ServerInstallMode.LinuxUser },
                    dataRoot = selection.advanced.dataRoot.trim().takeIf { it.isNotEmpty() },
                    configRoot = selection.advanced.configRoot.trim().takeIf { it.isNotEmpty() && mode == ServerInstallMode.LinuxUser },
                    stateRoot = selection.advanced.stateRoot.trim().takeIf { it.isNotEmpty() && mode == ServerInstallMode.LinuxUser },
                    cacheRoot = selection.advanced.cacheRoot.trim().takeIf { it.isNotEmpty() && mode == ServerInstallMode.LinuxUser },
                    fileRoots = selection.advanced.fileRoots.lines().map(String::trim).filter(String::isNotEmpty).takeIf { selection.fileAccess == "whitelist" && mode != ServerInstallMode.LinuxUser },
                    administratorFileAccess = selection.advanced.administratorFileAccess.takeIf { mode == ServerInstallMode.LinuxSystem },
                    administratorFileRoots = selection.advanced.administratorFileRoots.lines().map(String::trim).filter(String::isNotEmpty).takeIf { selection.advanced.administratorFileAccess == "whitelist" && mode == ServerInstallMode.LinuxSystem },
                    rootFileAccess = selection.advanced.rootFileAccess.takeIf { mode == ServerInstallMode.LinuxSystem },
                    rootFileRoots = selection.advanced.rootFileRoots.lines().map(String::trim).filter(String::isNotEmpty).takeIf { selection.advanced.rootFileAccess == "whitelist" && mode == ServerInstallMode.LinuxSystem },
                    dockerAccess = selection.advanced.dockerAccess && mode == ServerInstallMode.LinuxSystem,
                    allowUnsupportedSystem = selection.advanced.allowUnsupportedSystem && platform == ServerHostPlatform.Linux,
                    fileAccess = if (mode == ServerInstallMode.LinuxUser) "restricted" else selection.fileAccess, certificateMode = selection.certificateMode,
                    selfSignedIdentities = selection.identities.takeIf { selection.certificateMode == "selfSigned" }, confirmed = true)
                val staged = client.stage(ServerDeploymentRequest(ServerDeploymentProtocol.VERSION, operationId, kind, options),
                    platform, launcher, localZip, runtime, certificate, selection.password,
                    uploadProgress = { fraction ->
                        val length = localZip?.length() ?: 0L
                        mutableState.value = mutableState.value.copy(transfer = app.relaxkonos.mobile.ui.common.TransferProgress(
                            "server.zip", (fraction * length).toLong(), length))
                    })
                val index = container.serverInstallOperations
                val reference = index.record(session.target, key, operationId, platform)
                progress(if (kind == ServerDeploymentKind.Upgrade) R.string.server_progress_upgrading else R.string.server_progress_installing)
                val receipt = client.execute(staged, sudoPassword) { transfer ->
                    mutableState.value = mutableState.value.copy(
                        message = if (transfer != null) R.string.files_downloading else if (kind == ServerDeploymentKind.Upgrade)
                            R.string.server_progress_upgrading else R.string.server_progress_installing,
                        transfer = transfer?.let { app.relaxkonos.mobile.ui.common.TransferProgress("server.zip", it.bytes, it.total) },
                    )
                }
                index.markVerified(session.target, reference, key, System.currentTimeMillis())
                if (receipt.problemCode == "server-deployment.elevation_required") throw ServerInstallSudoException()
                check(receipt.state == ServerDeploymentState.Succeeded)
                progress(R.string.server_progress_verifying)
                val status = requireNotNull(read(ServerDeploymentKind.Status,
                    ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, mode = mode), sudoPassword).snapshot)
                container.serverCenter.recordVerifiedSnapshot(selection.hostId, status)
                check(status.installed && status.healthy)
            }
        } finally { localZip?.delete(); credential.clear() }
    }

    private fun certificateAsset(selection: ServerInstallSelection): ServerCenterUploadAsset {
        val resolver = getApplication<Application>().contentResolver
        fun bytes(uri: Uri): ByteArray = resolver.openInputStream(uri).use { input ->
            val stream = requireNotNull(input)
            val output = ByteArrayOutputStream()
            val buffer = ByteArray(8192)
            while (true) {
                val count = stream.read(buffer)
                if (count < 0) break
                require(output.size() + count <= 4 * 1024 * 1024) { "Certificate file is too large." }
                output.write(buffer, 0, count)
            }
            output.toByteArray()
        }
        val certificateBytes = bytes(requireNotNull(selection.certificate))
        if (selection.certificateFormat != "pem") return ServerCenterUploadAsset.bytes(certificateBytes)
        val certificates = CertificateFactory.getInstance("X.509").generateCertificates(certificateBytes.inputStream()).toTypedArray()
        val pem = bytes(requireNotNull(selection.privateKey)).toString(Charsets.UTF_8)
        val match = Regex("-----BEGIN (RSA PRIVATE KEY|PRIVATE KEY)-----([\\s\\S]*?)-----END \\1-----").find(pem)
            ?: error("Choose an unencrypted PKCS#8 or RSA PEM private key.")
        var encoded = Base64.getMimeDecoder().decode(match.groupValues[2])
        if (match.groupValues[1] == "RSA PRIVATE KEY") encoded = PrivateKeyInfo(
            AlgorithmIdentifier(PKCSObjectIdentifiers.rsaEncryption, DERNull.INSTANCE), RSAPrivateKey.getInstance(encoded)).encoded
        val key = listOf("RSA", "EC", "Ed25519").firstNotNullOfOrNull { algorithm ->
            runCatching { KeyFactory.getInstance(algorithm).generatePrivate(PKCS8EncodedKeySpec(encoded)) }.getOrNull()
        } ?: error("Unsupported private key.")
        val password = selection.password.toCharArray()
        try {
            val store = KeyStore.getInstance("PKCS12").apply { load(null, password); setKeyEntry("server", key, password, certificates) }
            return ServerCenterUploadAsset.bytes(ByteArrayOutputStream().apply { store.store(this, password) }.toByteArray())
        } finally { password.fill('\u0000'); encoded.fill(0) }
    }
}

private class ServerInstallSudoException : Exception()
