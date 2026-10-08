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
import kotlinx.coroutines.ensureActive
import org.bouncycastle.asn1.DERNull
import org.bouncycastle.asn1.pkcs.PKCSObjectIdentifiers
import org.bouncycastle.asn1.pkcs.PrivateKeyInfo
import org.bouncycastle.asn1.pkcs.RSAPrivateKey
import org.bouncycastle.asn1.x509.AlgorithmIdentifier

internal data class ServerInstallState(val busy: Boolean = false, val message: Int? = null,
    val installed: Boolean = false, val transfer: app.relaxkonos.mobile.ui.common.TransferProgress? = null,
    val uncertain: Boolean = false, val needsVerification: Boolean = false) {
    val canSubmit: Boolean get() = !busy && !installed && !uncertain
}

/** Tracks the authoritative receipt separately from the subsequent health check. */
internal class ServerInstallAttempt {
    var started = false
    var receiptState: ServerDeploymentState? = null
    fun failure(sudoRejected: Boolean = false): ServerInstallState = when {
        receiptState == ServerDeploymentState.Succeeded -> ServerInstallState(
            message = R.string.server_install_status_unverified, installed = true, needsVerification = true)
        started && (receiptState == null || receiptState == ServerDeploymentState.Queued || receiptState == ServerDeploymentState.Running) ->
            ServerInstallState(message = R.string.server_install_result_unknown, uncertain = true, needsVerification = true)
        else -> ServerInstallState(message = if (sudoRejected) R.string.ssh_workspace_deploy_sudo_failed else R.string.ssh_workspace_deploy_failed)
    }
}

private fun installationReceiptMatchesMode(
    receipt: ServerDeploymentOperation,
    mode: ServerInstallMode,
): Boolean {
    val result = receipt.result ?: return false
    return receipt.state == ServerDeploymentState.Succeeded &&
        receipt.kind in setOf(ServerDeploymentKind.Install, ServerDeploymentKind.Upgrade) &&
        result.healthy && ServerInstallationId.isValid(result.installationId) &&
        (receipt.installationId == null || receipt.installationId == result.installationId) &&
        result.mode == mode
}

internal fun installationSnapshotMatchesReceipt(receipt: ServerDeploymentOperation, snapshot: ServerHostSnapshot,
    mode: ServerInstallMode): Boolean = installationReceiptMatchesMode(receipt, mode) &&
    snapshot.mode == mode && snapshot.installationId == receipt.result?.installationId && snapshot.installed && snapshot.healthy

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
    val addFirewallRule: Boolean = false,
    val allowUnsupportedSystem: Boolean = false,
) : java.io.Serializable

internal class ServerInstallViewModel(application: Application) : AndroidViewModel(application) {
    private val container = (application as RelaxKonApplication).container
    private val mutableState = MutableStateFlow(ServerInstallState())
    val state = mutableState.asStateFlow()
    private val pendingStore = container.pendingServerInstalls
    private var pending: PendingServerInstall? = null
    private var restoredHostId: String? = null

    fun restore(hostId: String) {
        if (mutableState.value.busy) return
        val preserveSuccess = restoredHostId == hostId && mutableState.value.installed
        restoredHostId = hostId
        try {
            pending = pendingStore.read(hostId)
            pending?.takeUnless { it.attempted }?.let {
                container.serverInstallOperations.forget(it.reference)
                clearPending()
            }
            if (pending != null) mutableState.value = ServerInstallState(
                message = R.string.server_install_result_unknown, uncertain = true, needsVerification = true)
            else if (!preserveSuccess) mutableState.value = ServerInstallState()
        } catch (_: Exception) {
            pending = null
            mutableState.value = ServerInstallState(message = R.string.server_install_record_failed,
                uncertain = true, needsVerification = true)
        }
    }

