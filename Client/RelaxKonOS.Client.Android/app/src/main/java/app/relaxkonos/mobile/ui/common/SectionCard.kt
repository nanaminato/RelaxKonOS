package app.relaxkonos.mobile.ui.common

import androidx.annotation.DrawableRes
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * A titled card. Used so every destination groups its content the same way across breakpoints.
 *
 * The card is drawn as a near-white surface with a hairline outline and no shadow. Elevation on a
 * tinted backdrop produces a grey halo that hurts the text above it, so separation comes from the
 * surface step and the border instead — which also survives a dark theme unchanged.
 *
 * [leading] is the desktop icon shown beside the title; the app has exactly one icon vocabulary, so
 * there is one parameter for it rather than one per glyph source.
 */
@Composable
fun SectionCard(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    @DrawableRes leading: Int? = null,
    trailing: @Composable (() -> Unit)? = null,
    contentSpacing: Dp = Spacing.md,
    content: @Composable ColumnScope.() -> Unit,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Radius.lg),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLowest),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
    ) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(contentSpacing)) {
            BoxWithConstraints(Modifier.fillMaxWidth()) {
                val narrow = trailing != null && maxWidth < 360.dp * LocalDensity.current.fontScale.coerceAtLeast(1f)
                Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(Spacing.md),
                    ) {
                        if (leading != null) {
                            IconBadge(leading)
                        }
                        Column(Modifier.weight(1f)) {
                            Text(title, style = MaterialTheme.typography.titleMedium,
                                modifier = Modifier.semantics { heading() })
                            if (subtitle != null) {
                                Text(
                                    subtitle,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                        if (!narrow) trailing?.invoke()
                    }
                    if (narrow) {
                        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) { trailing?.invoke() }
                    }
                }
            }
            content()
        }
    }
}

/**
 * A card that only groups, with no heading of its own.
 *
 * Used where the page header already names the group — a settings list under "More" does not need the
 * word "More" repeated inside it. Padding is tighter than [SectionCard] because the rows bring their own
 * inset, which is what makes the group read as one list rather than as a stack of tiles.
 */
@Composable
fun SectionGroup(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Radius.lg),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLowest),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
    ) {
        Column(Modifier.padding(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            content()
        }
    }
}

/**
 * The caption above a [SectionGroup].
 *
 * A settings page is a header, then a few captioned groups. Naming the group from outside the surface
 * keeps the card itself free of chrome, which is what makes a group of rows read as one block rather
 * than as a titled tile.
 */
@Composable
fun SectionLabel(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
        modifier = modifier.padding(start = Spacing.md, bottom = Spacing.xs).semantics { heading() },
    )
}

/**
 * A label/value line.
 *
 * Both columns wrap: paths, fingerprints and addresses must remain readable on detail pages.
 */
@Composable
fun KeyValueRow(label: String, value: String, modifier: Modifier = Modifier) {
    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(0.35f).padding(end = Spacing.md),
        )
        Text(
            value,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.weight(0.65f),
        )
    }
}

/**
 * Hint shown inside a card when a region has nothing to show.
 *
 * It stays left-aligned and inline on purpose: this is the "nothing here yet" line under a heading
 * that is already on screen, not the [EmptyState] that fills an otherwise blank pane.
 */
@Composable
fun EmptyHint(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        modifier = modifier.fillMaxWidth().padding(vertical = Spacing.md),
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        style = MaterialTheme.typography.bodyMedium,
    )
}
