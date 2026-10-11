package app.relaxkonos.mobile.ui.manage

import android.app.Application
import app.relaxkonos.mobile.ui.manage.operations.AlertViewModel
import app.relaxkonos.mobile.ui.manage.operations.OperationsViewModel
import app.relaxkonos.mobile.ui.manage.websites.NginxViewModel
import app.relaxkonos.mobile.ui.manage.docker.DockerViewModel
import app.relaxkonos.mobile.ui.manage.deployments.DeploymentsViewModel
import app.relaxkonos.mobile.ui.manage.proxy.ProxyViewModel
import app.relaxkonos.mobile.ui.manage.tunnels.TunnelsViewModel
import java.lang.reflect.Modifier
import org.junit.Assert.assertTrue
import org.junit.Test

class ViewModelVisibilityTest {
    @Test
    fun defaultFactoryCanAccessManageViewModels() {
        listOf(
            OperationsViewModel::class.java,
            AlertViewModel::class.java,
            NginxViewModel::class.java,
            DockerViewModel::class.java,
            DeploymentsViewModel::class.java,
            ProxyViewModel::class.java,
            TunnelsViewModel::class.java,
        ).forEach { type ->
            assertTrue("${type.name} must be public for AndroidViewModelFactory", Modifier.isPublic(type.modifiers))
            assertTrue(
                "${type.name}(Application) must be public for AndroidViewModelFactory",
                Modifier.isPublic(type.getConstructor(Application::class.java).modifiers),
            )
        }
    }
}
