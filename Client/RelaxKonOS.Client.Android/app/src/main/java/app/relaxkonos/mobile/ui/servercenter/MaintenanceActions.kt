package app.relaxkonos.mobile.ui.servercenter

import android.content.res.AssetManager
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.common.UiMessage
import kotlinx.coroutines.*
import java.util.UUID

internal class MaintenanceRequest(
    val hostId: String, val kind: ServerDeploymentKind, val purge: Boolean = false, val sudo: String = "",
    val repairCertificate: Boolean = false, val certificateIdentities: String = "", val addFirewallRule: Boolean = false,
    val removeComponents: String = "", val configuration: ServerConfigurationDraft? = null, val removeFirewallRules: Boolean = true,
    val repairPackagePath: String = "", val reviewedSnapshot: ServerHostSnapshot? = null, val recovery: MaintenancePending? = null,
)

internal fun maintenanceSnapshotMatches(current: ServerHostSnapshot, reviewed: ServerHostSnapshot) =
    current.copy(verifiedAtUtc = reviewed.verifiedAtUtc) == reviewed

internal fun interface MaintenanceActions {
    /** The synchronous admission callback must persist recovery metadata before dispatch; failure aborts the write. */
    suspend fun execute(request: MaintenanceRequest, onProgress: (Int) -> Unit, beforeMutationDispatch: (MaintenancePending) -> Unit): MaintenanceState
}

