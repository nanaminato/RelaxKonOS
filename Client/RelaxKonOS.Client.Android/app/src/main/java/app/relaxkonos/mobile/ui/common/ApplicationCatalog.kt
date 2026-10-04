package app.relaxkonos.mobile.ui.common

import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

internal data class CatalogApplication(
    val id: String, val title: String, val description: String,
    @param:DrawableRes val icon: Int, val open: () -> Unit,
)

/** Search is local; capability-filtered applications are the only entries supplied by the caller. */
@Composable
internal fun ApplicationCatalog(applications: List<CatalogApplication>, modifier: Modifier = Modifier) {
    var query by rememberSaveable { mutableStateOf("") }
    val matches = applications.filter {
        query.isBlank() || it.title.contains(query.trim(), ignoreCase = true) ||
            it.description.contains(query.trim(), ignoreCase = true)
    }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(Spacing.lg)) {
        OutlinedTextField(
            value = query, onValueChange = { query = it },
            modifier = Modifier.fillMaxWidth(), singleLine = true,
            label = { Text(stringResource(R.string.manage_search_applications)) },
            leadingIcon = { DesktopIcon(DesktopIcons.search, size = 20.dp) },
            shape = MaterialTheme.shapes.large,
        )
        if (matches.isEmpty()) {
            EmptyState(stringResource(R.string.manage_search_empty), icon = DesktopIcons.search)
        } else {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(240.dp * LocalDensity.current.fontScale.coerceAtLeast(1f)),
                modifier = Modifier.weight(1f).fillMaxWidth(),
                contentPadding = PaddingValues(bottom = Spacing.xl),
                horizontalArrangement = Arrangement.spacedBy(Spacing.md),
                verticalArrangement = Arrangement.spacedBy(Spacing.md),
            ) {
                items(matches, key = { it.id }) { application ->
                    OutlinedCard(
                        onClick = application.open,
                        modifier = Modifier.fillMaxWidth(),
                        colors = CardDefaults.outlinedCardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainerLowest),
                        shape = MaterialTheme.shapes.large,
                    ) {
                        Column(Modifier.padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                                IconBadge(application.icon)
                                Spacer(Modifier.weight(1f))
                                DesktopIcon(DesktopIcons.disclosure, size = 20.dp)
                            }
                            Text(application.title, style = MaterialTheme.typography.titleMedium)
                            Text(application.description, style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
}
