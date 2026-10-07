package app.relaxkonos.mobile.ui.manage.smb

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

@Composable internal fun SmbRetainedShareEditor(draft: SmbDraft, facts: SmbFacts, busy: Boolean, refresh: () -> Unit, dismiss: () -> Unit) {
    Text(stringResource(R.string.smb_unverified), color = MaterialTheme.colorScheme.error)
    TextButton(enabled = !busy, onClick = refresh) { Text(stringResource(R.string.smb_refresh_keep_draft)) }
    SmbShareEditor(draft, facts, false, {}, {}, dismiss)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun SmbShareEditor(draft: SmbDraft, facts: SmbFacts, enabled: Boolean, change: (SmbDraft) -> Unit, save: () -> Unit, dismiss: () -> Unit) {
    var advanced by remember(draft.id) { mutableStateOf(false) }
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        Text(stringResource(if (draft.id == null) R.string.smb_create else R.string.smb_edit), style = MaterialTheme.typography.titleMedium)
        SmbPanel {
        OutlinedTextField(draft.name, { change(draft.copy(name = it)) }, modifier = Modifier.fillMaxWidth(), enabled = enabled, singleLine = true, label = { Text(stringResource(R.string.smb_name)) })
        Text(stringResource(R.string.smb_folder_help), style = MaterialTheme.typography.bodySmall)
        if (enabled) RemotePathField(draft.path, { change(draft.copy(path = it)) }, R.string.smb_path, RemotePathKind.Directory)
        else Text(draft.path)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            FilterChip(draft.readOnly, { change(draft.copy(readOnly = true)) }, enabled = enabled, label = { Text(stringResource(R.string.smb_read_only)) })
            FilterChip(!draft.readOnly, { change(draft.copy(readOnly = false)) }, enabled = enabled, label = { Text(stringResource(R.string.smb_read_write)) })
        }
        Text(stringResource(if (draft.readOnly) R.string.smb_read_help else R.string.smb_write_help), style = MaterialTheme.typography.bodySmall)
        SmbCheck(draft.guestAllowed, enabled, R.string.smb_guest) { change(draft.copy(guestAllowed = it)) }
        Text(stringResource(if (draft.guestAllowed) R.string.smb_guest_note else R.string.smb_accounts_help), style = MaterialTheme.typography.bodySmall)
        }
        SmbPanel {
        Text(stringResource(R.string.smb_permissions), style = MaterialTheme.typography.titleSmall)
        Text(stringResource(if (facts.capabilities.windowsShareSecuritySupported) R.string.smb_sid_note else R.string.smb_principal_note), style = MaterialTheme.typography.bodySmall)
        if (draft.permissions.isEmpty()) Text(stringResource(R.string.smb_permission_empty), style = MaterialTheme.typography.bodySmall)
        draft.permissions.forEachIndexed { index, permission ->
            OutlinedTextField(permission.principal, { value -> change(draft.copy(permissions = draft.permissions.mapIndexed { i, old -> if (i == index) old.copy(principal = value) else old })) },
                modifier = Modifier.fillMaxWidth(), enabled = enabled, singleLine = true, label = { Text(stringResource(R.string.smb_principal)) })
            if (!facts.capabilities.windowsShareSecuritySupported && permission.principal.isBlank()) {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    facts.users.orEmpty().filter { it.eligible }.forEach { user ->
                        SuggestionChip(onClick = {
                            change(draft.copy(permissions = draft.permissions.mapIndexed { i, old -> if (i == index) old.copy(principal = user.username) else old }))
                        }, enabled = enabled, label = { Text(user.username) })
                    }
                }
            }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                SmbAccess.entries.forEach { access -> FilterChip(permission.access == access, {
                    change(draft.copy(permissions = draft.permissions.mapIndexed { i, old -> if (i == index) old.copy(access = access) else old }))
                }, enabled = enabled, label = { Text(smbAccessLabel(access)) }) }
                TextButton(enabled = enabled, onClick = { change(draft.copy(permissions = draft.permissions.filterIndexed { i, _ -> i != index })) }) { ActionLabel(R.string.common_delete) }
            }
        }
        OutlinedButton(enabled = enabled && draft.permissions.size < 128, onClick = { change(draft.copy(permissions = draft.permissions + SmbPermission("", SmbAccess.Read))) }) { Text(stringResource(R.string.smb_add_permission)) }
        }
        SmbPanel {
        TextButton(onClick = { advanced = !advanced }) { Text(stringResource(if (advanced) R.string.smb_advanced_hide else R.string.smb_advanced)) }
        if (advanced) {
            OutlinedTextField(draft.description, { change(draft.copy(description = it)) }, modifier = Modifier.fillMaxWidth(), enabled = enabled, label = { Text(stringResource(R.string.smb_description)) })
            SmbCheck(draft.enabled, enabled, R.string.smb_enabled) { change(draft.copy(enabled = it)) }
        }
        if (!draft.enabled && !advanced) Text(stringResource(R.string.smb_disabled))
        }
        Text(stringResource(R.string.smb_path_note), style = MaterialTheme.typography.bodySmall)
        val valid = SmbValidation.share(draft.request(), facts.capabilities.windowsShareSecuritySupported)
        if (!valid) Text(stringResource(R.string.smb_invalid), color = MaterialTheme.colorScheme.error)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Button(enabled = enabled && valid, onClick = save) { ActionLabel(R.string.common_save) }
            TextButton(onClick = dismiss) { Text(stringResource(R.string.common_cancel)) }
        }
    }
}
@Composable internal fun SmbCheck(value: Boolean, enabled: Boolean, label: Int, change: (Boolean) -> Unit) {
    CheckboxOption(value, stringResource(label), enabled, change)
}
