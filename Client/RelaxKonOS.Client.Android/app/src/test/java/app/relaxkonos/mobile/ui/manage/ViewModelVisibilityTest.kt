package app.relaxkonos.mobile.ui.manage

import android.app.Application
import app.relaxkonos.mobile.ui.manage.operations.AlertViewModel
import app.relaxkonos.mobile.ui.manage.operations.OperationsViewModel
import app.relaxkonos.mobile.ui.manage.websites.WebsitesViewModel
import java.lang.reflect.Modifier
import org.junit.Assert.assertTrue
import org.junit.Test

class ViewModelVisibilityTest {
    @Test
    fun defaultFactoryCanAccessManageViewModels() {
        listOf(
            OperationsViewModel::class.java,
            AlertViewModel::class.java,
            WebsitesViewModel::class.java,
        ).forEach { type ->
            assertTrue("${type.name} must be public for AndroidViewModelFactory", Modifier.isPublic(type.modifiers))
            assertTrue(
                "${type.name}(Application) must be public for AndroidViewModelFactory",
                Modifier.isPublic(type.getConstructor(Application::class.java).modifiers),
            )
        }
    }
}
