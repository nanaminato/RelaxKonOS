package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.compose.runtime.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID
internal data class MaintenanceState(val busy: Boolean = false, val snapshot: ServerHostSnapshot? = null,
    val probe: ServerHostProbe? = null, val error: String? = null, val complete: Boolean = false,
    val progress: Int = R.string.server_progress_connecting, val firewallStatus: String? = null)

internal fun maintenanceResultValid(kind: ServerDeploymentKind, snapshot: ServerHostSnapshot): Boolean = when (kind) {
    ServerDeploymentKind.Uninstall -> !snapshot.installed
    ServerDeploymentKind.Repair -> snapshot.installed && snapshot.healthy
    else -> true
}

internal class ServerMaintenanceViewModel(application: Application) : AndroidViewModel(application) {
    private val container = (application as RelaxKonApplication).container
    private val mutable = MutableStateFlow(MaintenanceState())
    val state = mutable.asStateFlow()
    fun run(hostId: String, kind: ServerDeploymentKind = ServerDeploymentKind.Status, purge: Boolean = false, sudo: String = "",
        repairCertificate: Boolean = false, certificateIdentities: String = "", addFirewallRule: Boolean = false, removeComponents: String = "") {
        if (mutable.value.busy) return
        mutable.value = mutable.value.copy(busy = true, error = null, complete = false, firewallStatus = null,
            progress = R.string.server_progress_connecting)
        viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            if (secret == null) { mutable.value = MaintenanceState(error = getApplication<Application>().getString(R.string.ssh_workspace_deploy_verify)); return@launch }
            val credential = SshCredential(SshCredentialKind.Password, secret, null)
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
                            val receipt = client.execute(staged, password)
                            container.serverInstallOperations.markVerified(session.target, reference, key, System.currentTimeMillis())
                            check(receipt.state == ServerDeploymentState.Succeeded) { receipt.problemCode ?: "server-deployment.failed" }
                            return receipt
                        }
                        mutable.value = mutable.value.copy(progress = R.string.server_progress_checking)
                        val probe = requireNotNull(action(ServerDeploymentKind.Probe,
                            ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, serverPort = 5000)).probe)
                        val mode = probe.existingMode ?: if (platform == ServerHostPlatform.Windows) ServerInstallMode.WindowsSystem else if (probe.elevated || probe.sudoAvailable) ServerInstallMode.LinuxSystem else ServerInstallMode.LinuxUser
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
                            firewallStatus = action(kind, maintenanceOptions(kind, mode, requireNotNull(before.installationId), purge,
                                repairCertificate, certificateIdentities, addFirewallRule, removeComponents).copy(
                                    allowUnsupportedSystem = mode == ServerInstallMode.LinuxSystem && !probe.osSupported), password).result?.firewallStatus
                        }
                        mutable.value = mutable.value.copy(progress = R.string.server_progress_verifying)
                        val after = if (kind == ServerDeploymentKind.Status) before else requireNotNull(action(ServerDeploymentKind.Status, options, password).snapshot)
                        container.serverCenter.recordVerifiedSnapshot(hostId, after)
                        check(maintenanceResultValid(kind, after)) { "server-deployment.postcondition_failed" }
                        MaintenanceState(snapshot = after, probe = probe, complete = kind != ServerDeploymentKind.Status, firewallStatus = firewallStatus)
                    }
                }
                mutable.value = result
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { mutable.value = mutable.value.copy(busy = false, snapshot = null, error = error.message) }
            finally { credential.clear(); secret.fill('\u0000') }
        }
    }
}
