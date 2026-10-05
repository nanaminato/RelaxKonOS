package app.relaxkonos.mobile.ui.nav

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.lifecycle.AndroidViewModel
/**
 * Owns shell navigation.
 *
 * Navigation lives in a ViewModel rather than in composition state so that a configuration change —
 * including the activity recreation a language switch causes — restores the user to the destination
 * they were on. The stacks are plain observable state, so the JVM tests can drive them without Compose.
 */
class ShellViewModel(application: Application) : AndroidViewModel(application) {
    val navigator = MobileNavigator(Routes.HOME)
}

