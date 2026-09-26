package app.relaxkonos.mobile.servercenter

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File

/** 宿主目标列表的字节存储。 */
interface HostTargetStorage {
    fun read(): ByteArray?
    fun write(payload: ByteArray)
}

/**
 * `noBackupFilesDir` 下的单文件存储，原子替换写入。
 *
 * 宿主目标只有管理资料（SSH 端点、受管安装标识、最近核验状态），没有任何凭据，
 * 因此可以明文保存；它仍然不进 Android 备份与设备迁移，因为它描述的是本机对某台宿主的管辖关系。
 */
class FileHostTargetStorage(private val directory: File) : HostTargetStorage {
    private val file get() = File(directory, "host-targets.bin")

    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()

    override fun write(payload: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, file.name + ".tmp")
        temporary.writeBytes(payload)
        if (!temporary.renameTo(file)) {
            file.delete()
            if (!temporary.renameTo(file)) {
                temporary.delete()
                throw IllegalStateException("Unable to commit the host target file.")
            }
        }
    }
}

/** 单元测试用的内存存储。 */
class InMemoryHostTargetStorage : HostTargetStorage {
    private var payload: ByteArray? = null

    override fun read(): ByteArray? = payload?.copyOf()

    override fun write(payload: ByteArray) {
        this.payload = payload.copyOf()
    }
}

/**
 * 本机宿主目标的仓库。宿主目标只包含管理资料，因此与 SSH 凭据、主机指纹、登录记录分别保存：
 * 删除一个宿主目标只影响本机管理资料，不会顺带删掉登录或凭据。
 *
 * 与桌面 `HostTargetStore` 采用同一去重口径：同一 `(host, port)` 只保留一条记录，与 SSH 用户无关。
 */
class ServerHostTargetStore(private val storage: HostTargetStorage) {

    fun all(): List<ServerHostTarget> = read().sortedByDescending { it.lastUsedAtEpochMillis }

    fun find(hostId: String): ServerHostTarget? = read().firstOrNull { it.hostId == hostId }

    /** 按 SSH 端点查找；端点相同即同一目标，与 SSH 用户无关。 */
    fun findByEndpoint(host: String, port: Int): ServerHostTarget? {
        require(ServerHostTargetRules.isValidEndpoint(host, port, "probe")) {
            "An SSH host and port are required."
        }
        val identity = ServerHostTargetRules.endpointIdentity(host, port)
        return read().firstOrNull {
            ServerHostTargetRules.endpointIdentity(it.sshHost, it.sshPort) == identity
        }
    }

    /** 写入或更新一个宿主目标。同一端点只会保留一条记录。 */
    fun upsert(target: ServerHostTarget): ServerHostTarget {
        require(ServerHostTargetRules.isValidEndpoint(target.sshHost, target.sshPort, target.sshUserName)) {
            "A host target needs a valid SSH endpoint and user."
        }
        val identity = ServerHostTargetRules.endpointIdentity(target.sshHost, target.sshPort)
        val remaining = read().filterNot {
            ServerHostTargetRules.endpointIdentity(it.sshHost, it.sshPort) == identity
        }
        write(remaining + target)
        return target
    }

    /** 删除一个宿主目标。只影响本机管理资料，返回是否确有记录被删除。 */
    fun remove(hostId: String): Boolean {
        require(hostId.isNotBlank()) { "A host id is required." }
        val current = read()
        val remaining = current.filterNot { it.hostId == hostId }
        if (remaining.size == current.size) return false
        write(remaining)
        return true
    }

    /** 记录一次成功使用，用于列表按最近使用排序。找不到目标时不做任何事。 */
    fun markUsed(hostId: String, nowEpochMillis: Long) {
        val current = read()
        if (current.none { it.hostId == hostId }) return
        write(current.map { if (it.hostId == hostId) it.copy(lastUsedAtEpochMillis = nowEpochMillis) else it })
    }

    private fun read(): List<ServerHostTarget> {
        val payload = storage.read() ?: return emptyList()
        return try {
            DataInputStream(ByteArrayInputStream(payload)).use { input ->
                if (input.readInt() != MAGIC) {
                    emptyList()
                } else {
                    val count = input.readInt()
                    ArrayList<ServerHostTarget>(count).apply {
                        repeat(count) { add(readTarget(input)) }
                    }
                }
            }
        } catch (_: Exception) {
            // 读不懂的旧文件不能冒充用户创建过的宿主列表；按「没有宿主目标」处理。
            emptyList()
        }
    }

    private fun write(targets: List<ServerHostTarget>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeInt(targets.size)
            for (target in targets) writeTarget(output, target)
        }
        storage.write(buffer.toByteArray())
    }

    private fun writeTarget(output: DataOutputStream, target: ServerHostTarget) {
        output.writeUTF(target.hostId)
        output.writeUTF(target.displayName)
        output.writeUTF(target.sshHost)
        output.writeInt(target.sshPort)
        output.writeUTF(target.sshUserName)
        output.writeNullableUtf(target.installationId)
        output.writeVerifiedState(target.lastVerified)
        output.writeLong(target.createdAtEpochMillis)
        output.writeLong(target.lastUsedAtEpochMillis)
    }

    private fun readTarget(input: DataInputStream): ServerHostTarget = ServerHostTarget(
        hostId = input.readUTF(),
        displayName = input.readUTF(),
        sshHost = input.readUTF(),
        sshPort = input.readInt(),
        sshUserName = input.readUTF(),
        installationId = input.readNullableUtf(),
        lastVerified = input.readVerifiedState(),
        createdAtEpochMillis = input.readLong(),
        lastUsedAtEpochMillis = input.readLong(),
    )

    private fun DataOutputStream.writeVerifiedState(state: ServerHostVerifiedState?) {
        if (state == null) {
            writeByte(0)
            return
        }
        writeByte(1)
        writeByte(if (state.installed) 1 else 0)
        // 0 表示「没有模式」，其余为 ordinal+1，因此枚举新增值不会与空值混淆。
        writeByte(state.mode?.ordinal?.plus(1) ?: 0)
        writeNullableUtf(state.installationId)
        writeNullableUtf(state.version)
        writeNullableUtf(state.listenUrl)
        writeByte(if (state.healthy) 1 else 0)
        writeLong(state.verifiedAtEpochMillis)
    }

    private fun DataInputStream.readVerifiedState(): ServerHostVerifiedState? {
        if (readByte().toInt() == 0) return null
        val installed = readByte().toInt() == 1
        val modeOrdinal = readByte().toInt()
        val mode = if (modeOrdinal == 0) null else ServerInstallMode.entries.getOrNull(modeOrdinal - 1)
        val installationId = readNullableUtf()
        val version = readNullableUtf()
        val listenUrl = readNullableUtf()
        val healthy = readByte().toInt() == 1
        val verifiedAt = readLong()
        return ServerHostVerifiedState(installed, mode, installationId, version, listenUrl, healthy, verifiedAt)
    }

    private fun DataOutputStream.writeNullableUtf(value: String?) {
        if (value == null) {
            writeByte(0)
        } else {
            writeByte(1)
            writeUTF(value)
        }
    }

    private fun DataInputStream.readNullableUtf(): String? = if (readByte().toInt() == 1) readUTF() else null

    private companion object {
        /** `RKHT`：布局随首次正式发布前的接口直接演进，不做双解析。 */
        const val MAGIC = 0x524B4854
    }
}