internal class RepositoryMaintenanceActions(private val container: AppContainer, private val assets: AssetManager) : MaintenanceActions {
    override suspend fun execute(request: MaintenanceRequest, onProgress: (Int) -> Unit, beforeMutationDispatch: (MaintenancePending) -> Unit): MaintenanceState {
        with(request) {
            val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            if (secret == null) return MaintenanceState(error = UiMessage(R.string.ssh_workspace_deploy_verify))
            val credential = SshCredential(SshCredentialKind.Password, secret, null)
            var mutationStarted = false
            var mutationReceipt: ServerDeploymentOperation? = null
            try {
                return withContext(Dispatchers.IO) {
                    container.serverCenterConnections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
                        request.recovery?.let { pending ->
                            val reference = pending.reference
                            val key = requireNotNull(session.observedHostKey)
                            check(reference.hostId == hostId && reference.hostKeyAlgorithm == key.algorithm && reference.hostKeyFingerprint == key.fingerprint)
                            onProgress(R.string.server_progress_verifying)
                            val client = ServerCenterDeploymentClient(session.sshTransport)
                            val lookup = client.stageLookup(reference.platform, ServerCenterUploadAsset.launcher(assets, reference.platform))
                            val receipt = client.query(lookup, reference.operationId)
                            check(maintenanceRecoveryMatches(pending, receipt))
                            container.serverInstallOperations.markVerified(session.target, reference, key, System.currentTimeMillis())
                            return@withContext MaintenanceState(recovered = receipt)
                        }
                        val platform = if (session.sshTransport.run("uname -s").let { it.succeeded && it.standardOutput.trim() == "Linux" }) ServerHostPlatform.Linux else ServerHostPlatform.Windows
                        val launcher = ServerCenterUploadAsset.launcher(assets, platform)
                        val client = ServerCenterDeploymentClient(session.sshTransport)
                        val key = requireNotNull(session.observedHostKey)
                        suspend fun action(action: ServerDeploymentKind, options: ServerDeploymentOptions? = null, password: String? = null): ServerDeploymentOperation {
                            val id = UUID.randomUUID().toString()
                            val staged = client.stage(ServerDeploymentRequest(ServerDeploymentProtocol.VERSION, id, action, options), platform, launcher)
                            val reference = container.serverInstallOperations.record(session.target, key, id, platform)
                            if (action != ServerDeploymentKind.Probe && action != ServerDeploymentKind.Status) { beforeMutationDispatch(MaintenancePending(reference, action)); mutationStarted = true }
                            val receipt = client.execute(staged, password)
                            check(receipt.kind == action)
                            container.serverInstallOperations.markVerified(session.target, reference, key, System.currentTimeMillis())
                            if (mutationStarted && action == kind && maintenanceRecoveryMatches(MaintenancePending(reference, action), receipt)) mutationReceipt = receipt
                            check(receipt.state == ServerDeploymentState.Succeeded) { receipt.problemCode ?: "server-deployment.failed" }
                            return receipt
                        }
                        onProgress(R.string.server_progress_checking)
                        val probe = requireNotNull(action(ServerDeploymentKind.Probe,
                            ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, serverPort = 5000)).probe)
                        val mode = probe.existingMode ?: if (platform == ServerHostPlatform.Windows) ServerInstallMode.WindowsSystem else if (probe.elevated || probe.privilegeElevationAvailable) ServerInstallMode.LinuxSystem else ServerInstallMode.LinuxUser
                        val password = if (mode == ServerInstallMode.LinuxSystem && !probe.elevated) sudo.ifEmpty { String(secret) } else null
                        val options = ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, mode = mode)
                        val before = requireNotNull(action(ServerDeploymentKind.Status, options, password).snapshot)
                        var firewallStatus: String? = null
                        if (kind != ServerDeploymentKind.Status) {
                            check(before.installed && ServerInstallationId.isValid(before.installationId))
                            check(maintenanceSnapshotMatches(before, requireNotNull(reviewedSnapshot))) { "server-deployment.configuration_changed" }
                            onProgress(when (kind) {
                                ServerDeploymentKind.Uninstall -> R.string.server_progress_uninstalling
                                ServerDeploymentKind.Upgrade -> R.string.server_progress_upgrading
                                else -> R.string.server_progress_repairing
                            })
                            if (kind == ServerDeploymentKind.Firewall) {
                                action(kind, options.copy(expectedInstallationId = before.installationId, confirmed = true, removeFirewallRules = removeFirewallRules, addFirewallRule = !removeFirewallRules), password)
                            } else if (kind == ServerDeploymentKind.Configure) {
                                val draft = requireNotNull(configuration)
                                check(before.copy(verifiedAtUtc = draft.reviewed.verifiedAtUtc) == draft.reviewed) { "server-deployment.configuration_changed" }
                                check(draft.port in 1..65535 && before.guardianEnabled != null)
                                action(kind, options.copy(expectedInstallationId = before.installationId, confirmed = true,
                                    serverPort = draft.port, network = if (draft.lan) ServerNetworkProfile.Lan else ServerNetworkProfile.Loopback,
                                    guardianEnabled = draft.guardian, autoStart = draft.autoStart, fileAccess = draft.fileAccess, fileRoots = draft.fileRoots), password)
                            } else firewallStatus = action(kind, maintenanceOptions(kind, mode, requireNotNull(before.installationId), purge,
                                repairCertificate, certificateIdentities, addFirewallRule, removeComponents).copy(
                                    source = if (kind == ServerDeploymentKind.Repair && repairPackagePath.isNotBlank()) ServerPackageSourceKind.RemoteBundle else ServerPackageSourceKind.OfficialStable,
                                    remotePackagePath = repairPackagePath.trim().takeIf { kind == ServerDeploymentKind.Repair && it.isNotEmpty() },
                                    allowUnsupportedSystem = mode == ServerInstallMode.LinuxSystem && !probe.osSupported), password).result?.firewallStatus
                        }
                        onProgress(R.string.server_progress_verifying)
                        val after = if (kind == ServerDeploymentKind.Status) before else requireNotNull(action(ServerDeploymentKind.Status, options, password).snapshot)
                        container.serverCenter.recordVerifiedSnapshot(hostId, after)
                        if (kind == ServerDeploymentKind.Configure) {
                            val draft = requireNotNull(configuration)
                            val endpoint = java.net.URI(requireNotNull(after.listenUrl))
                            check((draft.autoStart == null || after.autoStart == draft.autoStart) &&
                                (draft.fileAccess == null || after.fileAccess == draft.fileAccess) &&
                                (draft.fileAccess != "whitelist" || after.fileRoots == draft.fileRoots) &&
                                after.guardianEnabled == draft.guardian && endpoint.port == draft.port &&
                                endpoint.host == if (draft.lan) "0.0.0.0" else "127.0.0.1") { "server-deployment.postcondition_failed" }
                        }
                        check(maintenanceResultValid(kind, after)) { "server-deployment.postcondition_failed" }
                        MaintenanceState(snapshot = after, probe = probe, complete = kind != ServerDeploymentKind.Status, firewallStatus = firewallStatus, verifiedMutation = mutationReceipt)
                    }
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) {
                if (SshFailureRules.classify(error) == SshFailureReason.AuthenticationRejected) container.serverCenter.forgetSessionPassword(hostId)
                return MaintenanceState(error = maintenanceErrorMessage(error, mutationStarted && mutationReceipt == null), verifiedMutation = mutationReceipt)
            }
            finally { credential.clear(); secret.fill('\u0000') }
        }
    }
}
