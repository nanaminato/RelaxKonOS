package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.*
import org.junit.Test

class UsageMemoryStoreTest {
    private fun owner(service: String = "server", user: String = "alice", workspace: String = "workspace") =
        SessionState.Active(service, "http://127.0.0.1:6000", user, "main", emptySet(), "linux", ExecutionEligibility.Available, workspaceId = workspace)

    @Test fun `defaults survive store recreation and remain partitioned`() {
        val storage = InMemoryUsageMemoryStorage()
        var current: SessionState.Active? = owner()
        var store = UsageMemoryStore(storage)
        val memory = store.capture(current) { current }
        memory.rememberAdministrator("  admin  ")
        memory.rememberDirectory("upload", false, "content://provider/document/opaque-id")
        memory.rememberDirectory("open", true, "/home/alice")
        store = UsageMemoryStore(storage)
        assertEquals("admin", store.capture(current) { current }.administrator)
        assertEquals("/home/alice", store.capture(current) { current }.directory("open", true))
        assertNull(store.capture(current) { current }.directory("open", false))
        assertNull(store.capture(current) { current }.directory("other", true))
        current = current!!.copy(effectiveBaseUrl = "http://127.0.0.1:6001")
        assertEquals("admin", store.capture(current) { current }.administrator)
        current = owner(workspace = "other")
        assertEquals("admin", store.capture(current) { current }.administrator)
        assertNull(store.capture(current) { current }.directory("open", true))
        current = owner(user = "bob")
        assertNull(store.capture(current) { current }.administrator)
        current = owner(service = "other")
        assertNull(store.capture(current) { current }.administrator)
    }

    @Test fun `clear affects only current identity and ignores delayed writes`() {
        val storage = InMemoryUsageMemoryStorage()
        val store = UsageMemoryStore(storage)
        var current: SessionState.Active? = owner()
        val original = current
        val old = store.capture(current) { current }
        old.rememberAdministrator("admin")
        current = owner(user = "bob")
        store.capture(current) { current }.rememberAdministrator("bob-admin")
        old.rememberAdministrator("wrong")
        assertEquals("bob-admin", store.capture(current) { current }.administrator)
        current = original
        assertEquals("admin", store.capture(current) { current }.administrator)
        store.clear(current)
        old.rememberAdministrator("late-answer")
        assertNull(UsageMemoryStore(storage).capture(current) { current }.administrator)
        current = owner(user = "bob")
        assertEquals("bob-admin", UsageMemoryStore(storage).capture(current) { current }.administrator)
    }

    @Test fun `cancel blank answer and storage failure preserve usable defaults`() {
        val store = UsageMemoryStore(object : UsageMemoryStorage {
            override fun read(): Map<String, String> = throw IllegalStateException("corrupt preferences")
            override fun write(values: Map<String, String>) = throw IllegalStateException("unavailable storage")
        })
        val current = owner()
        val memory = store.capture(current) { current }
        assertNull(memory.administrator)
        memory.rememberAdministrator("admin")
        memory.rememberAdministrator(" ")
        assertEquals("admin", memory.administrator)
        memory.rememberDirectory("upload", false, null)
        assertNull(memory.directory("upload", false))
        store.clear(current)
        assertNull(store.capture(current) { current }.administrator)
    }
}
