package app.relaxkonos.mobile.core.net

import java.net.InetAddress
import org.json.JSONArray
import org.json.JSONObject

data class FirewallStatus(val isAvailable: Boolean, val isEnabled: Boolean, val backend: String, val version: String?,
    val defaultIncomingPolicy: String?, val defaultOutgoingPolicy: String?, val problemCode: String)
data class FirewallRule(val number: Int, val action: String, val direction: String, val protocol: String,
    val source: String, val destination: String, val port: String, val addressFamily: String)
data class FirewallResult(val success: Boolean, val problemCode: String)
data class FirewallFacts(val status: FirewallStatus, val rules: List<FirewallRule>)
enum class FirewallChangeKind { Enabled, Defaults, Create, Replace, Delete }
data class FirewallChange(val kind: FirewallChangeKind, val number: Int? = null, val enabled: Boolean? = null,
    val incoming: String? = null, val outgoing: String? = null, val rule: FirewallRule? = null) {
    fun validate() {
        when (kind) {
            FirewallChangeKind.Enabled -> require(enabled != null)
            FirewallChangeKind.Defaults -> require(incoming in FirewallValues.policies && outgoing in FirewallValues.policies)
            FirewallChangeKind.Create, FirewallChangeKind.Replace -> {
                val value = requireNotNull(rule)
                require(value.action in FirewallValues.actions && value.direction in FirewallValues.directions && value.protocol in FirewallValues.protocols)
                require(FirewallValues.endpoint(value.source) && FirewallValues.endpoint(value.destination) && FirewallValues.port(value.port))
            }
            FirewallChangeKind.Delete -> Unit
        }
        if (kind in setOf(FirewallChangeKind.Replace, FirewallChangeKind.Delete)) require(number in 1..10_000)
    }
    fun body(): JsonBody {
        validate()
        val body = JsonBody()
        when (kind) {
            FirewallChangeKind.Enabled -> body.raw("enabled", enabled.toString())
            FirewallChangeKind.Defaults -> body.string("incomingPolicy", requireNotNull(incoming)).string("outgoingPolicy", requireNotNull(outgoing))
            FirewallChangeKind.Create, FirewallChangeKind.Replace -> requireNotNull(rule).let {
                body.string("action", it.action).string("direction", it.direction).string("protocol", it.protocol)
                    .string("source", it.source).string("destination", it.destination).string("port", it.port)
            }
            FirewallChangeKind.Delete -> Unit
        }
        return body
    }
}
object FirewallValues {
    val actions = listOf("allow", "deny", "reject", "limit")
    val policies = listOf("allow", "deny", "reject")
    val directions = listOf("in", "out")
    val protocols = listOf("tcp", "udp", "any")
    fun port(value: String): Boolean {
        if (value == "any" || value.isEmpty()) return true
        val parts = value.split(':'); if (parts.size !in 1..2) return false
        val numbers = parts.map { it.toIntOrNull() ?: return false }
        return numbers.all { it in 1..65535 } && (numbers.size == 1 || numbers[0] <= numbers[1])
    }
    fun endpoint(value: String): Boolean {
        if (value in setOf("any", "anywhere")) return true
        val parts = value.split('/'); if (parts.size !in 1..2) return false
        val address = parts[0]
        val ipv6 = address.contains(':')
        val valid = if (ipv6) address.matches(Regex("[0-9a-fA-F:.]+")) && runCatching { InetAddress.getByName(address) }.isSuccess
            else address.split('.').let { bytes -> bytes.size == 4 && bytes.all { it.isNotEmpty() && it.all(Char::isDigit) && it.toIntOrNull() in 0..255 } }
        return valid && (parts.size == 1 || parts[1].toIntOrNull() in 0..if (ipv6) 128 else 32)
    }
}
object FirewallRoutes {
    const val ROOT = "/api/v1.0/firewall"
    fun rule(number: Int): String { require(number in 1..10_000); return "$ROOT/rules/$number" }
    fun route(change: FirewallChange): String = when (change.kind) {
        FirewallChangeKind.Enabled -> "$ROOT/enabled"
        FirewallChangeKind.Defaults -> "$ROOT/defaults"
        FirewallChangeKind.Create -> "$ROOT/rules"
        FirewallChangeKind.Replace, FirewallChangeKind.Delete -> rule(requireNotNull(change.number))
    }
    fun method(kind: FirewallChangeKind) = when (kind) { FirewallChangeKind.Create -> "POST"; FirewallChangeKind.Delete -> "DELETE"; else -> "PUT" }
}
internal object FirewallWire {
    fun status(payload: String): FirewallStatus = JSONObject(payload).let {
        FirewallStatus(it.getBoolean("isAvailable"), it.getBoolean("isEnabled"), it.getString("backend"), nullable(it, "version"),
            nullable(it, "defaultIncomingPolicy"), nullable(it, "defaultOutgoingPolicy"), it.getString("problemCode"))
    }
    fun rules(payload: String): List<FirewallRule> = JSONArray(payload).let { array ->
        require(array.length() <= 10_000)
        (0 until array.length()).map { index -> array.getJSONObject(index).let {
            FirewallRule(it.getInt("number").also { number -> require(number in 1..10_000) }, it.getString("action"), it.getString("direction"),
                it.getString("protocol"), it.getString("source"), it.getString("destination"), it.getString("port"), it.getString("addressFamily"))
        } }.also { require(it.map(FirewallRule::number).distinct().size == it.size) }
    }
    fun result(payload: String): FirewallResult = JSONObject(payload).let { FirewallResult(it.getBoolean("success"), it.getString("problemCode")) }
    private fun nullable(json: JSONObject, key: String): String? { require(json.has(key)); return if (json.isNull(key)) null else json.getString(key) }
}
