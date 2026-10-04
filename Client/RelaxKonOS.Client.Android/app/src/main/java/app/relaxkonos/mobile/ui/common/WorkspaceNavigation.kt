package app.relaxkonos.mobile.ui.common

import androidx.annotation.StringRes
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.Saver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.unit.dp
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.Dp
import app.relaxkonos.mobile.ui.theme.Spacing

data class WorkspaceDestination(val id: String, @param:StringRes val title: Int)

internal class WorkspaceHeader(val screenTitle: String, initialBack: (() -> Unit)?) {
    var back: (() -> Unit)? = initialBack
    var hasBack by mutableStateOf(initialBack != null)
}

internal val LocalWorkspaceHeader = compositionLocalOf<WorkspaceHeader?> { null }

/** The workspace owns the title and navigation; the active page owns its back/leave policy. */
@Composable
fun WorkspaceFrame(
    title: String, screenTitle: String = title, subtitle: String? = null,
    pages: List<WorkspaceDestination>, selected: String, onSelect: (String) -> Unit,
    onBack: (() -> Unit)?, modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val header = remember(selected, screenTitle) { WorkspaceHeader(screenTitle, onBack) }
    Column(modifier.fillMaxSize().imePadding()) {
        ScreenHeader(title, subtitle = subtitle, onBack = if (header.hasBack) ({ header.back?.invoke() }) else null,
            modifier = Modifier.padding(Spacing.lg))
        WorkspaceBody(pages, selected, onSelect, Modifier.weight(1f)) {
            CompositionLocalProvider(LocalWorkspaceHeader provides header) { content() }
        }
    }
}

/** Stable, reachable navigation above the content, including narrow screens and large text. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun WorkspaceNavigation(pages: List<WorkspaceDestination>, selected: String, onSelect: (String) -> Unit) {
    if (pages.isEmpty()) return
    val focus = LocalFocusManager.current
    val index = pages.indexOfFirst { it.id == selected }.coerceAtLeast(0)
    PrimaryScrollableTabRow(selectedTabIndex = index, edgePadding = Spacing.lg,
        containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
        divider = {}) {
        pages.forEach { page ->
            Tab(selected = page.id == selected, onClick = { focus.clearFocus(); onSelect(page.id) },
                text = { Text(stringResource(page.title)) })
        }
    }
}

/** Use available pane width, not device identity: split windows retain reachable tabs. */
@Composable
private fun WorkspaceBody(
    pages: List<WorkspaceDestination>, selected: String, onSelect: (String) -> Unit,
    modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit,
) {
    val focus = LocalFocusManager.current
    BoxWithConstraints(modifier.fillMaxWidth()) {
        val wide = maxWidth >= 1000.dp * LocalDensity.current.fontScale.coerceAtLeast(1f)
        Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(if (wide) Spacing.md else 0.dp)) {
            if (wide && pages.isNotEmpty()) {
                Surface(
                    modifier = Modifier.width(220.dp).fillMaxHeight(),
                    shape = MaterialTheme.shapes.large,
                    color = MaterialTheme.colorScheme.surfaceContainerLow,
                ) {
                    Column(Modifier.verticalScroll(rememberScrollState()).padding(Spacing.sm),
                        verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                        pages.forEach { page ->
                            NavigationDrawerItem(
                                label = { Text(stringResource(page.title)) },
                                selected = page.id == selected,
                                onClick = { focus.clearFocus(); onSelect(page.id) },
                                modifier = Modifier.fillMaxWidth(),
                                shape = MaterialTheme.shapes.medium,
                            )
                        }
                    }
                }
            }
            Column(Modifier.weight(1f).fillMaxHeight()) {
                if (!wide) WorkspaceNavigation(pages, selected, onSelect)
                content()
            }
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
    contentSpacing: Dp = Spacing.md,
    contentPadding: PaddingValues = PaddingValues(Spacing.lg),
    content: @Composable ColumnScope.() -> Unit,
) {
    key(stateKey) {
    val scrolls = rememberSaveable(saver = ScrollsSaver) { mutableMapOf<String, ScrollState>() }
    Column(modifier.fillMaxSize().imePadding()) {
        ScreenHeader(title, onBack = onBack, modifier = Modifier.padding(Spacing.lg))
        WorkspaceBody(pages, selected, onSelect, Modifier.weight(1f)) {
            Column(Modifier.weight(1f).verticalScroll(scrolls.getOrPut(selected) { ScrollState(0) }).padding(contentPadding),
                verticalArrangement = Arrangement.spacedBy(contentSpacing), content = content)
        }
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
        content = {
            CompositionLocalProvider(LocalWorkspaceHeader provides LocalWorkspaceHeader.current.takeIf { visible }) {
                Column(verticalArrangement = Arrangement.spacedBy(Spacing.md), content = content)
            }
        }) { children, constraints ->
        if (visible) {
            val child = children.single().measure(constraints)
            layout(child.width, child.height) { child.placeRelative(0, 0) }
        } else layout(0, 0) {}
    }
}
