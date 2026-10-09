package app.relaxkonos.mobile.ui.connect

import androidx.compose.runtime.*
import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.EndpointDiscoveryResult
import app.relaxkonos.mobile.core.net.ServerEndpointDiscovery
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.*
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.URI

/** Login-owned SSH connection until authentication succeeds, then transferred to the app session. */
class LoginTunnelController(private val container: AppContainer) {
    var enabled by mutableStateOf(false)
    var host by mutableStateOf("")
    var port by mutableStateOf("22")
    var userName by mutableStateOf("")
    var secret by mutableStateOf("")
    var passphrase by mutableStateOf("")
    var usePrivateKey by mutableStateOf(false)
    var useServerCredentials by mutableStateOf(false)
        private set
    fun setReuseServerCredentials(value: Boolean) {
        useServerCredentials = value
        clearCredential()
        secret = ""; passphrase = ""
        if (value) usePrivateKey = false
    }
    var rememberCredential by mutableStateOf(false)
    var configurationOpen by mutableStateOf(true)
    var statusResource by mutableStateOf<Int?>(null)
        private set
    var review by mutableStateOf<ServerCenterHostKeyRejectedException?>(null)
        private set
    private var answer: CompletableDeferred<Boolean>? = null
    private var session: ServerCenterHostSession? = null
    private var forward: ServerCenterSshTunnel? = null
    private var certificateEndpoint: app.relaxkonos.mobile.core.net.TunnelCertificateBinding? = null
    private var verifiedEndpoint: String? = null
    private var verifiedCredential: SshCredential? = null
    var identity by mutableStateOf<ServerConnectionIdentity?>(null)
        private set
    val profiles get() = container.loginTunnels.all()
    fun answerHostKey(accept: Boolean) { answer?.complete(accept) }
    fun profile(remoteUrl: String, serverUserName: String) = SshLoginTunnelProfile.create(
        host, port.toInt(), if (useServerCredentials) serverUserName else userName, remoteUrl).copy(useServerCredentials = useServerCredentials)
    fun select(profile: SshLoginTunnelProfile) {
        setReuseServerCredentials(profile.useServerCredentials)
        enabled = true; host = profile.host; port = profile.port.toString(); userName = profile.userName
        secret = ""; passphrase = ""; identity = null
        configurationOpen = false
    }

    internal fun enteredCredential(serverPassword: CharArray): SshCredential? = when {
        useServerCredentials -> {
            if (serverPassword.isEmpty()) throw LoginTunnelFailure(R.string.login_tunnel_server_password_required)
            SshCredential(SshCredentialKind.Password, serverPassword.copyOf(), null)
        }
        secret.isNotEmpty() -> SshCredential(
            if (usePrivateKey) SshCredentialKind.PrivateKey else SshCredentialKind.Password,
            secret.toCharArray(), passphrase.takeIf { it.isNotEmpty() }?.toCharArray())
        else -> null
    }

