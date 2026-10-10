package app.relaxkonos.mobile.ui.manage.smb

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.theme.Spacing

internal data class SmbConfirmation(val expected: SmbFacts, val change: SmbChange)

@Composable
internal fun SmbConfirmationDialog(pending: SmbConfirmation, busy: Boolean, ready: Boolean,
    dismiss: () -> Unit, submit: (CharArray?) -> Unit) {
    var password by remember(pending) { mutableStateOf("") }
    var passwordAgain by remember(pending) { mutableStateOf("") }
    fun clear() { password = ""; passwordAgain = "" }
    fun close() { clear(); dismiss() }
    DisposableEffect(pending) { onDispose { clear() } }
    AlertDialog(onDismissRequest = ::close, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.smb_confirm)) }, text = {
            Column(Modifier.heightIn(max = 450.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(smbActionLabel(pending.change.kind)); pending.change.target?.let { Text(it) }
                Text(stringResource(R.string.smb_host_warning))
                pending.change.share?.let { request ->
                    Text(request.name); Text(request.path)
                    Text(stringResource(if (request.readOnly) R.string.smb_read_only else R.string.smb_read_write))
                    Text(stringResource(if (request.enabled) R.string.smb_enabled else R.string.smb_disabled))
                    if (request.enabled && SmbValidation.outsideSuggestedRoot(request.path, pending.expected.capabilities.windowsShareSecuritySupported)) Text(stringResource(R.string.smb_path_warning), color = MaterialTheme.colorScheme.error)
                    if (request.guestAllowed) Text(stringResource(R.string.smb_guest_note), color = MaterialTheme.colorScheme.error)
                    request.permissions.forEach { Text(it.principal + " · " + smbAccessLabel(it.access)) }
                }
                if (pending.change.kind == SmbChangeKind.DeleteShare) Text(stringResource(R.string.smb_delete_note))
                if (pending.change.kind == SmbChangeKind.Password) {
                    Text(stringResource(R.string.smb_password_note))
                    OutlinedTextField(password, { password = it }, enabled = !busy, singleLine = true, label = { Text(stringResource(R.string.smb_password)) }, visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
                    OutlinedTextField(passwordAgain, { passwordAgain = it }, enabled = !busy, singleLine = true, label = { Text(stringResource(R.string.smb_password_again)) }, visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
                }
            }
        }, confirmButton = {
            Button(enabled = ready && (pending.change.kind != SmbChangeKind.Password || password == passwordAgain && password.length in 12..1024 && password.none(Char::isISOControl)), onClick = {
                val secret = if (pending.change.kind == SmbChangeKind.Password) password.toCharArray() else null
                clear(); submit(secret)
            }) { Text(stringResource(R.string.smb_submit)) }
        }, dismissButton = { TextButton(onClick = ::close) { Text(stringResource(R.string.common_cancel)) } })
}
