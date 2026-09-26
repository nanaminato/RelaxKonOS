package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** 宿主目标仓库的持久化测试：同一端点只留一条记录，字段逐项往返，损坏文件不冒充资料。 */
class ServerHostTargetStoreTest {

    private val now = 1_790_000_000_000L
    private val installId = "rki-" + "a".repeat(32)

    private fun target(host: String = "host.example", port: Int = 22, user: String = "deploy", display: String? = null) =
        ServerHostTargetRules.create(host, port, user, display, now)

    @Test
    fun `every field survives a round trip`() {
        val store = ServerHostTargetStore(InMemoryHostTargetStorage())
        val installed = ServerHostTargetRules.applyVerifiedState(
            target(display = "机房 A"),
            ServerHostVerifiedState(
                installed = true,
                mode = ServerInstallMode.LinuxSystem,
                installationId = installId,
                version = "0.1.0",
                listenUrl = "http://127.0.0.1:5090",
                healthy = true,
                verifiedAtEpochMillis = now + 5,
            ),
            now + 5,
        )
        store.upsert(installed)

        val reloaded = store.findByEndpoint("HOST.EXAMPLE", 22)
        assertEquals(installed.hostId, reloaded?.hostId)
        assertEquals("机房 A", reloaded?.displayName)
        assertEquals("host.example", reloaded?.sshHost)
        assertEquals(22, reloaded?.sshPort)
        assertEquals("deploy", reloaded?.sshUserName)
        assertEquals(installId, reloaded?.installationId)
        assertEquals(ServerInstallMode.LinuxSystem, reloaded?.lastVerified?.mode)
        assertEquals("http://127.0.0.1:5090", reloaded?.lastVerified?.listenUrl)
        assertEquals(true, reloaded?.lastVerified?.healthy)
        assertEquals(now + 5, reloaded?.lastVerified?.verifiedAtEpochMillis)
        assertEquals(now, reloaded?.createdAtEpochMillis)
        assertEquals(now + 5, reloaded?.lastUsedAtEpochMillis)
    }

    @Test
    fun `a target that was never verified reloads without inventing state`() {
        val store = ServerHostTargetStore(InMemoryHostTargetStorage())
        store.upsert(target())

        val reloaded = store.findByEndpoint("host.example", 22)
        assertNull(reloaded?.installationId)
        assertNull(reloaded?.lastVerified)
    }

    @Test
    fun `the same endpoint only ever holds one record`() {
        val store = ServerHostTargetStore(InMemoryHostTargetStorage())
        val first = target(display = "first")
        store.upsert(first)
        // 同一端点、不同 SSH 用户仍是同一宿主目标。
        store.upsert(first.copy(sshUserName = "other", displayName = "second"))

        assertEquals(1, store.all().size)
        assertEquals("second", store.find(first.hostId)?.displayName)
    }

    @Test
    fun `a different port is a different target`() {
        val store = ServerHostTargetStore(InMemoryHostTargetStorage())
        store.upsert(target(port = 22))
        store.upsert(target(port = 2222))
        assertEquals(2, store.all().size)
    }

    @Test
    fun `removing a target reports whether anything was removed`() {
        val store = ServerHostTargetStore(InMemoryHostTargetStorage())
        val created = store.upsert(target())

        assertTrue(store.remove(created.hostId))
        assertNull(store.find(created.hostId))
        assertFalse(store.remove(created.hostId))
    }

    @Test
    fun `recency ordering follows the last used stamp`() {
        val store = ServerHostTargetStore(InMemoryHostTargetStorage())
        val older = store.upsert(target(host = "a.example"))
        val newer = store.upsert(target(host = "b.example"))
        store.markUsed(older.hostId, now + 100)

        assertEquals(listOf(older.hostId, newer.hostId), store.all().map { it.hostId })
    }

    @Test
    fun `an unreadable file degrades to no host targets`() {
        val storage = InMemoryHostTargetStorage()
        storage.write(byteArrayOf(1, 2, 3, 4, 5))
        val store = ServerHostTargetStore(storage)

        assertTrue(store.all().isEmpty())
        assertNull(store.find("rkhost-0000000000000000"))
    }

    @Test
    fun `a foreign magic value is rejected instead of parsed`() {
        val storage = InMemoryHostTargetStorage()
        storage.write(byteArrayOf(0x52, 0x4B, 0x48, 0x55)) // "RKHU"
        assertTrue(ServerHostTargetStore(storage).all().isEmpty())
    }
}