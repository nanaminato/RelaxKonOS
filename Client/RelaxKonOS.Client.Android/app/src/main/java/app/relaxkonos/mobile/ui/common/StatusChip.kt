package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.ui.theme.relaxKon

/**
 * Severity of a short state label.
 *
 * A screen never picks a raw colour for a status: it picks a tone, and the palette decides what that
 * means in light, dark and high contrast. That is what keeps "warning" readable without the screen
 * knowing which theme it is painting in.
 */
enum class StatusTone { Neutral, Primary, Success, Warning, Danger, Info }

/**
 * A pill for a short state word — "current connection", "stale", "saved by fingerprint".
 *
 * Colour is never the only carrier: the caller always passes the words too, so the chip stays readable
 * to someone who cannot distinguish the tints.
 */
@Composable
fun StatusChip(
    text: String,
    tone: StatusTone,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
) {
    val container = toneContainer(tone)
    val content = toneContent(tone)
    Row(
        modifier = modifier
            .background(container, RoundedCornerShape(Radius.pill))
            .padding(horizontal = Spacing.md, vertical = Spacing.xs + 1.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Spacing.xs + 2.dp),
    ) {
        if (icon != null) {
            Icon(icon, contentDescription = null, tint = content, modifier = Modifier.size(14.dp))
        }
        Text(text, style = MaterialTheme.typography.labelMedium, color = content)
    }
}

/** Background of a tone's capsule. Also used by the progress tracks so a bar matches its chip. */
@Composable
internal fun toneContainer(tone: StatusTone): Color = when (tone) {
    StatusTone.Neutral -> MaterialTheme.colorScheme.surfaceContainerHighest
    StatusTone.Primary -> MaterialTheme.colorScheme.primaryContainer
    StatusTone.Success -> MaterialTheme.relaxKon.successContainer
    StatusTone.Warning -> MaterialTheme.relaxKon.warningContainer
    StatusTone.Danger -> MaterialTheme.colorScheme.errorContainer
    StatusTone.Info -> MaterialTheme.relaxKon.infoContainer
}

/** Foreground of a tone. Readable on [toneContainer]. */
@Composable
internal fun toneContent(tone: StatusTone): Color = when (tone) {
    StatusTone.Neutral -> MaterialTheme.colorScheme.onSurfaceVariant
    StatusTone.Primary -> MaterialTheme.colorScheme.onPrimaryContainer
    StatusTone.Success -> MaterialTheme.relaxKon.onSuccessContainer
    StatusTone.Warning -> MaterialTheme.relaxKon.onWarningContainer
    StatusTone.Danger -> MaterialTheme.colorScheme.onErrorContainer
    StatusTone.Info -> MaterialTheme.relaxKon.onInfoContainer
}

/** Saturated accent of a tone, for progress bars and small marks that must stand out on a card. */
@Composable
internal fun toneAccent(tone: StatusTone): Color = when (tone) {
    StatusTone.Neutral -> MaterialTheme.colorScheme.outline
    StatusTone.Primary -> MaterialTheme.colorScheme.primary
    StatusTone.Success -> MaterialTheme.relaxKon.success
    StatusTone.Warning -> MaterialTheme.relaxKon.warning
    StatusTone.Danger -> MaterialTheme.colorScheme.error
    StatusTone.Info -> MaterialTheme.relaxKon.info
}

/**
 * Turns a 0..1 reading into the tone it deserves.
 *
 * Host metrics have no universally correct thresholds, so the boundaries are stated once here rather
 * than guessed per screen: below 70% is nominal, up to 90% is worth noticing, above it is a problem.
 */
fun loadTone(fraction: Float): StatusTone = when {
    fraction >= 0.9f -> StatusTone.Danger
    fraction >= 0.7f -> StatusTone.Warning
    else -> StatusTone.Success
}
