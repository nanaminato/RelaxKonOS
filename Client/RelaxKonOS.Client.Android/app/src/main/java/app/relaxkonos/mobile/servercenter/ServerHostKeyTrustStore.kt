package app.relaxkonos.mobile.servercenter

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File

/** 已确认主机密钥列表的字节存储。 */
interface HostKeyStorage {
    fun read(): ByteArray?
    fun write(payload: ByteArray)
}

/** `noBackupFilesDir` 下的单文件存储，原子替换写入。 */
class FileHostKeyStorage(private val directory: File) : HostKeyStorage {
    private val file get() = File(directory, "pinned-host-keys.bin")

    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()

    override fun write(payload: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, file.name + ".tmp")
        temporary.writeBytes(payload)
        if (!temporary.renameTo(file)) {
            file.delete()
            if (!temporary.renameTo(file)) {
                temporary.delete()
                throw IllegalStateException("Unable to commit the pinned host key file.")
            }
        }
    }
}

/** 单元测试用的内存存储。 */
class InMemoryHostKeyStorage : HostKeyStorage {
    private var payload: ByteArray? = null

    override fun read(): ByteArray? = payload?.copyOf()

    override fun write(payload: ByteArray) {
        this.payload = payload.copyOf()
    }
}

/**
 * 已确认的 SSH 主机密钥的本地固定仓库。主机密钥与指纹都不是秘密，因此可以明文保存；
 * 但必须抗意外覆盖，并在变化时阻断所有写操作。
 *
 * 判定逻辑全部来自 [ServerHostTrustRules]，与桌面共用同一口径；本仓库只负责读写，
 * 不提供任何「静默信任」或「跳过核对」的入口。
 */
class ServerHostKeyTrustStore(private val storage: HostKeyStorage) {

    fun all(): List<ServerHostKeyRecord> = read()

    /** 同端点同算法的既有固定记录。 */
    fun find(host: String, port: Int, algorithm: String): ServerHostKeyRecord? =
        ServerHostTrustRules.find(read(), host, port, algorithm)

    /** 判定本次观测到的宿主密钥。首次见到端点或该算法时返回 [ServerHostKeyTrust.Unknown]。 */
    fun evaluate(
        endpoint: ServerCenterSshEndpoint,
        observation: ServerCenterHostKeyObservation,
    ): ServerHostKeyTrust = ServerHostTrustRules.evaluate(
        read(),
        endpoint.host,
        endpoint.port,
        observation.algorithm,
        observation.publicKeyBlob,
    )

    /**
     * 在用户核对指纹后固定该密钥；同一端点同一算法只保留一条记录。
     * 返回写入的固定记录，供界面展示指纹与确认时间。
     */
    fun trust(
        endpoint: ServerCenterSshEndpoint,
        observation: ServerCenterHostKeyObservation,
        nowEpochMillis: Long,
    ): ServerHostKeyRecord {
        val confirmed = ServerHostKeyRecord(
            host = endpoint.host,
            port = endpoint.port,
            algorithm = observation.algorithm,
            publicKeyBase64 = Base64Codec.encode(observation.publicKeyBlob),
            fingerprint = observation.fingerprint,
            confirmedAtEpochMillis = nowEpochMillis,
        )
        write(ServerHostTrustRules.replace(read(), confirmed))
        return confirmed
    }

    /** 移除某端点某算法的固定记录（用户显式解除信任）。 */
    fun forget(host: String, port: Int, algorithm: String) {
        val key = ServerHostTrustRules.endpointKey(host, port)
        write(read().filterNot {
            ServerHostTrustRules.endpointKey(it.host, it.port) == key && it.algorithm == algorithm
        })
    }

    /** 移除某端点的全部固定记录。 */
    fun forgetEndpoint(host: String, port: Int) {
        val key = ServerHostTrustRules.endpointKey(host, port)
        write(read().filterNot { ServerHostTrustRules.endpointKey(it.host, it.port) == key })
    }

    private fun read(): List<ServerHostKeyRecord> {
        val payload = storage.read() ?: return emptyList()
        return try {
            DataInputStream(ByteArrayInputStream(payload)).use { input ->
                if (input.readInt() != MAGIC) {
                    emptyList()
                } else {
                    val count = input.readInt()
                    ArrayList<ServerHostKeyRecord>(count).apply {
                        repeat(count) {
                            add(
                                ServerHostKeyRecord(
                                    host = input.readUTF(),
                                    port = input.readInt(),
                                    algorithm = input.readUTF(),
                                    publicKeyBase64 = input.readUTF(),
                                    fingerprint = input.readUTF(),
                                    confirmedAtEpochMillis = input.readLong(),
                                ),
                            )
                        }
                    }
                }
            }
        } catch (_: Exception) {
            // 读不懂的旧文件不能静默变成信任来源；按「没有固定记录」处理，下次连接重新人工核对。
            emptyList()
        }
    }

    private fun write(records: List<ServerHostKeyRecord>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeInt(records.size)
            for (record in records) {
                output.writeUTF(record.host)
                output.writeInt(record.port)
                output.writeUTF(record.algorithm)
                output.writeUTF(record.publicKeyBase64)
                output.writeUTF(record.fingerprint)
                output.writeLong(record.confirmedAtEpochMillis)
            }
        }
        storage.write(buffer.toByteArray())
    }

    private companion object {
        /** `RKHK`：布局随首次正式发布前的接口直接演进，不做双解析。 */
        const val MAGIC = 0x524B484B
    }
}