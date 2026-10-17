package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.compose.runtime.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.common.UiMessage
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID
internal data class MaintenanceState(val busy: Boolean = false, val snapshot: ServerHostSnapshot? = null,
    val probe: ServerHostProbe? = null, val error: UiMessage? = null, val complete: Boolean = false,
    val progress: Int = R.string.server_progress_connecting, val firewallStatus: String? = null)

internal fun maintenanceResultValid(kind: ServerDeploymentKind, snapshot: ServerHostSnapshot): Boolean = when (kind) {
    ServerDeploymentKind.Uninstall -> !snapshot.installed
    ServerDeploymentKind.Firewall -> snapshot.installed
    ServerDeploymentKind.Repair, ServerDeploymentKind.Configure -> snapshot.installed && snapshot.healthy
    else -> true
}

internal class ServerMaintenanceViewModel(application: Application) : AndroidViewModel(application) {
    private val container = (application as RelaxKonApplication).container
    private val mutable = MutableStateFlow(MaintenanceState())
    val state = mutable.asStateFlow()
    fun run(hostId: String, kind: ServerDeploymentKind = ServerDeploymentKind.Status, purge: Boolean = false, sudo: String = "",
        repairCertificate: Boolean = false, certificateIdentities: String = "", addFirewallRule: Boolean = false, removeComponents: String = "", configuration: ServerConfigurationDraft? = null, removeFirewallRules: Boolean = true, repairPackagePath: String = "") {
        if (mutable.value.busy) return
        mutable.value = mutable.value.copy(busy = true, error = null, complete = false, firewallStatus = null,
            progress = R.string.server_progress_connecting)
        viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            if (secret == null) { mutable.value = MaintenanceState(error = UiMessage(R.string.ssh_workspace_deploy_verify)); return@launch }
            val credential = SshCredential(SshCredentialKind.Password, secret, null)
            var mutationStarted = false
            try {
                val result = withContext(Dispatchers.IO) {
                    container.serverCenterConnections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
                        val platform = if (session.sshTransport.run("uname -s").let { it.succeeded && it.standardOutput.trim() == "Linux" }) ServerHostPlatform.Linux else ServerHostPlatform.Windows
                        val launcher = ServerCenterUploadAsset.launcher(getApplication<Application>().assets, platform)
                        val client = ServerCenterDeploymentClient(session.sshTransport)
                        val key = requireNotNull(session.observedHostKey)
                        suspend fun action(action: ServerDeploymentKind, options: ServerDeploymentOptions? = null, password: String? = null): ServerDeploymentOperation {
                            val id = UUID.randomUUID().toString()
                            val staged = client.stage(ServerDeploymentRequest(ServerDeploymentProtocol.VERSION, id, action, options), platform, launcher)
                            val reference = container.serverInstallOperations.record(session.target, key, id, platform)
                            if (action != ServerDeploymentKind.Probe && action != ServerDeploymentKind.Status) mutationStarted = true
                            val receipt = client.execute(staged, password)
                            container.serverInstallOperations.markVerified(session.target, reference, key, System.currentTimeMillis())
                            check(receipt.state == ServerDeploymentState.Succeeded) { receipt.problemCode ?: "server-deployment.failed" }
                            return receipt
                        }
                        mutable.value = mutable.value.copy(progress = R.string.server_progress_checking)
                        val probe = requireNotNull(action(ServerDeploymentKind.Probe,
                            ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, serverPort = 5000)).probe)
                        val mode = probe.existingMode ?: if (platform == ServerHostPlatform.Windows) ServerInstallMode.WindowsSystem else if (probe.elevated || probe.privilegeElevationAvailable) ServerInstallMode.LinuxSystem else ServerInstallMode.LinuxUser
                        val password = if (mode == ServerInstallMode.LinuxSystem && !probe.elevated) sudo.ifEmpty { String(secret) } else null
                        val options = ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, mode = mode)
                        val before = requireNotNull(action(ServerDeploymentKind.Status, options, password).snapshot)
                        var firewallStatus: String? = null
                        if (kind != ServerDeploymentKind.Status) {
                            check(before.installed && ServerInstallationId.isValid(before.installationId))
                            mutable.value = mutable.value.copy(progress = when (kind) {
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
                        mutable.value = mutable.value.copy(progress = R.string.server_progress_verifying)
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
                        MaintenanceState(snapshot = after, probe = probe, complete = kind != ServerDeploymentKind.Status, firewallStatus = firewallStatus)
                    }
                }
                mutable.value = result
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) {
                if (SshFailureRules.classify(error) == SshFailureReason.AuthenticationRejected) container.serverCenter.forgetSessionPassword(hostId)
                mutable.value = mutable.value.copy(busy = false, snapshot = null, error = maintenanceErrorMessage(error, mutationStarted))
            }
            finally { credential.clear(); secret.fill('\u0000') }
        }
    }
}

/** Product errors expose only known protocol codes or a classified transport cause. */
internal fun maintenanceErrorMessage(error: Throwable, mutationStarted: Boolean): UiMessage {
    val code = error.message
    if (code == "server-deployment.postcondition_failed") return UiMessage(R.string.server_maintenance_verification_failed)
    if (code in ServerDeploymentProblemCodes.all) return UiMessage(R.string.server_maintenance_failed_code, listOf(requireNotNull(code)))
    if (mutationStarted) return UiMessage(R.string.server_maintenance_result_unknown)
    val reason = SshFailureRules.classify(error)
    return UiMessage(if (reason == SshFailureReason.Unexpected) R.string.server_maintenance_read_failed else sshFailureMessage(reason))
}
