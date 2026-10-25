package app.relaxkonos.mobile.ui.common

import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
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
            Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) { ApplicationTiles(matches) }
        }
    }
}

/** Readable icon grid shared by home and search; font scaling reduces the column count. */
@Composable
internal fun ApplicationTiles(applications: List<CatalogApplication>, modifier: Modifier = Modifier) {
    val fontScale = LocalDensity.current.fontScale.coerceAtLeast(1f)
    BoxWithConstraints(modifier.fillMaxWidth()) {
        val columns = (maxWidth.value / (78 * fontScale)).toInt().coerceIn(1, 8)
        Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            applications.chunked(columns).forEach { row ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    row.forEach { app ->
                        Column(
                            Modifier.weight(1f).clickable(role = Role.Button, onClick = app.open)
                                .semantics(mergeDescendants = true) { contentDescription = app.description }
                                .padding(vertical = Spacing.sm),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(Spacing.sm),
                        ) {
                            Surface(shape = MaterialTheme.shapes.large, color = MaterialTheme.colorScheme.surfaceContainerLowest) {
                                Box(Modifier.size(56.dp), contentAlignment = Alignment.Center) { DesktopIcon(app.icon, size = 32.dp) }
                            }
                            Text(app.title, style = MaterialTheme.typography.labelLarge, textAlign = TextAlign.Center)
                        }
                    }
                    repeat(columns - row.size) { Spacer(Modifier.weight(1f)) }
                }
            }
        }
    }
}
