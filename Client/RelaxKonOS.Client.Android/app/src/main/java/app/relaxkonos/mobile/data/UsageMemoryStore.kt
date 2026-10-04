package app.relaxkonos.mobile.data

import android.content.Context
import app.relaxkonos.mobile.core.auth.SessionState
import java.security.MessageDigest

/** Device-local, non-secret interaction defaults. Android backup is disabled for this application. */
interface UsageMemoryStorage {
    fun read(): Map<String, String>
    fun write(values: Map<String, String>)
}

class SharedPreferencesUsageMemoryStorage(context: Context) : UsageMemoryStorage {
    private val preferences = context.applicationContext.getSharedPreferences("relaxkonos.usage-memory", Context.MODE_PRIVATE)
    override fun read(): Map<String, String> = preferences.all.mapNotNull { (key, value) -> (value as? String)?.let { key to it } }.toMap()
    override fun write(values: Map<String, String>) {
        val editor = preferences.edit().clear()
        values.forEach { (key, value) -> editor.putString(key, value) }
        editor.commit()
    }
}

class InMemoryUsageMemoryStorage : UsageMemoryStorage {
    private var values = emptyMap<String, String>()
    override fun read() = values.toMap()
    override fun write(values: Map<String, String>) { this.values = values.toMap() }
}

class UsageMemoryStore(private val storage: UsageMemoryStorage) {
    private val values = try { storage.read().toMutableMap() } catch (_: RuntimeException) { mutableMapOf() }
    private val generations = mutableMapOf<String, Long>()

    private fun account(owner: SessionState.Active?): String {
        val identity = owner?.let { "${it.serviceId.length}:${it.serviceId}${it.userName.length}:${it.userName}" } ?: "local-device"
        return MessageDigest.getInstance("SHA-256").digest(identity.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
    }

    @Synchronized
    fun capture(owner: SessionState.Active?, currentOwner: () -> SessionState.Active?): UsageMemoryScope {
        val account = account(owner)
        return UsageMemoryScope(this, account, owner?.workspaceId, generations[account] ?: 0L) { currentOwner() === owner }
    }

    @Synchronized
    internal fun read(scope: UsageMemoryScope, key: String): String? =
        if (scope.isCurrent && (generations[scope.account] ?: 0L) == scope.generation) values[scope.account + ":" + key] else null

    @Synchronized
    internal fun write(scope: UsageMemoryScope, key: String, value: String?) {
        if (!scope.isCurrent || value.isNullOrBlank() || (generations[scope.account] ?: 0L) != scope.generation) return
        values[scope.account + ":" + key] = value
        persist()
    }

    @Synchronized
    fun clear(owner: SessionState.Active?) {
        val account = account(owner)
        values.keys.removeAll { it.startsWith("$account:") }
        generations[account] = (generations[account] ?: 0L) + 1
        persist()
    }

    private fun persist() {
        // Remembering a default must never fail an authorized operation.
        try { storage.write(values.toMap()) } catch (_: RuntimeException) { }
    }
}

class UsageMemoryScope internal constructor(
    private val store: UsageMemoryStore,
    internal val account: String,
    private val workspace: String?,
    internal val generation: Long,
    private val current: () -> Boolean,
) {
    val isCurrent: Boolean get() = current()
    val administrator: String? get() = store.read(this, "administrator")
    fun rememberAdministrator(username: String) = store.write(this, "administrator", username.trim())
    private fun directoryKey(purpose: String, remote: Boolean) = if (remote) "remote:$workspace:$purpose" else "local:$purpose"
    fun directory(purpose: String, remote: Boolean): String? = store.read(this, directoryKey(purpose, remote))
    fun rememberDirectory(purpose: String, remote: Boolean, directory: String?) = store.write(this, directoryKey(purpose, remote), directory)
}
