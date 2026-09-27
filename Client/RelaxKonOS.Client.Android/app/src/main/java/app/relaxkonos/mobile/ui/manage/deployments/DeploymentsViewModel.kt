package app.relaxkonos.mobile.ui.manage.deployments

import android.app.Application
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ArchiveDeploymentDefinition
import app.relaxkonos.mobile.data.DeploymentBrowser
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Coordinates file access and deployment submission outside the Compose deployment screens. */
class DeploymentsViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    val browser = DeploymentBrowser(container.deployments, container.session, viewModelScope)

    fun createArchive(uri: Uri, definition: ArchiveDeploymentDefinition) {
        viewModelScope.launch(Dispatchers.IO) {
            val document = container.uploadDocuments.open(uri.toString())
            withContext(Dispatchers.Main.immediate) {
                if (document == null) browser.archiveUnavailable() else browser.createArchive(definition, document)
            }
        }
    }

    fun createServerArchive(path: String, definition: ArchiveDeploymentDefinition) {
        browser.createServerArchive(definition, path)
    }
}
