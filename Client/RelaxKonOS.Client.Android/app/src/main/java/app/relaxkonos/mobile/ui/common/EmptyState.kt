package app.relaxkonos.mobile.ui.common

import androidx.annotation.DrawableRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * What a region shows when it has nothing in it yet.
 *
 * A bare sentence in the top-left corner of an empty pane reads as a rendering failure. Centring the
 * message under its glyph reads as an answer, and it keeps the "not loaded yet" and "genuinely empty"
 * cases visually distinct from a list that is simply short.
 *
 * The glyph is a desktop asset, so it is drawn untinted inside a neutral disc — the disc exists to give
 * the colourful artwork something to sit on, not to recolour it.
 */
@Composable
fun EmptyState(
    text: String,
    modifier: Modifier = Modifier,
    @DrawableRes icon: Int? = null,
    action: (@Composable () -> Unit)? = null,
) {
    Column(
        modifier = modifier.fillMaxWidth().padding(Spacing.xl),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        if (icon != null) {
            Box(
                modifier = Modifier
                    .size(56.dp)
                    .background(MaterialTheme.colorScheme.surfaceContainerHighest, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                DesktopIcon(icon = icon, size = 28.dp)
            }
        }
        Text(
            text,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        action?.invoke()
    }
}
