package app.relaxkonos.mobile.servercenter

import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.util.zip.ZipEntry
import java.util.zip.ZipException
import java.util.zip.ZipFile

/** Result of validating a complete signed release ZIP before SFTP upload. */
data class ServerReleaseArchiveVerification(
    val problemCode: String?,
    val manifest: ServerReleaseManifest?,
    val archiveSha256: String?,
) {
    val verified: Boolean get() = problemCode == null && manifest != null
}

/**
 * Verifies the archive digest, pinned manifest signature, RID, layout and every signed payload file.
 * The public key is supplied by application release configuration; a key carried inside the ZIP is
 * never considered a trust root.
 */
object ServerReleaseArchiveVerifier {
    private const val MAXIMUM_MANIFEST_BYTES = 1024 * 1024
    private const val MAXIMUM_SIGNATURE_BYTES = 16 * 1024
    private const val MAXIMUM_ENTRIES = 20_000
    private const val MAXIMUM_PAYLOAD_BYTES = 8L * 1024 * 1024 * 1024

    fun verify(
        archiveFile: File,
        expectedKind: ServerReleasePackageKind,
        expectedRuntime: ServerRuntimeIdentifier,
        trustedPublicKeys: Map<String, String>,
        expectedArchiveSha256: String? = null,
    ): ServerReleaseArchiveVerification {
        require(archiveFile.isFile) { "Release archive must be a readable file." }
        val archiveDigest = archiveFile.inputStream().use { sha256Hex(it)!! }
        if (expectedArchiveSha256 != null &&
            (!ServerDeploymentInputRules.isSha256(expectedArchiveSha256) ||
                !archiveDigest.equals(expectedArchiveSha256, ignoreCase = true))
        ) return failed(ServerDeploymentProblemCodes.PACKAGE_DIGEST_MISMATCH, archiveDigest)

        return try {
            if (!hasSafeCentralDirectory(archiveFile)) {
                return failed(ServerDeploymentProblemCodes.PACKAGE_LAYOUT_UNSAFE, archiveDigest)
            }
            ZipFile(archiveFile).use { archive ->
                val enumeration = archive.entries()
                val entries = linkedMapOf<String, ZipEntry>()
                var count = 0
                while (enumeration.hasMoreElements()) {
                    val entry = enumeration.nextElement()
                    if (++count > MAXIMUM_ENTRIES) {
                        return failed(ServerDeploymentProblemCodes.PACKAGE_LAYOUT_UNSAFE, archiveDigest)
                    }
                    val normalized = entry.name.trimEnd('/')
                    if (!ServerReleaseValidation.isSafeManifestPath(normalized)) {
                        return failed(ServerDeploymentProblemCodes.PACKAGE_LAYOUT_UNSAFE, archiveDigest)
                    }
                    if (!entry.isDirectory && entries.put(entry.name, entry) != null) {
                        return failed(ServerDeploymentProblemCodes.PACKAGE_LAYOUT_UNSAFE, archiveDigest)
                    }
                }

                val manifestEntry = entries["manifest.json"]
                    ?: return failed(ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID, archiveDigest)
                val signatureEntry = entries["manifest.json.sig"]
                    ?: return failed(ServerDeploymentProblemCodes.PACKAGE_SIGNATURE_INVALID, archiveDigest)
                val manifestBytes = archive.getInputStream(manifestEntry).use {
                    readBounded(it, manifestEntry.size, MAXIMUM_MANIFEST_BYTES)
                }
                val signatureBytes = archive.getInputStream(signatureEntry).use {
                    readBounded(it, signatureEntry.size, MAXIMUM_SIGNATURE_BYTES)
                }
                val manifest = ServerDeploymentWire.readManifest(manifestBytes)
                val signature = ServerDeploymentWire.readSignature(signatureBytes)
                if (manifest.packageKind != expectedKind) {
                    return failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
                }
                val publicKey = trustedPublicKeys[signature.keyId]
                    ?: return failed(ServerDeploymentProblemCodes.PACKAGE_TRUST_ROOT_MISSING, archiveDigest)
                val signatureProblem = ServerReleaseValidation.verifyManifest(
                    manifestBytes = manifestBytes,
                    manifest = manifest,
                    expectedRuntime = expectedRuntime,
                    signature = signature,
                    publicKeyPem = publicKey,
                    trustPolicy = ServerReleaseTrustPolicy(listOf(signature.keyId)),
                )
                if (signatureProblem != null) return failed(signatureProblem, archiveDigest)

                val signedFiles = manifest.files.associateBy { it.path }
                if (signedFiles.size != manifest.files.size) {
                    return failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
                }
                var totalLength = 0L
                for (file in manifest.files) {
                    val entry = entries[file.path]
                        ?: return failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
                    if (entry.size != file.length || file.length < 0 ||
                        file.length > MAXIMUM_PAYLOAD_BYTES - totalLength
                    ) return failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
                    totalLength += file.length
                    val actual = archive.getInputStream(entry).use { sha256Hex(it, file.length) }
                    if (actual == null || !actual.equals(file.sha256, ignoreCase = true)) {
                        return failed(ServerDeploymentProblemCodes.PACKAGE_DIGEST_MISMATCH, archiveDigest)
                    }
                }
                if (entries.keys.any { it != "manifest.json" && it != "manifest.json.sig" && it !in signedFiles }) {
                    return failed(ServerDeploymentProblemCodes.PACKAGE_LAYOUT_UNSAFE, archiveDigest)
                }
                ServerReleaseArchiveVerification(null, manifest, archiveDigest)
            }
        } catch (_: ZipException) {
            failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
        } catch (_: IOException) {
            failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
        } catch (_: IllegalArgumentException) {
            failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
        } catch (_: ArithmeticException) {
            failed(ServerDeploymentProblemCodes.PACKAGE_MANIFEST_INVALID, archiveDigest)
        }
    }

