package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import app.relaxkonos.mobile.servercenter.ServerDeploymentState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.ServerCenterDeploymentClient
import app.relaxkonos.mobile.servercenter.ServerCenterUploadAsset
import app.relaxkonos.mobile.servercenter.ServerDeploymentOperation
import app.relaxkonos.mobile.servercenter.ServerDeploymentReceiptMissingException
import app.relaxkonos.mobile.servercenter.ServerHostPlatform
import app.relaxkonos.mobile.servercenter.ServerInstallOperationReference
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
internal enum class InstallReceiptCheck { Verified, Missing, Unavailable }

internal data class InstallReceiptItem(
    val reference: ServerInstallOperationReference,
    val check: InstallReceiptCheck,
    val receipt: ServerDeploymentOperation? = null,
)

internal data class InstallRecoveryState(
    val hostId: String? = null,
    val platform: ServerHostPlatform? = null,
    val loading: Boolean = false,
    val needsVerification: Boolean = false,
    val incomplete: Boolean = false,
    val clearFailed: Boolean = false,
    val items: List<InstallReceiptItem> = emptyList(),
)

class ServerInstallRecoveryViewModel(application: Application) : AndroidViewModel(application) {
    suspend fun readLog(hostId: String, reference: ServerInstallOperationReference): Result<String> {
        val container = getApplication<RelaxKonApplication>().container
        val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            ?: return Result.failure(IllegalStateException("No verified credential"))
        val credential = SshCredential(SshCredentialKind.Password, secret, null)
        return try {
            Result.success(withContext(Dispatchers.IO) {
                container.serverCenterConnections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
                    val client = ServerCenterDeploymentClient(session.sshTransport)
                    val assets = getApplication<Application>().assets
                    val lookup = client.stageLookup(reference.platform, ServerCenterUploadAsset.launcher(assets, reference.platform))
                    client.diagnostics(lookup, reference.operationId)
                }
            })
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (error: Exception) { Result.failure(error) }
        finally { credential.clear(); secret.fill('\u0000') }
    }


    private val container = getApplication<RelaxKonApplication>().container
    private val mutableState = MutableStateFlow(InstallRecoveryState())
    internal val state = mutableState.asStateFlow()
    private var refreshJob: Job? = null
    private var generation = 0

    fun clearCompleted(hostId: String) {
        val current = mutableState.value
        if (current.hostId != hostId || current.loading) return
        val references = current.items.filter { it.canClearHistory() }.map { it.reference }
        if (references.isEmpty()) return
        mutableState.value = current.copy(loading = true, clearFailed = false)
        val request = ++generation
        refreshJob = viewModelScope.launch {
            val cleared = mutableSetOf<ServerInstallOperationReference>()
            val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            if (secret == null) {
                mutableState.value = current.copy(needsVerification = true, clearFailed = true)
                return@launch
            }
            val credential = SshCredential(SshCredentialKind.Password, secret, null)
            try {
                withContext(Dispatchers.IO) {
                    container.serverCenterConnections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
                        val key = requireNotNull(session.observedHostKey)
                        check(references.all { it.hostKeyAlgorithm == key.algorithm && it.hostKeyFingerprint == key.fingerprint })
                        val client = ServerCenterDeploymentClient(session.sshTransport)
                        val platform = references.first().platform
                        val lookup = client.stageLookup(platform, ServerCenterUploadAsset.launcher(getApplication<Application>().assets, platform))
                        references.forEach { reference ->
                            client.clearOperation(lookup, reference.operationId)
                            container.serverInstallOperations.forget(reference)
                            cleared.add(reference)
                        }
                    }
                }
                if (request == generation) mutableState.value = current.copy(
                    items = current.items.filterNot { it.reference in cleared }, clearFailed = false)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (request == generation) mutableState.value = current.copy(
                    items = current.items.filterNot { it.reference in cleared }, clearFailed = true)
            } finally {
                credential.clear()
                secret.fill('\u0000')
            }
        }
    }

    fun refresh(hostId: String, platform: ServerHostPlatform? = null) {
        refreshJob?.cancel()
        val request = ++generation
        mutableState.value = InstallRecoveryState(hostId = hostId, platform = platform, loading = true)
        refreshJob = viewModelScope.launch {
            val secret = container.serverCenter.verifiedPasswordCopy(hostId)
            if (secret == null) {
                if (request == generation) mutableState.value = InstallRecoveryState(hostId, platform, needsVerification = true)
                return@launch
            }
            try {
                val result = withContext(Dispatchers.IO) { readReceipts(hostId, platform, secret) }
                if (request == generation) mutableState.value = result
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (request == generation) mutableState.value = InstallRecoveryState(hostId, platform, incomplete = true)
            } finally {
                secret.fill('\u0000')
            }
        }
    }

    fun stop() {
        generation++
        refreshJob?.cancel()
        refreshJob = null
    }

    private suspend fun readReceipts(hostId: String, knownPlatform: ServerHostPlatform?, secret: CharArray): InstallRecoveryState {
        val credential = SshCredential(SshCredentialKind.Password, secret, null)
        try {
            return container.serverCenterConnections.connect(hostId, credential, System.currentTimeMillis()).use { session ->
                val platform = knownPlatform ?: if (session.sshTransport.run("uname -s").let { it.succeeded && it.standardOutput.trim() == "Linux" }) ServerHostPlatform.Linux else ServerHostPlatform.Windows
                val key = requireNotNull(session.observedHostKey) { "A verified SSH host key is required." }
                val index = container.serverInstallOperations
                val local = index.forTrustedHost(session.target, key).filter { it.platform == platform }
                val client = ServerCenterDeploymentClient(session.sshTransport)
                val launcher = ServerCenterUploadAsset.launcher(getApplication<Application>().assets, platform)
                val lookup = client.stageLookup(platform, launcher)
                var incomplete = false
                val remoteIds = try {
                    client.list(lookup)
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (_: Exception) {
                    incomplete = true
                    emptyList()
                }
                val localById = local.associateBy { it.operationId }
                val allIds = (remoteIds + local.map { it.operationId }).distinct()
                if (allIds.size > 100) incomplete = true
                val ids = allIds.take(100)
                val items = ids.map { id ->
                    val reference = localById[id] ?: try {
                        index.record(session.target, key, id, platform)
                    } catch (_: IllegalStateException) {
                        incomplete = true
                        ServerInstallOperationReference(session.target.hostId, key.algorithm, key.fingerprint,
                            id, platform, System.currentTimeMillis())
                    }
                    try {
                        val receipt = client.query(lookup, id)
                        try {
                            index.markVerified(session.target, reference, key, System.currentTimeMillis())
                        } catch (_: IllegalStateException) {
                            incomplete = true
                        }
                        InstallReceiptItem(reference, InstallReceiptCheck.Verified, receipt)
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (_: ServerDeploymentReceiptMissingException) {
                        InstallReceiptItem(reference, InstallReceiptCheck.Missing)
                    } catch (_: Exception) {
                        InstallReceiptItem(reference, InstallReceiptCheck.Unavailable)
                    }
                }
                InstallRecoveryState(hostId, platform, incomplete = incomplete, items = items)
            }
        } finally {
            credential.clear()
        }
    }
}

internal fun InstallReceiptItem.canClearHistory(): Boolean = check == InstallReceiptCheck.Verified &&
    receipt?.state in setOf(ServerDeploymentState.Succeeded, ServerDeploymentState.Failed,
        ServerDeploymentState.Cancelled, ServerDeploymentState.Interrupted)
