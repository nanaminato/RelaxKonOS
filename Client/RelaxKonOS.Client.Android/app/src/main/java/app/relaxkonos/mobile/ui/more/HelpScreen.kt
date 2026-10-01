package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.ExternalServiceAddress
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

/** Local three-language guide; every task link names an implemented, currently available workflow. */
@Composable
fun HelpScreen(onBack:(()->Unit)?,onOpenRoute:((String)->Unit)?,onOpenServerCenter:()->Unit,modifier:Modifier=Modifier) {
    val container=appContainer();val owner=container.activeSession
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),verticalArrangement=Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.help_title),onBack=onBack)
        SectionCard(stringResource(R.string.help_connect_title)) {
            Text(stringResource(R.string.help_connect_body))
            TextButton(onClick=onOpenServerCenter){Text(stringResource(R.string.server_center_open))}
        }
        SectionCard(stringResource(R.string.help_install_title)) {
            Text(stringResource(R.string.help_install_body))
            Text(stringResource(R.string.help_packages_body))
        }
        SectionCard(stringResource(R.string.help_tasks_title)) {
            Text(stringResource(R.string.help_tasks_body))
            if(owner==null||onOpenRoute==null) Text(stringResource(R.string.help_sign_in)) else {
                MobileFeatureCatalog.entries.filter{it.available(owner.capabilities)}.forEach{feature->
                    Text(stringResource(feature.title),style=MaterialTheme.typography.titleSmall)
                    Text(stringResource(feature.description),style=MaterialTheme.typography.bodySmall)
                    TextButton(onClick={if(container.activeSession===owner&&feature.available(container.capabilities)) onOpenRoute(feature.route)}) { Text(stringResource(R.string.help_open)) }
                }
            }
        }
        SectionCard(stringResource(R.string.help_recovery_title)) { Text(stringResource(R.string.help_recovery_body)) }
        SectionCard(stringResource(R.string.help_guides_title)) {
            Text(stringResource(R.string.help_guides_body))
            // The existing desktop help:// package cannot run in Compose. Open its source guides externally.
            ServiceAccess(listOf(ExternalServiceAddress("https://github.com/nanaminato/RelaxKonOS/tree/master/examples/HelpCenter/content"))) { true }
        }
    }
}
