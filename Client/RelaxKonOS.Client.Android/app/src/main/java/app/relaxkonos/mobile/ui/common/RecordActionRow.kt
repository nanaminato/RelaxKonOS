package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.theme.Spacing

/** Keep record identity readable before offering actions that affect that identity. */
@Composable
internal fun RecordActionRow(
    leading: @Composable () -> Unit,
    actions: @Composable () -> Unit,
    content: @Composable ColumnScope.() -> Unit,
) {
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val stacked = maxWidth < 480.dp * LocalDensity.current.fontScale.coerceAtLeast(1f)
        Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                leading()
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs), content = content)
                if (!stacked) actions()
            }
            if (stacked) Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) { actions() }
        }
    }
}
