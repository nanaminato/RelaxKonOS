package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.theme.Spacing

data class TransferProgress(val fileName: String, val bytes: Long = 0, val total: Long? = null) {
    val fraction: Float? get() = total?.takeIf { it > 0 }?.let { (bytes.toDouble() / it).coerceIn(0.0, 1.0).toFloat() }
}

@Composable
fun TransferProgressDialog(title: String, transfer: TransferProgress, onCancel: () -> Unit) {
    AlertDialog(
        onDismissRequest = {},
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(transfer.fileName)
                TransferProgressContent(transfer)
            }
        },
        confirmButton = { TextButton(onClick = onCancel) { Text(stringResource(R.string.common_cancel)) } },
    )
}

@Composable
fun TransferProgressContent(transfer: TransferProgress) {
    val fraction = transfer.fraction
    if (fraction == null) LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
    else {
        LinearProgressIndicator(progress = { fraction }, modifier = Modifier.fillMaxWidth())
        Text("${(fraction * 100).toInt()}%")
    }
    Text(if (transfer.total != null) stringResource(R.string.files_upload_progress,
        formatSize(transfer.bytes).orEmpty(), formatSize(transfer.total).orEmpty())
    else formatSize(transfer.bytes).orEmpty())
}
