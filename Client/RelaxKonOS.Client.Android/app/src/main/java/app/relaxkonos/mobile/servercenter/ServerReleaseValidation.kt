package app.relaxkonos.mobile.servercenter

/** 发布清单、描述符与包布局的校验规则。与 C# `ServerReleaseValidation` 对齐。 */
object ServerReleaseValidation {

    /** 校验包内清单结构。返回 null 表示通过，否则返回稳定的问题码。 */
    fun validateManifest(manifest: ServerReleaseManifest?, expectedRuntime: ServerRuntimeIdentifier): String? {
        if (manifest == null) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (manifest.schemaVersion != ServerDeploymentProtocol.VERSION) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (!ServerDeploymentInputRules.isVersion(manifest.version)) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (manifest.runtime != expectedRuntime) return ServerDeploymentProblemCodes.PACKAGE_RUNTIME_MISMATCH
        if (manifest.supportedSystems.isEmpty()) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (manifest.files.isEmpty()) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        val platform = when (expectedRuntime) {
            ServerRuntimeIdentifier.WinX64, ServerRuntimeIdentifier.WinArm64 -> "windows"
            ServerRuntimeIdentifier.LinuxX64, ServerRuntimeIdentifier.LinuxArm64 -> "linux"
        }
        val payload = manifest.payload[platform]
            ?: return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (payload.isEmpty()) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        val paths = mutableSetOf<String>()
        for (file in manifest.files) {
            val problem = validateFile(file)
            if (problem != null) return problem
            if (!paths.add(file.path)) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        }
        if (payload.values.any { !isSafeManifestPath(it) || it !in paths }) {
            return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        }
        return null
    }

    fun validateFile(file: ServerReleaseFile?): String? {
        if (file == null) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (!isSafeManifestPath(file.path)) return ServerDeploymentProblemCodes.PACKAGE_LAYOUT_UNSAFE
        if (!ServerDeploymentInputRules.isSha256(file.sha256)) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (file.length < 0) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        return null
    }

    /**
     * 清单内路径必须是相对 POSIX 路径：无前导分隔符、无反斜杠、无 `..`、无空段。
     * 解包时任何一条不满足都必须拒绝整个包。
     */
    fun isSafeManifestPath(path: String?): Boolean {
        if (path.isNullOrBlank()) return false
        if (path.startsWith('/') || path.startsWith('\\')) return false
        if (path.contains('\\')) return false
        if (path.contains("..")) return false
        if (path.contains('\u0000')) return false
        return path.split('/').none { it.isEmpty() || it == "." }
    }

    /** 校验下载描述符结构。 */
    fun validateDescriptor(descriptor: ServerReleaseDescriptor?): String? {
        if (descriptor == null) return ServerDeploymentProblemCodes.PACKAGE_UNAVAILABLE
        if (descriptor.schemaVersion != ServerDeploymentProtocol.VERSION) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (!ServerDeploymentInputRules.isVersion(descriptor.version)) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (!ServerDeploymentInputRules.isSha256(descriptor.sha256)) return ServerDeploymentProblemCodes.PACKAGE_DIGEST_MISMATCH
        return null
    }
}

/**
 * 最小 Base64 编解码器（标准字母表，容忍缺失填充）。使用自有实现是为了同时满足：
 * `java.util.Base64` 需要 API 26，而 `android.util.Base64` 在 JVM 单元测试中不可用。
 */
internal object Base64Codec {
    private const val ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"

    fun decode(value: String): ByteArray {
        val filtered = value.filterNot { it.isWhitespace() }.trimEnd('=')
        if (filtered.isEmpty()) return ByteArray(0)
        val output = java.io.ByteArrayOutputStream()
        var buffer = 0
        var bits = 0
        for (c in filtered) {
            val index = ALPHABET.indexOf(c)
            require(index >= 0) { "Invalid base64 character: $c" }
            buffer = (buffer shl 6) or index
            bits += 6
            if (bits >= 8) {
                bits -= 8
                output.write((buffer shr bits) and 0xFF)
            }
        }
        return output.toByteArray()
    }

    /** 标准字母表编码，带 `=` 填充，与 `java.util.Base64` 的标准编码器一致。 */
    fun encode(value: ByteArray): String {
        if (value.isEmpty()) return ""
        val builder = StringBuilder((value.size + 2) / 3 * 4)
        var index = 0
        while (index + 2 < value.size) {
            val chunk = (value[index].toInt() and 0xFF shl 16) or
                (value[index + 1].toInt() and 0xFF shl 8) or
                (value[index + 2].toInt() and 0xFF)
            builder.append(ALPHABET[chunk shr 18 and 0x3F])
            builder.append(ALPHABET[chunk shr 12 and 0x3F])
            builder.append(ALPHABET[chunk shr 6 and 0x3F])
            builder.append(ALPHABET[chunk and 0x3F])
            index += 3
        }
        when (value.size - index) {
            1 -> {
                val chunk = value[index].toInt() and 0xFF shl 16
                builder.append(ALPHABET[chunk shr 18 and 0x3F])
                builder.append(ALPHABET[chunk shr 12 and 0x3F])
                builder.append("==")
            }

            2 -> {
                val chunk = (value[index].toInt() and 0xFF shl 16) or (value[index + 1].toInt() and 0xFF shl 8)
                builder.append(ALPHABET[chunk shr 18 and 0x3F])
                builder.append(ALPHABET[chunk shr 12 and 0x3F])
                builder.append(ALPHABET[chunk shr 6 and 0x3F])
                builder.append('=')
            }
        }
        return builder.toString()
    }
}