    suspend fun open(activity: FragmentActivity, remoteUrl: String, serverUserName: String, serverPassword: CharArray, testOnly: Boolean = false,
        confirmCertificate: suspend (app.relaxkonos.mobile.core.net.CertificateReview) -> Boolean) {
        close()
        val profile = profile(remoteUrl, serverUserName)
        val endpoint = ServerCenterSshEndpoint.create(profile.host, profile.port, profile.userName)
        val target = ServerHostTargetRules.create(profile.host, profile.port, profile.userName, null, System.currentTimeMillis())
        val credential = enteredCredential(serverPassword) ?: if (verifiedEndpoint == "${profile.host}:${profile.port}:${profile.userName}" && verifiedCredential != null) {
            verifiedCredential!!.let { SshCredential(it.kind, it.secret.copyOf(), it.passphrase?.copyOf()) }
        } else {
            val record = container.sshCredentials.record(profile.host, profile.port, profile.userName)
                ?: throw LoginTunnelFailure(R.string.login_tunnel_credential_required)
            val mode = container.unlockMode(VaultKind.Ssh) ?: throw IllegalStateException("SSH vault unavailable")
            when (val result = container.sshCredentials.load(mode, record, activity,
                activity.getString(R.string.vault_unlock_title), profile.displayText, activity.getString(R.string.common_cancel))) {
                is VaultOperation.Success -> result.value
                VaultOperation.Cancelled -> throw TunnelCancelledException()
                is VaultOperation.Failed -> throw LoginTunnelFailure(R.string.login_saved_password_unavailable)
            }
        }
        statusResource = R.string.login_tunnel_connecting
        try {
            try {
                session = container.serverCenterConnections.connect(target, credential, container.serverCenterConnections.prepareHostKeyGuard(target))
            } catch (rejected: ServerCenterHostKeyRejectedException) {
                val pending = CompletableDeferred<Boolean>()
                answer = pending; review = rejected
                val accepted = try { pending.await() } finally { answer = null; review = null }
                if (!accepted) throw TunnelCancelledException()
                container.sshHostKeyTrust.trust(endpoint, rejected.observation, System.currentTimeMillis())
                session = container.serverCenterConnections.connect(target, credential, container.serverCenterConnections.prepareHostKeyGuard(target))
            }
            val remote = URI(profile.remoteUrl)
            statusResource = R.string.login_tunnel_checking
            val remotePort = if (remote.port != -1) remote.port else if (remote.scheme == "https") 443 else 80
            forward = withContext(Dispatchers.IO) { session!!.sshTransport.openLoopbackTunnel(remotePort, remote.path) }
            val resolved = profile.resolve(forward!!.localPort)
            certificateEndpoint = app.relaxkonos.mobile.core.net.ServerCertificateTrust.bindTunnel(resolved.effectiveBaseUrl, resolved.serviceId)
            var probe = ServerEndpointDiscovery.discover(resolved.effectiveBaseUrl)
            app.relaxkonos.mobile.core.net.ServerCertificateTrust.review(resolved.effectiveBaseUrl)?.let { certificate ->
                if (!confirmCertificate(certificate)) throw TunnelCancelledException()
                app.relaxkonos.mobile.core.net.ServerCertificateTrust.trust(certificate)
                probe = ServerEndpointDiscovery.discover(resolved.effectiveBaseUrl)
            }
            if (testOnly && probe !is EndpointDiscoveryResult.Found)
                throw LoginTunnelFailure(R.string.login_server_unavailable)
            verifiedCredential?.clear()
            verifiedCredential = SshCredential(credential.kind, credential.secret.copyOf(), credential.passphrase?.copyOf())
            verifiedEndpoint = "${profile.host}:${profile.port}:${profile.userName}"
            container.loginTunnels.save(profile)
            if (!useServerCredentials && rememberCredential && secret.isNotEmpty()) {
                val mode = container.unlockMode(VaultKind.Ssh)
                val saved = if (mode == null) null else container.sshCredentials.save(mode, profile.host, profile.port, profile.userName, credential, activity,
                    activity.getString(R.string.vault_save_connection_title), profile.displayText,
                    activity.getString(R.string.common_cancel), System.currentTimeMillis())
                if (saved !is VaultOperation.Success) {
                    container.showNotice(app.relaxkonos.mobile.ui.common.UiMessage(R.string.login_tunnel_not_saved))
                }
            }
            identity = resolved
        } catch (error: Throwable) {
            val failure = if (error is LoginTunnelFailure || error is TunnelCancelledException || error is kotlinx.coroutines.CancellationException) error
                else LoginTunnelFailure(if (statusResource == R.string.login_tunnel_connecting)
                    app.relaxkonos.mobile.ui.servercenter.sshFailureMessage(SshFailureRules.classify(error)) else R.string.login_tunnel_failed)
            close(); throw failure
        }
        finally { credential.clear(); secret = ""; passphrase = "" }
    }

    fun adopt() {
        val ownedForward = forward ?: return
        val ownedSession = session ?: return
        val ownedIdentity = identity ?: return
        val ownedCertificateEndpoint = certificateEndpoint
        certificateEndpoint = null
        forward = null; session = null; identity = null
        clearCredential()
        container.adoptLoginTunnel(ownedIdentity) {
            ownedCertificateEndpoint?.let(app.relaxkonos.mobile.core.net.ServerCertificateTrust::unbindTunnel)
            try { ownedForward.close() } finally { ownedSession.close() }
        }
    }
    fun close() {
        statusResource = null
        certificateEndpoint?.let(app.relaxkonos.mobile.core.net.ServerCertificateTrust::unbindTunnel)
        certificateEndpoint = null
        val ownedForward = forward
        val ownedSession = session
        forward = null; session = null; identity = null
        try { ownedForward?.close() } finally { ownedSession?.close() }
    }
    fun clearCredential() { verifiedCredential?.clear(); verifiedCredential = null; verifiedEndpoint = null }
}

class TunnelCancelledException : Exception()
class LoginTunnelFailure(val messageResource: Int) : Exception()
