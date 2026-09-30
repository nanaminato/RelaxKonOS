package app.relaxkonos.mobile.ui.manage.smb

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun SmbShareEditor(draft: SmbDraft, facts: SmbFacts, enabled: Boolean, change: (SmbDraft) -> Unit, save: () -> Unit, dismiss: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        Text(stringResource(if (draft.id == null) R.string.smb_create else R.string.smb_edit), style = MaterialTheme.typography.titleMedium)
        OutlinedTextField(draft.name, { change(draft.copy(name = it)) }, enabled = enabled, singleLine = true, label = { Text(stringResource(R.string.smb_name)) })
        if (enabled) RemotePathField(draft.path, { change(draft.copy(path = it)) }, R.string.smb_path, RemotePathKind.Directory)
        else Text(draft.path)
        OutlinedTextField(draft.description, { change(draft.copy(description = it)) }, enabled = enabled, label = { Text(stringResource(R.string.smb_description)) })
        SmbCheck(draft.readOnly, enabled, R.string.smb_read_only) { change(draft.copy(readOnly = it)) }
        SmbCheck(draft.enabled, enabled, R.string.smb_enabled) { change(draft.copy(enabled = it)) }
        SmbCheck(draft.guestAllowed, enabled, R.string.smb_guest) { change(draft.copy(guestAllowed = it)) }
        if (draft.guestAllowed) Text(stringResource(R.string.smb_guest_note), style = MaterialTheme.typography.bodySmall)
        Text(stringResource(R.string.smb_permissions), style = MaterialTheme.typography.titleSmall)
        Text(stringResource(if (facts.capabilities.windowsShareSecuritySupported) R.string.smb_sid_note else R.string.smb_principal_note), style = MaterialTheme.typography.bodySmall)
        draft.permissions.forEachIndexed { index, permission ->
            OutlinedTextField(permission.principal, { value -> change(draft.copy(permissions = draft.permissions.mapIndexed { i, old -> if (i == index) old.copy(principal = value) else old })) },
                enabled = enabled, singleLine = true, label = { Text(stringResource(R.string.smb_principal)) })
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                SmbAccess.entries.forEach { access -> FilterChip(permission.access == access, {
                    change(draft.copy(permissions = draft.permissions.mapIndexed { i, old -> if (i == index) old.copy(access = access) else old }))
                }, enabled = enabled, label = { Text(smbAccessLabel(access)) }) }
                TextButton(enabled = enabled, onClick = { change(draft.copy(permissions = draft.permissions.filterIndexed { i, _ -> i != index })) }) { Text(stringResource(R.string.common_delete)) }
            }
        }
        OutlinedButton(enabled = enabled && draft.permissions.size < 128, onClick = { change(draft.copy(permissions = draft.permissions + SmbPermission("", SmbAccess.Read))) }) { Text(stringResource(R.string.smb_add_permission)) }
        Text(stringResource(R.string.smb_path_note), style = MaterialTheme.typography.bodySmall)
        val valid = SmbValidation.share(draft.request(), facts.capabilities.windowsShareSecuritySupported)
        if (!valid) Text(stringResource(R.string.smb_invalid), color = MaterialTheme.colorScheme.error)
        Button(enabled = enabled && valid, onClick = save) { Text(stringResource(R.string.common_save)) }
        TextButton(onClick = dismiss) { Text(stringResource(R.string.common_cancel)) }
    }
}
@Composable internal fun SmbCheck(value: Boolean, enabled: Boolean, label: Int, change: (Boolean) -> Unit) {
    Row { Checkbox(value, change, enabled = enabled); Text(stringResource(label)) }
}
