package app.relaxkonos.mobile

import android.os.Build
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

sealed interface LoginResult {
    data class Success(val workspaceName: String) : LoginResult
    data class Failure(val message: String) : LoginResult
}

/**
 * Kotlin implementation of the current RelaxKonOS authentication wire contract. Keep route names,
 * JSON property names, and the string enum value synchronized with RelaxKonOS.Protocol.
 */
class RelaxKonApi {
    suspend fun login(serverUrl: String, identifier: String, password: String): LoginResult = withContext(Dispatchers.IO) {
        try {
            val endpoint = URL(serverUrl.trim().trimEnd('/') + "/api/v1.0/auth/login")
            val payload = JSONObject()
                .put("identifier", identifier.trim())
                .put("password", password)
                .put("clientPlatform", "android")
                .put("deviceName", "${Build.MANUFACTURER} ${Build.MODEL}".trim())
                .put("clientVersion", "0.1.0-m0")

            val connection = (endpoint.openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 15_000
                readTimeout = 15_000
                doOutput = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
            }

            connection.outputStream.bufferedWriter(Charsets.UTF_8).use { it.write(payload.toString()) }
            val responseBody = (if (connection.responseCode in 200..299) connection.inputStream else connection.errorStream)
                ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()

            if (connection.responseCode !in 200..299) {
                val detail = runCatching { JSONObject(responseBody).optString("detail") }.getOrNull()
                return@withContext LoginResult.Failure(detail?.takeIf { it.isNotBlank() }
                    ?: "Server returned HTTP ${connection.responseCode}.")
            }

            val workspaceName = JSONObject(responseBody)
                .getJSONObject("workspace")
                .getString("name")
            LoginResult.Success(workspaceName)
        } catch (exception: Exception) {
            LoginResult.Failure(exception.message ?: "Unable to connect to the server.")
        }
    }
}
