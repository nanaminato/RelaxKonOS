package app.relaxkonos.mobile.ui.files

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import com.google.android.gms.auth.api.identity.AuthorizationRequest
import com.google.android.gms.auth.api.identity.Identity
import com.google.android.gms.common.api.Scope
import java.time.Instant
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** In-memory challenges only. Leaving this dialog does not persist codes, links or tokens. */
@Composable
fun CloudAuthorizationDialog(container: AppContainer, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val identity by container.session.state.collectAsState()
    var browsing by remember(identity) { mutableStateOf<CloudConnection?>(null) }
    browsing?.let { selected ->
        CloudBrowserDialog(container, selected) { browsing = null }
    }
    var providers by remember(identity) { mutableStateOf(emptyList<CloudAuthorizationProvider>()) }
    var connections by remember(identity) { mutableStateOf(emptyList<CloudConnection>()) }
    var challenge by remember(identity) { mutableStateOf<CloudAuthorizationSession?>(null) }
    var name by remember(identity) { mutableStateOf("") }
    var writes by remember(identity) { mutableStateOf(false) }
    var busy by remember(identity) { mutableStateOf(false) }
    var failed by remember(identity) { mutableStateOf(false) }
    var expired by remember(identity) { mutableStateOf(false) }
    val api = container.gateway
    val auth = container.session
    val lifetime = remember(identity) { CloudAuthorizationLifetime(identity) }
    // Keep the exact launch binding across recompositions and login changes. A late Activity result
    // must never be relabelled with the new dialog's current challenge.
    var resolutionBinding by remember { mutableStateOf<Pair<CloudAuthorizationLifetime, String>?>(null) }
    DisposableEffect(lifetime) { onDispose { lifetime.close() } }
    suspend fun <T> request(call: suspend (String, String) -> ApiResult<T>): ApiResult<T> =
        auth.authenticated { url, token ->
            if (lifetime.live(auth.state.value)) call(url, token) else ApiResult.Transport(null)
        }
    val google = remember(context) { Identity.getAuthorizationClient(context) }
    suspend fun refresh() {
        val result = request(api::cloudConnections)
        if (!lifetime.live(auth.state.value)) return
        when (result) {
            is ApiResult.Success -> connections = result.value
            else -> failed = true
        }
    }
    fun sendCode(expectedId: String, code: String?) {
        if (!lifetime.matches(auth.state.value, expectedId)) return
        if (code.isNullOrBlank()) { failed = true; return }
        scope.launch {
            val result = request { u, t -> api.cloudCode(u, t, expectedId, code) }
            if (!lifetime.matches(auth.state.value, expectedId)) return@launch
            when (result) {
                is ApiResult.Success -> { challenge = result.value; refresh() }
                else -> failed = true
            }
        }
    }
    val googleResolution = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { result ->
        val binding = resolutionBinding
        resolutionBinding = null
        if (binding != null && binding.first === lifetime && lifetime.matches(auth.state.value, binding.second)) {
            if (result.resultCode == android.app.Activity.RESULT_OK) {
                runCatching { google.getAuthorizationResultFromIntent(result.data).serverAuthCode }
                    .onSuccess { sendCode(binding.second, it) }.onFailure { failed = true }
            } else { failed = true }
        }
    }
    LaunchedEffect(identity) {
        val result = request(api::cloudProviders)
        if (!lifetime.live(auth.state.value)) return@LaunchedEffect
        when (result) {
            is ApiResult.Success -> providers = result.value
            else -> failed = true
        }
        refresh()
    }
    LaunchedEffect(challenge?.id, identity) {
        while (challenge?.pending == true) {
            val current = challenge ?: break
            if (!Instant.now().isBefore(current.expiresAt)) { expired = true; break }
            delay(current.pollIntervalSeconds * 1000L)
            val result = request { u, t -> api.cloudStatus(u, t, current.id) }
            if (!lifetime.matches(auth.state.value, current.id)) break
            when (result) {
                is ApiResult.Success -> { challenge = result.value; if (!result.value.pending) refresh() }
                else -> { failed = true; break }
            }
        }
    }
    AlertDialog(onDismissRequest = { if (!busy && challenge?.pending != true) { lifetime.close(); onDismiss() } },
        title = { Text(stringResource(R.string.cloud_authorization)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.cloud_android_boundary))
                connections.forEach { connection ->
                    TextButton(enabled = !busy && challenge?.pending != true, onClick = { browsing = connection }) {
                        Text("${connection.name} (${connection.protocol}) · ${connection.id.take(8)}")
                    }
                }
                if (failed) Text(stringResource(R.string.cloud_auth_failed), color = MaterialTheme.colorScheme.error)
                if (challenge == null) {
                    OutlinedTextField(value = name, onValueChange = { name = it.take(100) }, label = { Text(stringResource(R.string.cloud_connection_name)) })
                    Row { Checkbox(checked = writes, onCheckedChange = { writes = it }); Text(stringResource(R.string.cloud_enable_writes)) }
                    providers.filter { it.protocol in setOf("google-drive", "baidu-netdisk") }.forEach { provider ->
                        Button(enabled = provider.configured && !busy && name.isNotBlank(), onClick = {
                            scope.launch {
                                busy = true; failed = false; expired = false
                                val native = provider.protocol == "google-drive"
                                val result = request { u, t -> api.cloudStart(u, t, provider.protocol, name.trim(), if (native) "android" else "browser", writes) }
                                if (!lifetime.live(auth.state.value)) return@launch
                                when (result) {
                                    is ApiResult.Success -> {
                                        lifetime.bind(result.value.id)
                                        challenge = result.value
                                        if (native) {
                                            val clientId = provider.clientId
                                            if (clientId.isNullOrBlank()) failed = true else {
                                                val request = AuthorizationRequest.builder()
                                                    .setRequestedScopes(listOf(Scope(if (writes) "https://www.googleapis.com/auth/drive" else "https://www.googleapis.com/auth/drive.readonly")))
                                                    // Each new named connection needs its own refresh token. Do not
                                                    // reuse another connection's token without verified Google identity.
                                                    .requestOfflineAccess(clientId, true).build()
                                                google.authorize(request).addOnSuccessListener { authorization ->
                                                    if (!lifetime.matches(auth.state.value, result.value.id)) return@addOnSuccessListener
                                                    if (authorization.hasResolution()) {
                                                        authorization.pendingIntent?.let {
                                                            resolutionBinding = lifetime to result.value.id
                                                            googleResolution.launch(IntentSenderRequest.Builder(it).build())
                                                        }
                                                            ?: run { failed = true }
                                                    } else sendCode(result.value.id, authorization.serverAuthCode)
                                                }.addOnFailureListener { if (lifetime.matches(auth.state.value, result.value.id)) failed = true }
                                            }
                                        }
                                    }
                                    else -> failed = true
                                }
                                busy = false
                            }
                        }) { Text(provider.protocol + if (!provider.configured) " (not configured)" else "") }
                    }
                }
                challenge?.let { current ->
                    Text(if (expired) stringResource(R.string.cloud_expired) else current.status)
                    if (current.pending && !expired) {
                        current.userCode?.let { Text(it, style = MaterialTheme.typography.headlineMedium) }
                        current.authorizationUrl?.takeIf(CloudAuthorizationWire::safeBaiduUrl)?.let { url ->
                            Button(onClick = { runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }.onFailure { failed = true } }) {
                                Text(stringResource(R.string.cloud_open_browser))
                            }
                        }
                        Text(stringResource(R.string.cloud_waiting))
                    }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy, onClick = {
                val current = challenge
                if (current?.pending == true) scope.launch {
                    busy = true
                    val result = request { u, t -> api.cloudCancel(u, t, current.id) }
                    if (!lifetime.matches(auth.state.value, current.id)) return@launch
                    when (result) {
                        is ApiResult.Success -> { lifetime.close(); challenge = null; onDismiss() }
                        else -> failed = true
                    }
                    busy = false
                } else { lifetime.close(); onDismiss() }
            }) { Text(stringResource(if (challenge?.pending == true) R.string.cloud_cancel else R.string.cloud_close)) }
        })
}
