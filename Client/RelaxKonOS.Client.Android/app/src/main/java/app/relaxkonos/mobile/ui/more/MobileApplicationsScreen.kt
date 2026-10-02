package app.relaxkonos.mobile.ui.more

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.BuildConfig
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

@Composable
fun MobileApplicationsScreen(onBack:(()->Unit)?,onOpenRoute:(String)->Unit,modifier:Modifier=Modifier) {
    val context=LocalContext.current;val container=appContainer();val owner=container.activeSession
    var failed by remember { mutableStateOf(false) }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),verticalArrangement=Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.mobile_apps_title),onBack=onBack)
        SectionCard(stringResource(R.string.about_client_title)) {
            KeyValueRow(stringResource(R.string.about_client_version),BuildConfig.VERSION_NAME)
            Text(stringResource(R.string.mobile_apps_package,context.packageName))
            Text(stringResource(R.string.mobile_apps_os_note))
            Button(onClick={
                try { context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,Uri.fromParts("package",context.packageName,null)));failed=false }
                catch(_:Exception){failed=true}
            }) { Text(stringResource(R.string.mobile_apps_os_open)) }
            OperationMessageDialog(if (failed) stringResource(R.string.error_generic) else null, onDismiss = { failed = false })
        }
        SectionCard(stringResource(R.string.mobile_apps_builtin)) {
            Text(stringResource(R.string.mobile_apps_builtin_note))
            if(owner!=null) MobileFeatureCatalog.entries.filter{it.available(owner.capabilities)}.forEach{feature->
                TextButton(onClick={if(container.activeSession===owner&&feature.available(container.capabilities)) onOpenRoute(feature.route)}) { Text(stringResource(feature.title)) }
            }
        }
        SectionCard(stringResource(R.string.mobile_apps_remote)) {
            Text(stringResource(R.string.mobile_apps_remote_note))
            Text(stringResource(R.string.mobile_apps_packages_note))
        }
    }
}
