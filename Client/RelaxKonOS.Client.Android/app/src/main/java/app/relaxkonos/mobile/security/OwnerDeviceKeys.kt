package app.relaxkonos.mobile.security

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import java.io.File
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.suspendCancellableCoroutine
import org.json.JSONArray
import org.json.JSONObject

/** The non-secret half of one Android owner-device enrollment. */
data class OwnerDeviceRegistration(val serviceId: String, val deviceId: String)

/** Raised when a server nonce cannot be signed by this device's Android Keystore key. */
class OwnerDeviceSigningException(message: String, cause: Throwable? = null) : IllegalStateException(message, cause)

/**
 * Stores only `(serviceId, deviceId)` outside the Android Keystore. The P-256 private key is
 * non-exportable and remains under an alias derived from the normalized server identity.
 */
class OwnerDeviceKeyStore(
    context: Context,
    private val biometricCapability: BiometricCapabilityDetector,
) {
    private val storage = File(context.noBackupFilesDir, "owner-device-registrations.json")

    fun isPaired(serviceId: String): Boolean = registration(serviceId) != null

    fun registration(serviceId: String): OwnerDeviceRegistration? = registrations()
        .firstOrNull { it.serviceId == serviceId }

    /** Replaces a stale local key before an invitation is consumed. */
    fun create(serviceId: String): String {
        require(serviceId.isNotBlank()) { "A server identity is required." }
        remove(serviceId)
        val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEY_STORE)
        val policy = biometricCapability.detect()
        val builder = KeyGenParameterSpec.Builder(keyAlias(serviceId), KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
        configureAuthentication(builder, policy)
        generator.initialize(builder.build())
        val publicKey = generator.generateKeyPair().public.encoded
        return Base64.encodeToString(publicKey, Base64.NO_WRAP)
    }

    fun save(registration: OwnerDeviceRegistration) {
        val values = registrations().filterNot { it.serviceId == registration.serviceId } + registration
        val temporary = File(storage.parentFile, storage.name + ".tmp")
        temporary.writeText(JSONArray().apply {
            values.forEach { put(JSONObject().put("serviceId", it.serviceId).put("deviceId", it.deviceId)) }
        }.toString(), Charsets.UTF_8)
        if (!temporary.renameTo(storage)) {
            temporary.delete()
            throw IllegalStateException("Could not persist the owner-device registration.")
        }
    }

    fun remove(serviceId: String) {
        keyStore().deleteEntry(keyAlias(serviceId))
        val retained = registrations().filterNot { it.serviceId == serviceId }
        if (retained.isEmpty()) {
            storage.delete()
            return
        }
        val temporary = File(storage.parentFile, storage.name + ".tmp")
        temporary.writeText(JSONArray().apply {
            retained.forEach { put(JSONObject().put("serviceId", it.serviceId).put("deviceId", it.deviceId)) }
        }.toString(), Charsets.UTF_8)
        if (!temporary.renameTo(storage)) {
            temporary.delete()
            throw IllegalStateException("Could not update the owner-device registrations.")
        }
    }

    /** Signs [nonce] with the key for [serviceId], asking the owner when Android protects the key. */
    suspend fun sign(activity: FragmentActivity, serviceId: String, nonce: ByteArray): ByteArray {
        val privateKey = keyStore().getKey(keyAlias(serviceId), null) as? PrivateKey
            ?: throw OwnerDeviceSigningException("This Android device is not paired with the selected server.")
        val signature = try {
            Signature.getInstance("SHA256withECDSA").apply {
                initSign(privateKey)
                update(nonce)
            }
        } catch (error: Exception) {
            throw OwnerDeviceSigningException("The Android owner-device key is unavailable.", error)
        }
        return when (keyAuthenticationPolicy()) {
            KeyAuthenticationPolicy.None -> signature.sign()
            KeyAuthenticationPolicy.StrongBiometric -> authorizeStrongBiometric(activity, signature)
            KeyAuthenticationPolicy.DeviceUnlockWindow -> {
                authorizeDeviceWindow(activity)
                try {
                    signature.sign()
                } catch (error: Exception) {
                    throw OwnerDeviceSigningException("The device unlock did not authorize the owner-device key.", error)
                }
            }
        }
    }

    private fun registrations(): List<OwnerDeviceRegistration> {
        if (!storage.isFile) return emptyList()
        return runCatching {
            val values = JSONArray(storage.readText(Charsets.UTF_8))
            buildList {
                for (index in 0 until values.length()) {
                    val value = values.getJSONObject(index)
                    val serviceId = value.getString("serviceId")
                    val deviceId = value.getString("deviceId")
                    if (serviceId.isNotBlank() && deviceId.isNotBlank()) add(OwnerDeviceRegistration(serviceId, deviceId))
                }
            }
        }.getOrElse { emptyList() }
    }

    private fun keyAuthenticationPolicy(): KeyAuthenticationPolicy = when (biometricCapability.detect()) {
        BiometricCapability.Strong -> KeyAuthenticationPolicy.StrongBiometric
        BiometricCapability.WeakOnly, BiometricCapability.DeviceCredentialOnly -> KeyAuthenticationPolicy.DeviceUnlockWindow
        BiometricCapability.None -> KeyAuthenticationPolicy.None
    }

    private suspend fun authorizeStrongBiometric(activity: FragmentActivity, signature: Signature): ByteArray =
        suspendCancellableCoroutine { continuation ->
            val prompt = BiometricPrompt(activity, ContextCompat.getMainExecutor(activity), object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                    val authorized = result.cryptoObject?.signature
                    if (authorized == null) continuation.resumeWithException(OwnerDeviceSigningException("The biometric prompt did not return a signature."))
                    else runCatching { authorized.sign() }
                        .onSuccess { continuation.resume(it) }
                        .onFailure { continuation.resumeWithException(OwnerDeviceSigningException("Could not sign the owner-device challenge.", it)) }
                }

                override fun onAuthenticationError(code: Int, message: CharSequence) {
                    continuation.resumeWithException(OwnerDeviceSigningException(message.toString()))
                }
            })
            continuation.invokeOnCancellation { prompt.cancelAuthentication() }
            prompt.authenticate(
                BiometricPrompt.PromptInfo.Builder()
                    .setTitle("Sign in with this device")
                    .setSubtitle("Confirm to use the Android owner-device key")
                    .setNegativeButtonText("Cancel")
                    .setAllowedAuthenticators(androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_STRONG)
                    .build(),
                BiometricPrompt.CryptoObject(signature),
            )
        }

    private suspend fun authorizeDeviceWindow(activity: FragmentActivity): Unit = suspendCancellableCoroutine { continuation ->
        val prompt = BiometricPrompt(activity, ContextCompat.getMainExecutor(activity), object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = continuation.resume(Unit)
            override fun onAuthenticationError(code: Int, message: CharSequence) =
                continuation.resumeWithException(OwnerDeviceSigningException(message.toString()))
        })
        continuation.invokeOnCancellation { prompt.cancelAuthentication() }
        val builder = BiometricPrompt.PromptInfo.Builder()
            .setTitle("Sign in with this device")
            .setSubtitle("Unlock the Android owner-device key")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            builder.setAllowedAuthenticators(
                androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_STRONG or
                    androidx.biometric.BiometricManager.Authenticators.DEVICE_CREDENTIAL,
            )
        } else {
            @Suppress("DEPRECATION") builder.setDeviceCredentialAllowed(true)
        }
        prompt.authenticate(builder.build())
    }

    private fun configureAuthentication(builder: KeyGenParameterSpec.Builder, capability: BiometricCapability) {
        when (capability) {
            BiometricCapability.None -> builder.setUserAuthenticationRequired(false)
            BiometricCapability.Strong -> {
                builder.setUserAuthenticationRequired(true)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    builder.setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
                } else {
                    @Suppress("DEPRECATION") builder.setUserAuthenticationValidityDurationSeconds(-1)
                }
            }
            BiometricCapability.WeakOnly, BiometricCapability.DeviceCredentialOnly -> {
                builder.setUserAuthenticationRequired(true)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    builder.setUserAuthenticationParameters(
                        DEVICE_UNLOCK_WINDOW_SECONDS,
                        KeyProperties.AUTH_BIOMETRIC_STRONG or KeyProperties.AUTH_DEVICE_CREDENTIAL,
                    )
                } else {
                    @Suppress("DEPRECATION") builder.setUserAuthenticationValidityDurationSeconds(DEVICE_UNLOCK_WINDOW_SECONDS)
                }
            }
        }
    }

    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }

    private fun keyAlias(serviceId: String): String = "relaxkonos.owner-device." + MessageDigest.getInstance("SHA-256")
        .digest(serviceId.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }

    private enum class KeyAuthenticationPolicy { None, StrongBiometric, DeviceUnlockWindow }

    private companion object {
        const val ANDROID_KEY_STORE = "AndroidKeyStore"
        const val DEVICE_UNLOCK_WINDOW_SECONDS = 300
    }
}
