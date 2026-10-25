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
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.lg)) {
        ScreenHeader(title = stringResource(R.string.nav_manage))
        ApplicationCatalog(mobileApplications(appContainer().capabilities, onOpenRoute), Modifier.weight(1f))
    }
}
