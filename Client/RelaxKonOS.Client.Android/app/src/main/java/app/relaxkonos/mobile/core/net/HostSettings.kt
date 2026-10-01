package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

// HTTP uses the shared camelCase enum converter; local Kotlin enum names are never wire values.
enum class HostSettingKind(val path: String, val capability: String, val resource: String) {
    Time("time", "hostTimeChange", "host/time"), Identity("identity", "hostIdentityChange", "host/identity"),
    Environment("environment", "hostEnvironmentChange", "host/environment/machine")
}
enum class HostEnvironmentScope(val query: String) { HostMachine("hostMachine"), HostUser("hostUser") }
data class HostSettingsTarget(val resourceId: String, val scope: String, val platformIdentity: String?)
data class HostSettingsCapability(val state: String, val reasonCode: String?)
data class HostTimeSettings(val zoneId: String, val zones: List<String>, val revision: String, val observedAt: String,
    val provider: String, val target: HostSettingsTarget, val capability: HostSettingsCapability, val effectiveState: String)
data class HostIdentitySettings(val name: String, val pendingName: String, val maximumLength: Int, val revision: String,
    val observedAt: String, val provider: String, val target: HostSettingsTarget, val capability: HostSettingsCapability, val effectiveState: String)
data class HostEnvironmentVariable(val name: String, val rawValue: String?, val expandedPreview: String?, val valueKind: String,
    val source: String, val sensitive: Boolean, val masked: Boolean, val warnings: List<String>)
data class HostEnvironmentSettings(val target: HostSettingsTarget, val revision: String, val observedAt: String,
    val capability: HostSettingsCapability, val effectiveState: String, val provider: String, val caseSensitiveNames: Boolean,
    val pathSeparator: String, val variables: List<HostEnvironmentVariable>)
data class HostSettingsDifference(val settingId: String, val before: String?, val after: String?)
data class HostSettingsPlan(val id: String, val target: HostSettingsTarget, val expectedRevision: String, val expiresAt: String,
    val differences: List<HostSettingsDifference>, val requiredCapability: String, val authorizationTarget: String,
    val effectiveState: String, val impactCode: String)