    private fun failedAttempt(attempt: ServerInstallAttempt, sudoRejected: Boolean = false): ServerInstallState {
        val failure = attempt.failure(sudoRejected)
        pending?.takeUnless { it.attempted }?.let {
            try {
                container.serverInstallOperations.forget(it.reference)
                clearPending()
            } catch (_: Exception) { /* Keep the gate if durable cleanup cannot be proved. */ }
        }
        return if (pending != null && failure.canSubmit) failure.copy(
            message = R.string.server_install_record_failed, uncertain = true, needsVerification = true) else failure
    }

    private fun clearPending() {
        pending?.let(pendingStore::clear)
        pending = null
    }

    fun install(selection: ServerInstallSelection) {
        restore(selection.hostId)
        if (!mutableState.value.canSubmit) return
        pending = null
        mutableState.value = ServerInstallState(true, R.string.server_progress_connecting)
        val attempt = ServerInstallAttempt()
        viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(selection.hostId)
            if (secret == null) {
                mutableState.value = ServerInstallState(message = R.string.ssh_workspace_deploy_verify)
                return@launch
            }
            try {
                val firewallStatus = withContext(Dispatchers.IO) { execute(selection, secret, attempt) }
                mutableState.value = ServerInstallState(message = when (firewallStatus) {
                    "disabled" -> R.string.server_install_firewall_disabled
                    "ruleAdded" -> R.string.server_install_firewall_added
                    else -> R.string.ssh_workspace_deploy_success
                }, installed = true)
            } catch (cancelled: CancellationException) {
                mutableState.value = failedAttempt(attempt)
                throw cancelled
            } catch (_: ServerInstallSudoException) {
                mutableState.value = failedAttempt(attempt, sudoRejected = true)
            } catch (_: Exception) {
                mutableState.value = failedAttempt(attempt)
            } finally { secret.fill('\u0000') }
        }
    }

    /** Queries the frozen operation ID. It never invokes the installation launcher action. */
    fun verifyOriginal(sudoPassword: String = "") {
        if (pending?.attempted != true) restoredHostId?.let(::restore)
        val original = pending ?: return
        val before = mutableState.value
        if (before.busy || !before.needsVerification) return
        mutableState.value = before.copy(busy = true, message = R.string.server_progress_verifying)
        val attempt = ServerInstallAttempt().apply {
            started = true
            if (before.installed) receiptState = ServerDeploymentState.Succeeded
        }
        viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(original.reference.hostId)
            if (secret == null) {
                mutableState.value = before.copy(message = R.string.ssh_workspace_deploy_verify)
                return@launch
            }
            val credential = SshCredential(SshCredentialKind.Password, secret, null)
            try {
                val outcome = withContext(Dispatchers.IO) {
                    container.serverCenterConnections.connect(original.reference.hostId, credential, System.currentTimeMillis()).use { session ->
                        val key = requireNotNull(session.observedHostKey)
                        check(key.algorithm == original.reference.hostKeyAlgorithm && key.fingerprint == original.reference.hostKeyFingerprint)
                        val client = ServerCenterDeploymentClient(session.sshTransport)
                        val launcher = ServerCenterUploadAsset.launcher(getApplication<Application>().assets, original.reference.platform)
                        val lookup = client.stageLookup(original.reference.platform, launcher)
                        val receipt = client.query(lookup, original.reference.operationId)
                        check(receipt.kind == original.kind)
                        if (receipt.state == ServerDeploymentState.Succeeded) check(installationReceiptMatchesMode(receipt, original.mode))
                        if (before.installed) check(receipt.state == ServerDeploymentState.Succeeded)
                        attempt.receiptState = receipt.state
                        container.serverInstallOperations.markVerified(session.target, original.reference, key, System.currentTimeMillis())
                        when (receipt.state) {
                            ServerDeploymentState.Succeeded -> {
                                val statusRequest = ServerDeploymentRequest(ServerDeploymentProtocol.VERSION,
                                    UUID.randomUUID().toString(), ServerDeploymentKind.Status,
                                    ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, mode = original.mode))
                                val stagedStatus = client.stage(statusRequest, original.reference.platform, launcher)
                                val statusReceipt = client.execute(stagedStatus,
                                    if (original.needsSudo) sudoPassword.ifEmpty { String(secret) } else null)
                                check(statusReceipt.kind == ServerDeploymentKind.Status && statusReceipt.state == ServerDeploymentState.Succeeded)
                                val status = requireNotNull(statusReceipt.snapshot)
                                container.serverCenter.recordVerifiedSnapshot(original.reference.hostId, status)
                                check(installationSnapshotMatchesReceipt(receipt, status, original.mode))
                                clearPending()
                                ServerInstallState(installed = true, message = when (receipt.result?.firewallStatus) {
                                    "disabled" -> R.string.server_install_firewall_disabled
                                    "ruleAdded" -> R.string.server_install_firewall_added
                                    else -> R.string.ssh_workspace_deploy_success
                                })
                            }
                            ServerDeploymentState.Queued, ServerDeploymentState.Running -> attempt.failure()
                            else -> {
                                clearPending()
                                attempt.failure(sudoRejected = receipt.problemCode == "server-deployment.elevation_required")
                            }
                        }
                    }
                }
                mutableState.value = outcome
            } catch (cancelled: CancellationException) {
                mutableState.value = failedAttempt(attempt)
                throw cancelled
            } catch (_: Exception) {
                mutableState.value = failedAttempt(attempt)
            } finally { credential.clear(); secret.fill('\u0000') }
        }
    }

    private fun progress(message: Int) {
        mutableState.value = mutableState.value.copy(message = message, transfer = null)
    }

    private suspend fun execute(selection: ServerInstallSelection, secret: CharArray, attempt: ServerInstallAttempt): String? {
        var firewallStatus: String? = null
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
                    addFirewallRule = selection.advanced.addFirewallRule && mode != ServerInstallMode.LinuxUser && selection.network == "lan",
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
                val recovery = PendingServerInstall(reference, mode, kind, sudoPassword != null)
                pendingStore.write(recovery)
                pending = recovery
                progress(if (kind == ServerDeploymentKind.Upgrade) R.string.server_progress_upgrading else R.string.server_progress_installing)
                kotlinx.coroutines.currentCoroutineContext().ensureActive()
                pending = pendingStore.markStarted(recovery)
                attempt.started = true
                val receipt = client.execute(staged, sudoPassword) { transfer ->
                    mutableState.value = mutableState.value.copy(
                        message = if (transfer != null) R.string.files_downloading else if (kind == ServerDeploymentKind.Upgrade)
                            R.string.server_progress_upgrading else R.string.server_progress_installing,
                        transfer = transfer?.let { app.relaxkonos.mobile.ui.common.TransferProgress("server.zip", it.bytes, it.total) },
                    )
                }
                check(receipt.kind == kind)
                if (receipt.state == ServerDeploymentState.Succeeded) check(installationReceiptMatchesMode(receipt, mode))
                attempt.receiptState = receipt.state
                index.markVerified(session.target, reference, key, System.currentTimeMillis())
                if (receipt.state in setOf(ServerDeploymentState.Failed, ServerDeploymentState.Cancelled, ServerDeploymentState.Interrupted)) clearPending()
                if (receipt.problemCode == "server-deployment.elevation_required") throw ServerInstallSudoException()
                check(receipt.state == ServerDeploymentState.Succeeded)
                progress(R.string.server_progress_verifying)
                val statusReceipt = read(ServerDeploymentKind.Status,
                    ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, mode = mode), sudoPassword)
                check(statusReceipt.kind == ServerDeploymentKind.Status && statusReceipt.state == ServerDeploymentState.Succeeded)
                val status = requireNotNull(statusReceipt.snapshot)
                container.serverCenter.recordVerifiedSnapshot(selection.hostId, status)
                check(installationSnapshotMatchesReceipt(receipt, status, mode))
                clearPending()
                firewallStatus = receipt.result?.firewallStatus
            }
        } finally { localZip?.delete(); credential.clear() }
        return firewallStatus
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
