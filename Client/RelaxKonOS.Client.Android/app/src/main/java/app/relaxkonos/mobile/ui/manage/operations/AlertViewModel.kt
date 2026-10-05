package app.relaxkonos.mobile.ui.manage.operations

import android.app.Application
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.data.AlertNotificationCategory
import androidx.compose.runtime.*
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
internal class AlertViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    fun enabledNotifications(owner: SessionState.Active) = AlertNotificationCategory.entries.filter {
        container.alertNotificationStore.enabled(owner.serviceId, it)
    }.toSet()

    fun changeNotification(owner: SessionState.Active, category: AlertNotificationCategory, enabled: Boolean) {
        container.alertNotificationStore.setEnabled(owner.serviceId, category, enabled)
        if (!enabled) container.foregroundAlertNotifier.clearForCategory(owner, category)
        container.foregroundAlertNotifier.restart()
    }


    val browser = EventAlertBrowser(getApplication<RelaxKonApplication>().container.eventAlerts, viewModelScope)
    override fun onCleared() { browser.stop(); super.onCleared() }
}
