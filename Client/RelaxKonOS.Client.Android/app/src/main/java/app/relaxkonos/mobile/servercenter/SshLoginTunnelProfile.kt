package app.relaxkonos.mobile.servercenter

import java.net.URI
import java.security.MessageDigest
import java.io.*

/** Device-local settings only; passwords and keys live in the SSH vault. */
data class SshLoginTunnelProfile(val host: String, val port: Int, val userName: String, val remoteUrl: String, val useServerCredentials: Boolean = false) {
    val serviceId: String get() = IDENTITY_PREFIX + MessageDigest.getInstance("SHA-256")
        .digest("$host\n$port\n$userName\n$remoteUrl".toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02x".format(it.toInt() and 255) }
    val displayText: String get() = "$userName@$host:$port → $remoteUrl"
    fun resolve(localPort: Int): ServerConnectionIdentity {
        require(localPort in 1..65535)
        val remote = URI(remoteUrl)
        return ServerConnectionIdentity(ServerServiceIdKind.SshTunnelProfile, serviceId,
            "${remote.scheme}://127.0.0.1:$localPort${remote.path.orEmpty().trimEnd('/')}")
    }
    companion object {
        const val IDENTITY_PREFIX = "ssh-tunnel:"
        fun create(host: String, port: Int, userName: String, remoteUrl: String): SshLoginTunnelProfile {
            require(ServerHostTargetRules.isValidEndpoint(host, port, userName))
            require(host.none(Char::isISOControl) && userName.none(Char::isISOControl) && URI("ssh://${host.trim()}").host != null)
            val uri = URI(remoteUrl.trim())
            require(uri.scheme?.lowercase(java.util.Locale.ROOT) in listOf("http", "https") &&
                uri.host?.lowercase(java.util.Locale.ROOT) in listOf("localhost", "127.0.0.1") &&
                (uri.port == -1 || uri.port in 1..65535) && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null && uri.path in listOf("", "/"))
            return SshLoginTunnelProfile(ServerHostTrustRules.normalizeHost(host), port, userName.trim(),
                ServerConnectionIdentityRules.normalizeServerUrl(remoteUrl))
        }
    }
}

class LoginTunnelStore(private val directory: File) {
    private val file get() = File(directory, "login-tunnels.bin")
    fun all(): List<SshLoginTunnelProfile> {
        if (!file.isFile) return emptyList()
        return try {
            DataInputStream(file.inputStream()).use { input ->
                require(input.readInt() == 2)
                val count = input.readInt()
                require(count in 0..1000)
                List(count) { SshLoginTunnelProfile.create(input.readUTF(), input.readInt(), input.readUTF(), input.readUTF()).copy(useServerCredentials = input.readBoolean()) }
            }
        } catch (_: Exception) { emptyList() }
    }
    fun save(profile: SshLoginTunnelProfile) {
        val profiles = listOf(profile) + all().filter { it.serviceId != profile.serviceId }
        directory.mkdirs()
        val temporary = File(directory, "login-tunnels.bin.tmp")
        DataOutputStream(temporary.outputStream()).use { output ->
            output.writeInt(2); output.writeInt(profiles.size)
            profiles.forEach { output.writeUTF(it.host); output.writeInt(it.port); output.writeUTF(it.userName); output.writeUTF(it.remoteUrl); output.writeBoolean(it.useServerCredentials) }
        }
        java.nio.file.Files.move(temporary.toPath(), file.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING)
    }
}
