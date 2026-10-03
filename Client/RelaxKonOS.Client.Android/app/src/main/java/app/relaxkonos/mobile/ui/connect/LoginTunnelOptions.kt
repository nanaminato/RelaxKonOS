package app.relaxkonos.mobile.ui.connect

import androidx.compose.runtime.Composable
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.PasswordTextField

@Composable
fun LoginTunnelOptions(viewModel: LoginViewModel, activity: FragmentActivity) {
    val tunnel = viewModel.tunnel
    LoginCheckboxRow(tunnel.enabled, stringResource(R.string.login_tunnel_toggle), !viewModel.isLoggingIn) {
        tunnel.close(); tunnel.clearCredential(); tunnel.enabled = it
    }
    if (!tunnel.enabled) return
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        tunnel.statusResource?.let { Text(stringResource(it)) }
        Text(stringResource(R.string.login_tunnel_hint), style = MaterialTheme.typography.bodySmall)
        tunnel.profiles.forEach { profile ->
            TextButton(enabled = !viewModel.isLoggingIn, onClick = { tunnel.select(profile); viewModel.changeServer(profile.remoteUrl) }) {
                Text(profile.displayText)
            }
        }
        Text("${if (tunnel.useServerCredentials) viewModel.identifier else tunnel.userName}@${tunnel.host}:${tunnel.port}", style = MaterialTheme.typography.bodySmall)
        TextButton(onClick = { tunnel.configurationOpen = !tunnel.configurationOpen }, enabled = !viewModel.isLoggingIn) {
            Text(stringResource(R.string.login_tunnel_configure))
        }
        if (tunnel.configurationOpen) {
        OutlinedTextField(value = tunnel.host, onValueChange = { tunnel.host = it }, label = { Text(stringResource(R.string.login_tunnel_host)) },
            singleLine = true, enabled = !viewModel.isLoggingIn, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(value = tunnel.port, onValueChange = { tunnel.port = it }, label = { Text(stringResource(R.string.login_tunnel_port)) },
            singleLine = true, enabled = !viewModel.isLoggingIn, modifier = Modifier.fillMaxWidth())
        LoginCheckboxRow(tunnel.useServerCredentials, stringResource(R.string.login_tunnel_reuse_server), !viewModel.isLoggingIn) {
            tunnel.setReuseServerCredentials(it)
        }
        if (tunnel.useServerCredentials) {
            Text(stringResource(R.string.login_tunnel_reuse_hint), style = MaterialTheme.typography.bodySmall)
        } else {
        OutlinedTextField(value = tunnel.userName, onValueChange = { tunnel.userName = it }, label = { Text(stringResource(R.string.login_tunnel_user)) },
            singleLine = true, enabled = !viewModel.isLoggingIn, modifier = Modifier.fillMaxWidth())
        LoginCheckboxRow(tunnel.usePrivateKey, stringResource(R.string.login_tunnel_key), !viewModel.isLoggingIn) { tunnel.usePrivateKey = it }
        if (tunnel.usePrivateKey) {
            OutlinedTextField(value = tunnel.secret, onValueChange = { tunnel.secret = it },
                label = { Text(stringResource(R.string.login_tunnel_secret)) }, visualTransformation = PasswordVisualTransformation(),
                enabled = !viewModel.isLoggingIn, modifier = Modifier.fillMaxWidth(), minLines = 3)
            PasswordTextField(value = tunnel.passphrase, onValueChange = { tunnel.passphrase = it },
                label = stringResource(R.string.login_tunnel_passphrase), enabled = !viewModel.isLoggingIn)
        } else {
            PasswordTextField(value = tunnel.secret, onValueChange = { tunnel.secret = it },
                label = stringResource(R.string.login_tunnel_secret), enabled = !viewModel.isLoggingIn)
        }
        }
        if (!tunnel.useServerCredentials) {
            LoginCheckboxRow(tunnel.rememberCredential, stringResource(R.string.login_tunnel_remember), !viewModel.isLoggingIn) { tunnel.rememberCredential = it }
        }
        }
        OutlinedButton(onClick = { viewModel.testTunnel(activity) }, enabled = !viewModel.isLoggingIn) {
            Text(stringResource(R.string.login_tunnel_test))
        }
    }
}
