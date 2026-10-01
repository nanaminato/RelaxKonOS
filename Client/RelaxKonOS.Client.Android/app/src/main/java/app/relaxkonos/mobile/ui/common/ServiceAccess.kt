package app.relaxkonos.mobile.ui.common

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.ExternalServiceAddress
import app.relaxkonos.mobile.data.ExternalServiceAddresses
import app.relaxkonos.mobile.ui.theme.Spacing

/** External ACTION_VIEW only. No WebView, headers, API token, native bridge or certificate override. */
@Composable
fun ServiceAccess(addresses: List<ExternalServiceAddress>, ready: Boolean = true, stillCurrent: () -> Boolean) {
    val context = LocalContext.current
    var failed by remember(addresses) { mutableStateOf(false) }
    if (addresses.isEmpty()) return
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        Text(stringResource(R.string.service_access_note), style = MaterialTheme.typography.bodySmall)
        addresses.forEach { address ->
            Text(address.url, style = MaterialTheme.typography.bodySmall)
            if (address.loopback && address.url.startsWith("https:")) Text(stringResource(R.string.service_access_loopback_tls), style = MaterialTheme.typography.bodySmall)
            OutlinedButton(enabled = ready && ExternalServiceAddresses.valid(address.url), onClick = {
                if (stillCurrent() && ExternalServiceAddresses.valid(address.url)) {
                    try {
                        val view = Intent(Intent.ACTION_VIEW, Uri.parse(address.url)).addCategory(Intent.CATEGORY_BROWSABLE)
                        context.startActivity(Intent.createChooser(view, context.getString(R.string.service_access_open)))
                        failed = false
                    } catch (_: Exception) { failed = true }
                } else failed = true
            }) { Text(stringResource(R.string.service_access_open)) }
        }
        if (failed) Text(stringResource(R.string.service_access_failed), color = MaterialTheme.colorScheme.error)
    }
}
