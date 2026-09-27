package app.relaxkonos.mobile.ui.manage.deployments

import android.app.Application
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.data.DeploymentBrowser
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Coordinates file access and deployment submission outside the Compose deployment screens. */
class DeploymentsViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    val browser = DeploymentBrowser(container.deployments, container.session, viewModelScope)
    private var archiveSelectionGeneration = 0

    fun clearStagedArchive() {
        archiveSelectionGeneration++
        browser.clearStagedArchive()
    }

    fun stageArchive(uri: Uri) {
        val generation = ++archiveSelectionGeneration
        viewModelScope.launch(Dispatchers.IO) {
            val document = container.uploadDocuments.open(uri.toString())
            withContext(Dispatchers.Main.immediate) {
                if (generation == archiveSelectionGeneration) {
                    if (document == null) browser.archiveUnavailable() else browser.stageArchive(document)
                }
            }
        }
    }

    fun stageServerArchive(path: String) {
        archiveSelectionGeneration++
        browser.stageServerArchive(path)
    }
}
