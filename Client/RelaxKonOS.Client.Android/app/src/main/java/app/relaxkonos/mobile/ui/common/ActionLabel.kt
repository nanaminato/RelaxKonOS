package app.relaxkonos.mobile.ui.common

import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/** A labelled action keeps its meaning visible and its parent button's touch target intact. */
@Composable
fun ActionLabel(@StringRes label: Int) {
    Row(verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        when (label) {
            R.string.common_refresh, R.string.common_retry -> DesktopIcon(DesktopIcons.refresh, size = 18.dp)
            R.string.common_delete -> DesktopIcon(DesktopIcons.delete, size = 18.dp)
            R.string.common_save -> Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(18.dp))
        }
        Text(stringResource(label))
    }
}
