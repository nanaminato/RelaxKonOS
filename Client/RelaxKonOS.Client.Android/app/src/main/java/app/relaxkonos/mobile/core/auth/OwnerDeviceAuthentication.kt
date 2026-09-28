package app.relaxkonos.mobile.core.auth

import android.os.Build
import android.util.Base64
import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.IsoInstant
import app.relaxkonos.mobile.core.net.LoginSession
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.security.OwnerDeviceKeyStore
import app.relaxkonos.mobile.security.OwnerDeviceRegistration
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentity
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import java.net.URI
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** A validated one-time pairing invitation emitted by an already enrolled Windows owner device. */
data class OwnerDevicePairingInvitation(
    val serverUrl: String,
    val token: String,
    val expiresAtEpochMillis: Long,
) {
    companion object {
        /** Parses the desktop QR/paste payload. There is intentionally no second Android-only format. */
        fun parse(payload: String, nowEpochMillis: Long = System.currentTimeMillis()): OwnerDevicePairingInvitation {
            try {
                val json = org.json.JSONObject(String(Base64.decode(payload.trim(), Base64.DEFAULT), Charsets.UTF_8))
                require(json.getInt("version") == 1)
                val serverUrl = json.getString("serverUrl")
                val uri = URI(serverUrl)
                // Pairing originates on the desktop client, whose current contract permits a
                // reachable non-loopback HTTP or HTTPS endpoint. Do not make Android reject an
                // otherwise valid desktop invitation before it can reach the Server.
                require(uri.scheme.equals("http", ignoreCase = true) || uri.scheme.equals("https", ignoreCase = true))
                require(!uri.host.isNullOrBlank())
                require(uri.userInfo.isNullOrEmpty() && uri.query.isNullOrEmpty() && uri.fragment.isNullOrEmpty())
                require(uri.path.isNullOrEmpty() || uri.path == "/")
                val expiresAt = IsoInstant.toEpochMillis(json.getString("expiresAt")) ?: throw IllegalArgumentException()
                require(expiresAt > nowEpochMillis)
                val token = json.getString("token").trim()
                require(token.isNotEmpty())
                return OwnerDevicePairingInvitation(
                    serverUrl = ServerConnectionIdentityRules.normalizeServerUrl(serverUrl),
                    token = token,
                    expiresAtEpochMillis = expiresAt,
                )
            } catch (error: Exception) {
                throw IllegalArgumentException("The pairing code is invalid or has expired.", error)
            }
        }
    }
}

/** Coordinates Android Keystore enrollment and the server's owner-device challenge protocol. */
class OwnerDeviceAuthenticationService(
    private val gateway: RelaxKonGateway,
    private val keys: OwnerDeviceKeyStore,
    private val deviceName: String = "${Build.MANUFACTURER} ${Build.MODEL}".trim(),
) {
    fun isPaired(connection: ServerConnectionIdentity): Boolean = keys.isPaired(connection.serviceId)

    /**
     * Generates a fresh local P-256 key only after the invitation was validated, then consumes the
     * invitation. A failed enrollment removes that new key; an existing key is never silently reused.
     */
    suspend fun pair(payload: String): ApiResult<ServerConnectionIdentity> {
        val invitation = OwnerDevicePairingInvitation.parse(payload)
        val connection = ServerConnectionIdentityRules.direct(invitation.serverUrl)
        if (keys.isPaired(connection.serviceId)) {
            return ApiResult.Transport("This Android device is already paired with the selected server.")
        }
        val publicKeySpki = withContext(Dispatchers.Default) { keys.create(connection.serviceId) }
        return when (val enrolled = gateway.acceptOwnerDeviceInvitation(
            connection.effectiveBaseUrl, invitation.token, deviceName, publicKeySpki,
        )) {
            is ApiResult.Success -> {
                withContext(Dispatchers.Default) {
                    keys.save(OwnerDeviceRegistration(connection.serviceId, enrolled.value.deviceId))
                }
                ApiResult.Success(connection)
            }
            is ApiResult.Problem -> {
                withContext(Dispatchers.Default) { keys.remove(connection.serviceId) }
                enrolled
            }
            is ApiResult.Transport -> {
                withContext(Dispatchers.Default) { keys.remove(connection.serviceId) }
                enrolled
            }
        }
    }

    /** Obtains, signs and consumes a nonce. The host password is never present in this flow. */
    suspend fun signIn(connection: ServerConnectionIdentity, activity: FragmentActivity): ApiResult<LoginSession> {
        val registration = keys.registration(connection.serviceId)
            ?: return ApiResult.Transport("This Android device is not paired with the selected server.")
        return when (val challenge = gateway.ownerDeviceChallenge(connection.effectiveBaseUrl, registration.deviceId)) {
            is ApiResult.Success -> {
                val nonce = decodeBase64Url(challenge.value.nonce)
                try {
                    val signature = Base64.encodeToString(keys.sign(activity, connection.serviceId, nonce), Base64.NO_WRAP)
                    gateway.signInWithOwnerDevice(
                        connection.effectiveBaseUrl,
                        challenge.value.challengeId,
                        registration.deviceId,
                        signature,
                    )
                } finally {
                    nonce.fill(0)
                }
            }
            is ApiResult.Problem -> challenge
            is ApiResult.Transport -> challenge
        }
    }

    private fun decodeBase64Url(value: String): ByteArray {
        val padded = value.replace('-', '+').replace('_', '/').let { encoded ->
            encoded.padEnd(encoded.length + (4 - encoded.length % 4) % 4, '=')
        }
        return Base64.decode(padded, Base64.DEFAULT)
    }
}
