package app.relaxkonos.mobile.ui.manage.components

import app.relaxkonos.mobile.core.net.ComponentModeSupport
import app.relaxkonos.mobile.core.net.ComponentState
import app.relaxkonos.mobile.core.net.ComponentStateView
import app.relaxkonos.mobile.data.ComponentRead
import app.relaxkonos.mobile.ui.nav.Routes
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ComponentDependencyRulesTest {
    @Test
    fun `an absent component is the only missing dependency`() {
        assertEquals(ComponentDependencyVerdict.Missing, componentVerdict(read("nginx", state("nginx", "absent"))))
        assertEquals(ComponentDependencyVerdict.Ready, componentVerdict(read("nginx", state("nginx", "managed"))))
        assertEquals(ComponentDependencyVerdict.Ready, componentVerdict(read("nginx", state("nginx", "external"))))
        assertEquals(ComponentDependencyVerdict.Ready,
            componentVerdict(read("nginx", state("nginx", "unknown"))))
    }

    @Test
    fun `unknown and unsupported states never block entry`() {
        assertEquals(ComponentDependencyVerdict.Unknown, componentVerdict(ComponentRead("nginx", null, "component.unavailable")))
        assertEquals(ComponentDependencyVerdict.Ready, componentVerdict(read("samba",
            ComponentStateView("samba", null,
                listOf(ComponentModeSupport("systemPackage", "windows", false, "component.platform_unsupported")),
                listOf("component.platform_unsupported"), null))))
        assertEquals(ComponentDependencyVerdict.Unknown, componentVerdict(read("samba",
            ComponentStateView("samba", null, emptyList(), listOf("component.state_unavailable"), null))))
    }

    @Test
    fun `recovery and a running task outrank a sibling's missing state`() {
        val missing = read("frpc", state("frpc", "absent"))
        val recovery = read("frps", state("frps", "absent", recovery = true))
        assertEquals(ComponentDependencyVerdict.Recovery, dependencyVerdict(listOf(missing, recovery)))
        val busy = read("frps", state("frps", "absent", pending = "operation-1"))
        assertEquals(ComponentDependencyVerdict.Busy, dependencyVerdict(listOf(missing, busy)))
    }

    @Test
    fun `one installed alternative satisfies the dependency`() {
        val missing = read("frpc", state("frpc", "absent"))
        val installed = read("frps", state("frps", "managed"))
        assertEquals(ComponentDependencyVerdict.Ready, dependencyVerdict(listOf(missing, installed)))
        assertEquals(ComponentDependencyVerdict.Missing, dependencyVerdict(listOf(missing, missing)))
    }

    @Test
    fun `an empty or unreadable answer never blocks`() {
        assertEquals(ComponentDependencyVerdict.Ready, dependencyVerdict(emptyList()))
    }

    @Test
    fun `application routes declare their components and alternatives`() {
        assertEquals(listOf("nginx"), AppComponentDependencies.componentsFor(Routes.MANAGE_WEBSITES))
        assertEquals(listOf("docker"), AppComponentDependencies.componentsFor(Routes.MANAGE_DOCKER_RESOURCES))
        assertEquals(listOf("docker"), AppComponentDependencies.componentsFor(Routes.MANAGE_DOCKER_CONTROL))
        assertEquals(listOf("frpc", "frps"), AppComponentDependencies.componentsFor(Routes.MANAGE_TUNNELS))
        assertNull(AppComponentDependencies.componentsFor(Routes.MANAGE_FIREWALL))
        assertNull(AppComponentDependencies.componentsFor(Routes.MANAGE_COMPONENTS))
    }

    @Test
    fun `a file install requires an archive component a version and a live reference`() {
        assertTrue(validFileInstall("frpc", "v0.61.0", "reference-1"))
        assertTrue(validFileInstall("frps", "0.61.0", "reference-1"))
        assertTrue(validFileInstall("nginx", "1.26.0", "reference-1"))
        assertFalse(validFileInstall("mihomo", "1.0.0", "reference-1"))
        assertFalse(validFileInstall("frpc", "v0.61.0 ", " "))
        assertFalse(validFileInstall("frpc", "", "reference-1"))
        assertFalse(validFileInstall("frpc", "-leading", "reference-1"))
        assertFalse(validFileInstall("frpc", "v0.61.0", ""))
    }

    private fun read(componentId: String, view: ComponentStateView?, problem: String? = null) =
        ComponentRead(componentId, view, problem)

    private fun state(componentId: String, ownership: String, recovery: Boolean = false, pending: String? = null) =
        ComponentStateView(componentId,
            ComponentState(componentId, "host-1", 3, ownership, "systemPackage", "linux / x64 / systemd", "1.0", recovery),
            listOf(ComponentModeSupport("systemPackage", "linux", true, null)), emptyList(), pending)
}
