package app.relaxkonos.mobile.ui.manage.git

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.theme.Spacing

/** Shared visual hierarchy for Git pages; collapsed forms retain their parent's drafts. */
@Composable
internal fun GitPanel(title: String, collapsible: Boolean = false, initiallyExpanded: Boolean = true,
    content: @Composable ColumnScope.() -> Unit) {
    var expanded by rememberSaveable { mutableStateOf(initiallyExpanded) }
    OutlinedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            if (collapsible) {
                TextButton(onClick = { expanded = !expanded }, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(title, modifier = Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
                        Text(stringResource(if (expanded) R.string.git_collapse else R.string.git_expand))
                    }
                }
            } else Text(title, style = MaterialTheme.typography.titleMedium)
            if (!collapsible || expanded) content()
        }
    }
}

@Composable
internal fun GitCode(text: String) {
    Text(text, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace)
}

@Composable
internal fun GitAdaptiveColumns(showSecond: Boolean, first: @Composable () -> Unit, second: @Composable () -> Unit) {
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        if (maxWidth >= 840.dp && showSecond) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.md)) { first() }
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.md)) { second() }
            }
        } else Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            first()
            if (showSecond) second()
        }
    }
}

@Composable
internal fun GitDetailDialog(title: String, onClose: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(Modifier.fillMaxSize()) {
            Column(Modifier.safeDrawingPadding().padding(Spacing.md)) {
                ScreenHeader(title, onBack = onClose)
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(Spacing.md), content = content)
            }
        }
    }
}

@Composable
internal fun gitBuildStateLabel(state: String): String = stringResource(when (state) {
    "queued" -> R.string.installation_state_queued
    "running" -> R.string.installation_state_running
    "succeeded" -> R.string.installation_state_succeeded
    "failed" -> R.string.installation_state_failed
    "cancelled" -> R.string.installation_state_cancelled
    else -> R.string.git_build_interrupted
})
