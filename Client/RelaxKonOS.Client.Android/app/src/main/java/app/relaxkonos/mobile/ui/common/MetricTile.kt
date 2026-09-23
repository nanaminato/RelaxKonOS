package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * One reading, given the weight it deserves.
 *
 * A label-and-value row makes every number look equally important and equally small. A tile puts the
 * number first, names it second, and — when the server reported a total — shows how full the reading
 * is, which is what turns "8.2 GB used" into something a glance can judge.
 *
 * It is intentionally chrome-free: tiles are grouped two-up inside a [SectionCard], and a card inside a
 * card would read as two nested containers rather than one group.
 */
@Composable
fun MetricTile(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
    supporting: String? = null,
    progress: Float? = null,
    tone: StatusTone = StatusTone.Primary,
) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            value,
            style = MaterialTheme.typography.headlineSmall,
            color = MaterialTheme.colorScheme.onSurface,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        if (progress != null) {
            MetricTrack(progress = progress, tone = tone)
        }
        if (supporting != null) {
            Text(
                supporting,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/**
 * A thin usage track.
 *
 * Both stops are theme tokens: the groove is a neutral surface so it stays visible on any card, and the
 * filled part takes the tone's accent. The fraction is clamped, so an over-100% server reading cannot
 * draw outside the track.
 */
@Composable
fun MetricTrack(
    progress: Float,
    tone: StatusTone,
    modifier: Modifier = Modifier,
) {
    val fraction = progress.coerceIn(0f, 1f)
    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(6.dp)
            .clip(RoundedCornerShape(Radius.pill))
            .background(MaterialTheme.colorScheme.surfaceContainerHighest),
    ) {
        Box(
            Modifier
                .fillMaxWidth(fraction)
                .fillMaxHeight()
                .clip(RoundedCornerShape(Radius.pill))
                .background(toneAccent(tone)),
        )
    }
}

/**
 * A file-system reading: mount point, usage, and how full it is.
 *
 * Disks are a list rather than a tile grid because their count is not fixed — a host can report one
 * mount point or twenty, and a two-up grid would reflow unpredictably.
 */
@Composable
fun DiskRow(
    name: String,
    usedLabel: String,
    fraction: Float,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth().padding(vertical = Spacing.xs),
        verticalArrangement = Arrangement.spacedBy(Spacing.xs + 2.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            Text(
                name,
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Text(
                usedLabel,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        MetricTrack(progress = fraction, tone = loadTone(fraction))
    }
}
