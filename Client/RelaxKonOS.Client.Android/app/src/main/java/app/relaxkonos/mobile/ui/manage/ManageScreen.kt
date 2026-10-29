package app.relaxkonos.mobile.ui.manage

import androidx.compose.foundation.layout.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

/** Complete capability-filtered application directory, shared with the home launcher. */
@Composable
fun ManageScreen(onOpenRoute: (String) -> Unit, modifier: Modifier = Modifier) {
    // The component-center entry depends on the current session's host surface, so the directory is
    // built from the active session rather than from a container-wide capability set.
    val session = appContainer().activeSession
    val applications = if (session == null) emptyList()
        else mobileApplications(session.capabilities, session.privilegedOperations, onOpenRoute)
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.lg)) {
        ScreenHeader(title = stringResource(R.string.nav_manage))
        ApplicationCatalog(applications, Modifier.weight(1f))
    }
}
