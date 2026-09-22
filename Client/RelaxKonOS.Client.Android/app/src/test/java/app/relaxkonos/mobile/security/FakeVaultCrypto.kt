package app.relaxkonos.mobile.security

import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * JVM stand-in for [VaultKeyManager].
 *
 * The real key lives in the Android Keystore, which a unit test cannot reach. Everything the vault
 * actually does with a Cipher — AAD binding, GCM tag verification, record isolation — is identical, so
 * using a fixed in-memory key here exercises the vault's own logic rather than the platform's.
 */
class FakeVaultCrypto : VaultCrypto {
    private val key = SecretKeySpec(ByteArray(32) { (it + 7).toByte() }, "AES")

    override fun sealCipher(kind: VaultKind): Cipher =
        Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key) }

    override fun openCipher(kind: VaultKind, iv: ByteArray): Cipher =
        Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, iv)) }

    private companion object {
        const val TRANSFORMATION = "AES/GCM/NoPadding"
    }
}
