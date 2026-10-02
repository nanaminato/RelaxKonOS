package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import app.relaxkonos.mobile.ui.theme.Spacing

/** Groups current facts and their controls without introducing another heading. */
@Composable fun ManagementCard(content: @Composable ColumnScope.() -> Unit) {
    OutlinedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm), content = content)
    }
}
