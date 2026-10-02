package app.relaxkonos.mobile.ui.common

import androidx.annotation.StringRes
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.Saver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import app.relaxkonos.mobile.ui.theme.Spacing

data class WorkspaceDestination(val id: String, @param:StringRes val title: Int)

/** Stable, reachable navigation above the content, including narrow screens and large text. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun WorkspaceNavigation(pages: List<WorkspaceDestination>, selected: String, onSelect: (String) -> Unit) {
    val index = pages.indexOfFirst { it.id == selected }.coerceAtLeast(0)
    PrimaryScrollableTabRow(selectedTabIndex = index, edgePadding = Spacing.sm) {
        pages.forEach { page ->
            Tab(selected = page.id == selected, onClick = { onSelect(page.id) },
                text = { Text(stringResource(page.title)) })
        }
    }
}

private val ScrollsSaver = Saver<MutableMap<String, ScrollState>, Map<String, Int>>(
    save = { states -> states.mapValues { it.value.value } },
    restore = { positions -> positions.mapValues { ScrollState(it.value) }.toMutableMap() },
)

/** Each category owns its scroll position; title and category navigation never scroll away. */
@Composable
fun WorkspaceColumn(
    title: String, onBack: (() -> Unit)?, pages: List<WorkspaceDestination>, selected: String,
    onSelect: (String) -> Unit, modifier: Modifier = Modifier, stateKey: Any? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    key(stateKey) {
    val scrolls = rememberSaveable(saver = ScrollsSaver) { mutableMapOf<String, ScrollState>() }
    Column(modifier.fillMaxSize().imePadding()) {
        ScreenHeader(title, onBack = onBack, modifier = Modifier.padding(Spacing.lg))
        WorkspaceNavigation(pages, selected, onSelect)
        Column(Modifier.weight(1f).verticalScroll(scrolls.getOrPut(selected) { ScrollState(0) }).padding(Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Spacing.md), content = content)
    }
    }
}

/** Keep visited forms and selection composed, but hidden pages have no layout or accessibility nodes.
 * Category switching cannot recreate operation IDs, erase drafts or restart a pending submission.
 * The caller must key the workspace by its server/account and gate any page-specific BackHandler.
 */
@Composable
fun WorkspaceSection(visible: Boolean, modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    var visited by rememberSaveable { mutableStateOf(visible) }
    if (visible) visited = true
    if (!visited) return
    Layout(modifier = if (visible) modifier else Modifier.clearAndSetSemantics {},
        content = { Column(verticalArrangement = Arrangement.spacedBy(Spacing.md), content = content) }) { children, constraints ->
        if (visible) {
            val child = children.single().measure(constraints)
            layout(child.width, child.height) { child.placeRelative(0, 0) }
        } else layout(0, 0) {}
    }
}
