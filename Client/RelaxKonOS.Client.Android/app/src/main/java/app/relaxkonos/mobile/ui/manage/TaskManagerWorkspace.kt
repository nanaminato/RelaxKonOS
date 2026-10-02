package app.relaxkonos.mobile.ui.manage

import androidx.compose.foundation.layout.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.monitor.MonitorScreen
import app.relaxkonos.mobile.ui.manage.processes.ProcessesScreen

@Composable
fun TaskManagerWorkspace(initialSection: String, onBack: (() -> Unit)?, modifier: Modifier = Modifier) {
    val owner = appContainer().activeSession
    val pages = buildList {
        if (ServerCapabilities.METRICS in appContainer().capabilities) add(WorkspaceDestination("performance", R.string.workspace_performance))
        if (ServerCapabilities.PROCESSES in appContainer().capabilities) add(WorkspaceDestination("processes", R.string.workspace_processes))
    }
    if (pages.isEmpty()) return
    var section by rememberSaveable(owner) { mutableStateOf(initialSection) }
    LaunchedEffect(initialSection) { section = initialSection }
    val selected = section.takeIf { id -> pages.any { it.id == id } } ?: pages.first().id
    Column(modifier.fillMaxSize()) {
        WorkspaceNavigation(pages, selected) { section = it }
        key(owner) { Box(Modifier.weight(1f)) {
            WorkspaceSection(selected == "performance", Modifier.fillMaxSize()) { MonitorScreen(onBack, Modifier.fillMaxSize(), active = selected == "performance") }
            WorkspaceSection(selected == "processes", Modifier.fillMaxSize()) { ProcessesScreen(onBack, Modifier.fillMaxSize(), active = selected == "processes") }
        } }
    }
}
