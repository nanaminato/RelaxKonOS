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
    Row {
        Checkbox(checked = tunnel.enabled, onCheckedChange = { tunnel.close(); tunnel.clearCredential(); tunnel.enabled = it }, enabled = !viewModel.isLoggingIn)
        Text(stringResource(R.string.login_tunnel_toggle))
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
        Text("${tunnel.userName}@${tunnel.host}:${tunnel.port}", style = MaterialTheme.typography.bodySmall)
        TextButton(onClick = { tunnel.configurationOpen = !tunnel.configurationOpen }, enabled = !viewModel.isLoggingIn) {
            Text(stringResource(R.string.login_tunnel_configure))
        }
        if (tunnel.configurationOpen) {
        OutlinedTextField(value = tunnel.host, onValueChange = { tunnel.host = it }, label = { Text(stringResource(R.string.login_tunnel_host)) },
            singleLine = true, enabled = !viewModel.isLoggingIn, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(value = tunnel.port, onValueChange = { tunnel.port = it }, label = { Text(stringResource(R.string.login_tunnel_port)) },
            singleLine = true, enabled = !viewModel.isLoggingIn, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(value = tunnel.userName, onValueChange = { tunnel.userName = it }, label = { Text(stringResource(R.string.login_tunnel_user)) },
            singleLine = true, enabled = !viewModel.isLoggingIn, modifier = Modifier.fillMaxWidth())
        Row {
            Checkbox(checked = tunnel.usePrivateKey, onCheckedChange = { tunnel.usePrivateKey = it }, enabled = !viewModel.isLoggingIn)
            Text(stringResource(R.string.login_tunnel_key))
        }
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
        Row {
            Checkbox(checked = tunnel.rememberCredential, onCheckedChange = { tunnel.rememberCredential = it }, enabled = !viewModel.isLoggingIn)
            Text(stringResource(R.string.login_tunnel_remember))
        }
        }
        OutlinedButton(onClick = { viewModel.testTunnel(activity) }, enabled = !viewModel.isLoggingIn) {
            Text(stringResource(R.string.login_tunnel_test))
        }
    }
}
