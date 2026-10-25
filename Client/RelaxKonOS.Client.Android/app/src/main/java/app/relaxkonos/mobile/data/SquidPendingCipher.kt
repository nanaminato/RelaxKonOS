package app.relaxkonos.mobile.data

import javax.crypto.Cipher
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** The journal identity prevents moving a valid request to another server/account/workspace. */
internal object SquidPendingCipher {
    fun encrypt(key: SecretKey, text: String, identity: String): ByteArray = Cipher.getInstance("AES/GCM/NoPadding").run {
        init(Cipher.ENCRYPT_MODE, key)
        updateAAD(identity.toByteArray(Charsets.UTF_8))
        iv + doFinal(text.toByteArray(Charsets.UTF_8))
    }
    fun decrypt(key: SecretKey, bytes: ByteArray, identity: String): String = Cipher.getInstance("AES/GCM/NoPadding").run {
        require(bytes.size >= 28)
        init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, bytes.copyOfRange(0, 12)))
        updateAAD(identity.toByteArray(Charsets.UTF_8))
        String(doFinal(bytes.copyOfRange(12, bytes.size)), Charsets.UTF_8)
    }
}
