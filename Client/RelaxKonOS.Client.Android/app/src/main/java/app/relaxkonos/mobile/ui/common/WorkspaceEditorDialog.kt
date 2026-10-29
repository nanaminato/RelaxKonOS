package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.relaxkonos.mobile.R

/** Bounded, keyboard-aware editor; the workspace remains a read-only summary behind the modal. */
@Composable internal fun WorkspaceEditorDialog(title: String, busy: Boolean, onClose: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    Dialog(onDismissRequest = { if (!busy) onClose() }, properties = DialogProperties(usePlatformDefaultWidth = false,
        dismissOnBackPress = !busy, dismissOnClickOutside = !busy)) {
        Surface(shape = MaterialTheme.shapes.large, modifier = Modifier.imePadding().padding(16.dp).widthIn(max = 840.dp).fillMaxWidth().fillMaxHeight(.92f)) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(title, style = MaterialTheme.typography.titleLarge)
                Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp), content = content)
                TextButton(enabled = !busy, onClick = onClose, modifier = Modifier.align(androidx.compose.ui.Alignment.End)) { Text(stringResource(R.string.common_close)) }
            }
        }
    }
}
