package app.relaxkonos.mobile.ui.common

import androidx.annotation.DrawableRes
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.theme.Layout
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * A square container for a leading glyph.
 *
 * Icons dropped bare next to a title read as decoration; giving them a container makes a list scan as
 * a set of distinct entries.
 *
 * The container is a neutral surface step rather than a tinted accent, and the glyph is never tinted:
 * the artwork comes from the desktop set and carries its own colours (see `ui/icons/DesktopIcons.kt`).
 * A blue container under a yellow folder is what a tinted badge would produce here.
 */
@Composable
fun IconBadge(
    @DrawableRes icon: Int,
    modifier: Modifier = Modifier,
    contentDescription: String? = null,
) {
    Box(
        modifier = modifier
            .size(Layout.iconBadge)
            .background(MaterialTheme.colorScheme.surfaceContainerHigh, RoundedCornerShape(Radius.md)),
        contentAlignment = Alignment.Center,
    ) {
        DesktopIcon(icon = icon, size = 22.dp, contentDescription = contentDescription)
    }
}

/**
 * One entry in a list.
 *
 * The three text slots are deliberately distinct rather than one concatenated string: [subtitle] is
 * identity ("what is this"), [supporting] is detail ("size, date, account"). Joining them with
 * separators — which the previous implementation did — makes a long path push the facts that matter off
 * the line.
 *
 * [onClick] is what makes the row interactive; passing `null` leaves a static row, which is what a
 * read-only settings summary wants.
 */
@Composable
fun ListRow(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    supporting: String? = null,
    leading: (@Composable () -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
    onClick: (() -> Unit)? = null,
    selected: Boolean = false,
) {
    val shape = RoundedCornerShape(Radius.md)
    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(
                color = if (selected) MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.45f) else Color.Transparent,
                shape = shape,
            )
            .let { if (onClick != null) it.clip(shape).clickable(onClick = onClick) else it }
            .padding(horizontal = Spacing.sm, vertical = Spacing.sm + 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        leading?.invoke()
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                title,
                style = MaterialTheme.typography.bodyLarge,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (subtitle != null) {
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
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
        trailing?.invoke()
    }
}
