package app.relaxkonos.mobile.ui.theme

import androidx.activity.compose.LocalActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

/** Match the activity's system icons to the resolved app palette, including explicit theme choices. */
@Composable
internal fun ActivitySystemBars() {
    val window = LocalActivity.current?.window ?: return
    val view = LocalView.current
    val lightSurface = MaterialTheme.colorScheme.surface.luminance() > 0.5f
    SideEffect {
        WindowCompat.getInsetsController(window, view).apply {
            isAppearanceLightStatusBars = lightSurface
            isAppearanceLightNavigationBars = lightSurface
        }
    }
}
