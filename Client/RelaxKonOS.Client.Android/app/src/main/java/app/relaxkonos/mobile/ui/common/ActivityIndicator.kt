package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Close
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.theme.Spacing

/** Inline feedback keeps the current page visible while a request is in flight. */
@Composable
fun ActivityIndicator(text: String, modifier: Modifier = Modifier) {
    Row(
        modifier.semantics(mergeDescendants = true) {},
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
    ) {
        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
        Text(text, style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Task progress is animated; a long-lived service uses a static running mark instead. */
@Composable
fun ExecutionStatusChip(text: String, state: String?, modifier: Modifier = Modifier, task: Boolean = true) {
    val normalized = state?.lowercase()
    val busy = normalized in setOf("queued", "starting", "stopping", "installing", "cancelling", "pending", "backoff") ||
        task && normalized == "running"
    val tone = when (normalized) {
        "running" -> StatusTone.Primary
        "succeeded", "verified", "healthy", "available" -> StatusTone.Success
        "failed", "partialfailed", "timedout", "crashloop", "unhealthy" -> StatusTone.Danger
        "interrupted", "degraded", "disconnected", "backoff" -> StatusTone.Warning
        "queued", "starting", "stopping", "installing", "cancelling", "pending" -> StatusTone.Info
        else -> StatusTone.Neutral
    }
    if (normalized == "running" && !task) {
        StatusChip(text, tone, modifier, icon = Icons.Default.PlayArrow)
    } else if (normalized in setOf("stopped", "cancelled")) {
        StatusChip(text, tone, modifier, icon = Icons.Default.Close)
    } else {
        StatusChip(text, tone, modifier, busy = busy)
    }
}
