package app.relaxkonos.mobile.ui.icons

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.collectAsStateValue

/** Shared live projection for saved login and password rows, keyed by service rather than account. */
@Composable
fun ServerPlatformBadge(serviceId: String) {
    val kind = appContainer().hostOperatingSystems.systems.collectAsStateValue()[serviceId]
    IconBadge(
        icon = hostPlatformMark(kind),
        contentDescription = hostPlatformMarkLabel(kind)?.let { stringResource(it) },
    )
}
