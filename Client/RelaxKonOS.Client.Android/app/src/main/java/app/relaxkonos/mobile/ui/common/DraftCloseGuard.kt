package app.relaxkonos.mobile.ui.common

import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R

/** All close entry points share the same busy gate and explicit discard decision. */
@Composable
internal fun DraftCloseGuard(enabled: Boolean, dirty: Boolean, onClose: () -> Unit,
    content: @Composable (requestClose: () -> Unit) -> Unit) {
    var confirming by remember { mutableStateOf(false) }
    content { if (enabled) { if (dirty) confirming = true else onClose() } }
    if (confirming) AlertDialog(onDismissRequest = { confirming = false },
        title = { Text(stringResource(R.string.ui_discard_draft_title)) },
        confirmButton = { TextButton(enabled = enabled, onClick = { confirming = false; onClose() }) { Text(stringResource(R.string.editor_discard_changes)) } },
        dismissButton = { TextButton(onClick = { confirming = false }) { Text(stringResource(R.string.common_cancel)) } })
}
