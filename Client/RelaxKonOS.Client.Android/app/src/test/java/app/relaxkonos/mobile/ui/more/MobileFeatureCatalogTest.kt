package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.nav.Routes
import org.junit.Assert.*
import org.junit.Test

class MobileFeatureCatalogTest {
    @Test fun `without host capability no management link is invented`() {
        val visible=MobileFeatureCatalog.entries.filter{it.available(emptySet())}.map{it.route}
        assertTrue(Routes.MORE_HOST_SETTINGS in visible)
        assertFalse(visible.any{it.startsWith("manage/")});assertFalse(Routes.FILES in visible)
    }
    @Test fun `capability removal hides task and domains match the native manager boundaries`() {
        val caps=setOf(ServerCapabilities.WEB_SERVER,ServerCapabilities.GUARDIAN,ServerCapabilities.FILES)
        val visible=MobileFeatureCatalog.entries.filter{it.available(caps)}.map{it.route}
        assertTrue(Routes.MANAGE_WEBSITES in visible);assertTrue(Routes.MANAGE_OPERATIONS in visible)
        assertTrue(Routes.MANAGE_GUARDIAN in visible);assertTrue(Routes.MANAGE_SCRIPTS in visible)
        assertTrue(Routes.FILES in visible);assertFalse(Routes.MANAGE_DOCKER in visible)
    }
    @Test fun `catalog only links concrete native routes and excludes desktop extension runtime`() {
        val routes=MobileFeatureCatalog.entries.map{it.route}
        assertEquals(routes.size,routes.toSet().size)
        assertFalse(routes.any{it.contains("registry")||it.contains("browser")||it.contains("appinstaller")||it.contains("welcome")})
        assertTrue(routes.all{it==Routes.FILES||it==Routes.TERMINAL||it.startsWith("manage/")||it.startsWith("more/")})
    }
}