data class HostSettingsOperation(val id: String, val settingId: String, val target: HostSettingsTarget, val state: String,
    val updatedAt: String, val observedRevision: String?, val problemCode: String?, val effectiveState: String) {
    val terminal get() = state in setOf("applied", "failed", "rolledBack")
}
data class HostEnvironmentMutation(val name: String, val delete: Boolean, val value: String?, val expand: Boolean = false)
object HostSettingsRules {
    fun revision(value: String) = Regex("[0-9A-F]{64}").matches(value)
    fun id(value: String): String = UUID.fromString(value).toString().also { require(it == value) }
    fun hostname(value: String, maximum: Int): Boolean = value.length in 1..maximum &&
        Regex("[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?").matches(value) && !value.all { it in '0'..'9' }
    fun mutation(value: HostEnvironmentMutation, windows: Boolean): Boolean = value.name.length in 1..255 &&
        validUnicode(value.name) && value.name.none { it == '=' || it.isISOControl() } &&
        (windows || Regex("[A-Za-z_][A-Za-z0-9_]*").matches(value.name)) &&
        if (value.delete) value.value == null && !value.expand else value.value != null && value.value.length <= 32767 &&
            '\u0000' !in value.value && validUnicode(value.value) && (!value.expand || windows) && value.value.toByteArray(Charsets.UTF_8).size + value.name.toByteArray(Charsets.UTF_8).size <= 256 * 1024
    private fun validUnicode(value: String): Boolean {
        var i = 0
        while (i < value.length) {
            if (Character.isHighSurrogate(value[i])) { i++; if(i >= value.length || !Character.isLowSurrogate(value[i])) return false }
            else if(Character.isLowSurrogate(value[i])) return false
            i++
        }
        return true
    }
    fun highImpact(name: String, windows: Boolean): Boolean {
        val n = if (windows) name.uppercase(java.util.Locale.ROOT) else name
        return n in setOf("PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR", "DOTNET_ROOT", "DOTNET_STARTUP_HOOKS", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "NODE_OPTIONS", "PYTHONPATH", "PYTHONHOME", "PERL5OPT", "RUBYOPT") ||
            listOf("LD_", "DYLD_", "COR_", "CORECLR_", "DOTNET_").any(n::startsWith)
    }
}
object HostSettingsRoutes {
    const val ROOT = "/api/v1.0/host-settings"
    fun environment(scope: HostEnvironmentScope, reveal: Boolean) = "$ROOT/environment?scope=${scope.query}&reveal=$reveal"
    fun target(scope: HostEnvironmentScope) = "$ROOT/environment/target?scope=${scope.query}"
    fun operation(id: String) = "/api/v1.0/settings/operations/${HostSettingsRules.id(id)}"
}
internal object HostSettingsWire {
    private val effects = setOf("immediate", "newProcess", "newLogin", "serviceRestart", "hostRestart")
    fun target(raw: String) = target(JSONObject(raw))
    private fun target(j: JSONObject) = HostSettingsTarget(j.getString("resourceId"), j.getString("scope").also { require(it in setOf("hostMachine", "hostUser")) }, j.nullText("platformIdentity"))
    private fun capability(j: JSONObject) = HostSettingsCapability(j.getString("state").also { require(it in setOf("available", "unauthenticated", "offline", "loadingFailed", "accessDenied", "elevationRequired", "helperUnavailable", "platformUnsupported", "policyLocked", "recoveryUnavailable")) }, j.nullText("reasonCode"))
    private fun effect(j: JSONObject) = j.getString("effectiveState").also { require(it in effects) }
    private fun revision(j: JSONObject, key: String = "revision") = j.getString(key).also { require(HostSettingsRules.revision(it)) }
    private fun instant(j: JSONObject, key: String) = j.getString(key).also { IsoInstant.requireEpochMillis(it) }
    fun time(raw: String): HostTimeSettings = JSONObject(raw).let { j -> val v = j.getJSONObject("value")
        HostTimeSettings(v.getString("timeZoneId"), v.getJSONArray("availableTimeZoneIds").texts(4096), revision(v), instant(v,"observedAt"),
            v.getString("provider"), target(j.getJSONObject("target")), capability(j.getJSONObject("capability")), effect(j)) }
    fun identity(raw: String): HostIdentitySettings = JSONObject(raw).let { j -> val v = j.getJSONObject("value")
        HostIdentitySettings(v.getString("hostName"), v.getString("pendingHostName"), v.getInt("maximumHostNameLength").also { require(it in 1..63) }, revision(v),
            instant(v,"observedAt"), v.getString("provider"), target(j.getJSONObject("target")), capability(j.getJSONObject("capability")), effect(j)) }
    fun environment(raw: String): HostEnvironmentSettings = JSONObject(raw).let { j ->
        HostEnvironmentSettings(target(j.getJSONObject("target")), revision(j), instant(j,"observedAt"), capability(j.getJSONObject("capability")), effect(j),
            j.getString("provider"), j.getBoolean("caseSensitiveNames"), j.getString("pathSeparator"), j.getJSONArray("variables").objects(4096) { v ->
                HostEnvironmentVariable(v.getString("name"), v.nullText("rawValue"), v.nullText("expandedPreview"), v.getString("valueKind").also { require(it in setOf("string","expandString")) },
                    v.getString("source"), v.getBoolean("sensitive"), v.getBoolean("masked"), v.getJSONArray("warnings").texts(64)).also {
                    require(!it.masked || it.rawValue == null && it.expandedPreview == null)
                }
            }) }
    fun plan(raw: String): HostSettingsPlan = JSONObject(raw).let { j ->
        HostSettingsPlan(HostSettingsRules.id(j.getString("planId")), target(j.getJSONObject("target")), revision(j,"expectedRevision"), instant(j,"expiresAt"),
            j.getJSONArray("differences").objects(128) { d -> HostSettingsDifference(d.getString("settingId"), d.nullText("before"), d.nullText("after")) },
            j.getString("requiredCapability"), j.getString("authorizationTarget"), effect(j), j.getString("impactCode")) }
    fun operation(raw: String): HostSettingsOperation = JSONObject(raw).let { j ->
        HostSettingsOperation(HostSettingsRules.id(j.getString("operationId")), j.getString("settingId"), target(j.getJSONObject("target")), j.getString("state").also {
            require(it in setOf("prepared","applying","applied","failed","partiallyApplied","unknown","awaitingConfirmation","rolledBack","recoveryRequired")) },
            instant(j,"updatedAt"), j.nullText("observedRevision")?.also { require(HostSettingsRules.revision(it)) }, j.nullText("problemCode"), effect(j)).also { require(it.state !in setOf("applied","rolledBack") || it.observedRevision != null) } }
    private fun JSONObject.nullText(key: String): String? { require(has(key)); return if (isNull(key)) null else get(key) as? String ?: error("Expected text") }
    private fun JSONArray.texts(max: Int): List<String> { require(length() <= max); return (0 until length()).map { get(it) as String } }
    private fun <T> JSONArray.objects(max: Int, parse: (JSONObject) -> T): List<T> { require(length() <= max); return (0 until length()).map { parse(getJSONObject(it)) } }
}
