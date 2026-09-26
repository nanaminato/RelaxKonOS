package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 受管登录与服务器中心隧道的绑定契约测试。
 *
 * 被测对象是两条纯函数判断：按安装标识找宿主、把隧道解析结果绑到登录身份上。
 * 网络握手、保险箱解封与界面都不在其中——它们分别由连接解析器、宿主详情页和 Compose 层负责。
 */
class ManagedLoginTunnelResolutionTest {

    private val now = 1_790_000_000_000L
    private val installationA = "rki-" + "a".repeat(32)
    private val installationB = "rki-" + "b".repeat(32)

    private fun host(
        installationId: String? = installationA,
        host: String = "host.example",
        port: Int = 22,
    ): ServerHostTarget =
        ServerHostTargetRules.create(host, port, "deploy", null, now).copy(installationId = installationId)

    private fun tunnel(installationId: String = installationA, port: Int = 51000): ServerConnectionResolution =
        ServerTunnelRules.managedTunnel(installationId, port, null, now)

    @Test
    fun `a managed login is matched to its host by installation id`() {
        val targets = listOf(host(installationB, host = "other.example", port = 2222), host(installationA))

        assertEquals(targets[1], ManagedLoginTunnelRules.hostFor(targets, installationA))
    }

    @Test
    fun `an installation without a host record resolves to nothing`() {
        assertNull(ManagedLoginTunnelRules.hostFor(listOf(host(installationB)), installationA))
    }

    @Test
    fun `a host that has no verified installation is never claimed`() {
        // 首次安装前的宿主目标没有安装标识，因此不能被任何受管登录认领。
        assertNull(ManagedLoginTunnelRules.hostFor(listOf(host(installationId = null)), installationA))
    }

    @Test
    fun `a blank identity resolves to nothing`() {
        assertNull(ManagedLoginTunnelRules.hostFor(listOf(host()), ""))
        assertNull(ManagedLoginTunnelRules.hostFor(listOf(host()), null))
    }

    @Test
    fun `binding keeps the login identity and takes the tunnel address`() {
        val identity = ManagedLoginTunnelRules.bind(installationA, host(), tunnel(port = 52345))

        assertEquals(ServerServiceIdKind.ManagedInstallation, identity.kind)
        assertEquals(installationA, identity.serviceId)
        assertEquals("http://127.0.0.1:52345", identity.effectiveBaseUrl)
    }

    @Test
    fun `a tunnel from another installation is refused`() {
        // 宿主被重装后安装标识会变，旧登录绝不能被静默接到新安装上。
        val error = runCatching { ManagedLoginTunnelRules.bind(installationA, host(), tunnel(installationB)) }

        assertTrue(error.exceptionOrNull() is IllegalArgumentException)
    }

    @Test
    fun `a host that is not associated with the login is refused`() {
        val error = runCatching {
            ManagedLoginTunnelRules.bind(installationA, host(installationB), tunnel(installationA))
        }

        assertTrue(error.exceptionOrNull() is IllegalArgumentException)
    }

    @Test
    fun `a direct resolution cannot stand in for a tunnel`() {
        val error = runCatching {
            ManagedLoginTunnelRules.bind(installationA, host(), ServerTunnelRules.direct("http://127.0.0.1:5090", now))
        }

        assertTrue(error.exceptionOrNull() is IllegalArgumentException)
    }

    @Test
    fun `an invalid installation id is refused`() {
        val error = runCatching { ManagedLoginTunnelRules.bind("not-an-installation", host(), tunnel()) }

        assertTrue(error.exceptionOrNull() is IllegalArgumentException)
    }

    @Test
    fun `a rebound tunnel keeps the login identity and only changes the address`() {
        val first = tunnel(port = 51000)
        val rebound = ServerTunnelRules.rebindTunnel(first, 52345, now + 1)

        val bound = ManagedLoginTunnelRules.bind(installationA, host(), rebound)

        assertEquals(installationA, bound.serviceId)
        assertEquals("http://127.0.0.1:52345", bound.effectiveBaseUrl)
        assertTrue(ServerConnectionIdentityRules.preservesIdentity(first.identity, bound))
    }

    @Test
    fun `the store resolver answers from the host repository`() {
        val store = ServerHostTargetStore(InMemoryHostTargetStorage())
        val stored = store.upsert(host())

        val resolver = StoreManagedLoginResolver(store)

        assertEquals(stored.hostId, resolver.hostFor(installationA)?.hostId)
        assertNull(resolver.hostFor(installationB))
    }
}