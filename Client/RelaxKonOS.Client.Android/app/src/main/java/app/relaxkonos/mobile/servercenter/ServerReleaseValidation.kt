package app.relaxkonos.mobile.servercenter

import java.security.KeyFactory
import java.security.MessageDigest
import java.security.PublicKey
import java.security.Signature
import java.security.spec.MGF1ParameterSpec
import java.security.spec.PSSParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.Locale

/** v1 发布签名算法。与 C# `ServerReleaseSignatureAlgorithms` 一致。 */
object ServerReleaseSignatureAlgorithms {
    const val RSA_PSS_SHA256 = "rsa-pss-sha256"
}

/**
 * 发布清单、描述符与包布局的校验规则。与 C# `ServerReleaseValidation` 对齐。
 *
 * 签名对象是制品的**原始文件字节**（`manifest.json` 或 `latest/{rid}.json`），签名单独放在同名 `*.sig` 文件中。
 * `signedSha256` 恒为这些原始字节的 SHA-256，因此 C#、Kotlin 与打包脚本不需要共享 JSON 规范化实现。
 */
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

    /** 校验清单结构并验证其伴随签名。 */
    fun verifyManifest(
        manifestBytes: ByteArray,
        manifest: ServerReleaseManifest?,
        expectedRuntime: ServerRuntimeIdentifier,
        signature: ServerReleaseSignature?,
        publicKeyPem: String,
        trustPolicy: ServerReleaseTrustPolicy,
    ): String? {
        val structural = validateManifest(manifest, expectedRuntime)
        if (structural != null) return structural
        return ServerReleaseSignatureVerifier.verify(manifestBytes, signature, publicKeyPem, trustPolicy)
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

    /** 校验下载描述符结构。签名是伴随文件，因此这里只做结构校验。 */
    fun validateDescriptor(descriptor: ServerReleaseDescriptor?): String? {
        if (descriptor == null) return ServerDeploymentProblemCodes.PACKAGE_UNAVAILABLE
        if (descriptor.schemaVersion != ServerDeploymentProtocol.VERSION) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (!ServerDeploymentInputRules.isVersion(descriptor.version)) return ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID
        if (!ServerDeploymentInputRules.isSha256(descriptor.sha256)) return ServerDeploymentProblemCodes.PACKAGE_DIGEST_MISMATCH
        return null
    }

    /** 校验描述符结构并验证其伴随签名。缺少签名直接拒绝。 */
    fun verifyDescriptor(
        descriptorBytes: ByteArray,
        descriptor: ServerReleaseDescriptor?,
        signature: ServerReleaseSignature?,
        publicKeyPem: String,
        trustPolicy: ServerReleaseTrustPolicy,
    ): String? {
        val structural = validateDescriptor(descriptor)
        if (structural != null) return structural
        return ServerReleaseSignatureVerifier.verify(descriptorBytes, signature, publicKeyPem, trustPolicy)
    }
}

/**
 * 发布签名校验。签名覆盖制品原始字节的 SHA-256，客户端用内置可信公钥验证，
 * 并确认 `keyId` 在信任策略内。
 */
object ServerReleaseSignatureVerifier {

    /**
     * 验证一段制品字节的签名。
     *
     * @param signedBytes 制品原始文件字节。
     * @param signature 签名记录，携带算法、keyId 与 Base64 签名。
     * @param publicKeyPem 该 keyId 对应的 SubjectPublicKeyInfo PEM。
     * @param trustPolicy 发布信任策略；未受信任的 keyId 直接拒绝。
     * @return null 表示通过，否则返回稳定的问题码。
     */
    fun verify(
        signedBytes: ByteArray,
        signature: ServerReleaseSignature?,
        publicKeyPem: String,
        trustPolicy: ServerReleaseTrustPolicy,
    ): String? {
        if (signature == null) return ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        if (signature.schemaVersion != ServerDeploymentProtocol.VERSION) return ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        if (signature.algorithm != ServerReleaseSignatureAlgorithms.RSA_PSS_SHA256) {
            return ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        }
        if (publicKeyPem.isBlank()) return ServerDeploymentProblemCodes.PACKAGE_TRUST_ROOT_MISSING
        if (!trustPolicy.isTrustedKey(signature.keyId) && !trustPolicy.acceptsUntrustedSource) {
            return ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        }

        val digest = sha256Hex(signedBytes)
        if (!digest.equals(signature.signedSha256, ignoreCase = true)) {
            return ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        }

        val signatureBytes = try {
            Base64Codec.decode(signature.signature)
        } catch (e: IllegalArgumentException) {
            return ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        }

        return try {
            val key = parsePublicKey(publicKeyPem)
            val verifier = Signature.getInstance("RSASSA-PSS")
            verifier.setParameter(PSSParameterSpec("SHA-256", "MGF1", MGF1ParameterSpec.SHA256, 32, 1))
            verifier.initVerify(key)
            verifier.update(signedBytes)
            if (verifier.verify(signatureBytes)) null else ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        } catch (e: Exception) {
            ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID
        }
    }

    private fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes)
            .joinToString("") { "%02x".format(it) }

    private fun parsePublicKey(pem: String): PublicKey {
        val base64 = pem
            .replace("-----BEGIN PUBLIC KEY-----", "")
            .replace("-----END PUBLIC KEY-----", "")
            .filterNot { it.isWhitespace() }
        val der = Base64Codec.decode(base64)
        return KeyFactory.getInstance("RSA").generatePublic(X509EncodedKeySpec(der))
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