    private fun failed(code: String, digest: String?) = ServerReleaseArchiveVerification(code, null, digest)

    private fun readBounded(input: InputStream, declaredLength: Long, maximum: Int): ByteArray {
        require(declaredLength in 0..maximum.toLong()) { "Release metadata exceeds its size limit." }
        val output = java.io.ByteArrayOutputStream(declaredLength.toInt())
        val buffer = ByteArray(8192)
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            require(output.size() <= maximum - count) { "Release metadata exceeds its size limit." }
            output.write(buffer, 0, count)
        }
        require(output.size().toLong() == declaredLength) { "Release metadata length changed." }
        return output.toByteArray()
    }

    private fun sha256Hex(input: InputStream, expectedLength: Long? = null): String? {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(81920)
        var received = 0L
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            if (expectedLength != null && count > expectedLength - received) return null
            received += count
            digest.update(buffer, 0, count)
        }
        if (expectedLength != null && received != expectedLength) return null
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    /**
     * `ZipEntry` does not expose Unix mode bits. Inspect the central directory directly so a signed
     * entry marked as a symbolic link is rejected before upload, matching the desktop verifier.
     */
    private fun hasSafeCentralDirectory(file: File): Boolean = RandomAccessFile(file, "r").use { archive ->
        if (archive.length() < END_MINIMUM_SIZE) return false
        val searchLength = minOf(archive.length(), END_MAXIMUM_SEARCH.toLong()).toInt()
        val tail = ByteArray(searchLength)
        archive.seek(archive.length() - searchLength)
        archive.readFully(tail)
        var end = tail.size - END_MINIMUM_SIZE
        while (end >= 0 && littleInt(tail, end) != END_SIGNATURE) end--
        if (end < 0) return false
        val entries = littleShort(tail, end + 10)
        val directorySize = littleUInt(tail, end + 12)
        val directoryOffset = littleUInt(tail, end + 16)
        if (entries == 0xffff || directorySize == 0xffffffffL || directoryOffset == 0xffffffffL ||
            directoryOffset + directorySize > archive.length()
        ) return false

        archive.seek(directoryOffset)
        repeat(entries) {
            val header = ByteArray(CENTRAL_FIXED_SIZE)
            archive.readFully(header)
            if (littleInt(header, 0) != CENTRAL_SIGNATURE) return false
            val flags = littleShort(header, 8)
            if (flags and 1 != 0) return false // encrypted entries are never valid release payloads
            val nameLength = littleShort(header, 28)
            val extraLength = littleShort(header, 30)
            val commentLength = littleShort(header, 32)
            val externalAttributes = littleUInt(header, 38)
            val unixType = (externalAttributes ushr 16) and 0xF000
            if (unixType == 0xA000L) return false
            archive.seek(archive.filePointer + nameLength + extraLength + commentLength)
        }
        archive.filePointer == directoryOffset + directorySize
    }

    private fun littleShort(bytes: ByteArray, offset: Int): Int =
        (bytes[offset].toInt() and 0xff) or ((bytes[offset + 1].toInt() and 0xff) shl 8)

    private fun littleInt(bytes: ByteArray, offset: Int): Int =
        littleUInt(bytes, offset).toInt()

    private fun littleUInt(bytes: ByteArray, offset: Int): Long =
        littleShort(bytes, offset).toLong() or (littleShort(bytes, offset + 2).toLong() shl 16)

    private const val END_SIGNATURE = 0x06054b50
    private const val CENTRAL_SIGNATURE = 0x02014b50
    private const val END_MINIMUM_SIZE = 22
    private const val END_MAXIMUM_SEARCH = 65_557
    private const val CENTRAL_FIXED_SIZE = 46
}
