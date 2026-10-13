package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import java.util.UUID

/**
 * Rendering backends understood by the wire protocol.
 *
 * Mirrors `RelaxKonOS.Protocol.Workspace.TerminalBackends` on the desktop/server side. Android keeps the
 * field as a pass-through value: the mobile client only ever renders with its own terminal, but it must
 * not drop a backend chosen on the desktop when it saves an unrelated appearance change.
 */
object TerminalBackends {
    const val BUILT_IN = "builtin"
    const val XTERM_JS = "xterm"
    val all = listOf(BUILT_IN, XTERM_JS)
    fun isKnown(value: String?): Boolean = value != null && value.trim().lowercase() in all
    /** Absent/null keeps legacy documents on the built-in renderer; tokens this client does not know do the same. */
    fun normalize(value: String?): String {
        val token = value?.trim()?.lowercase() ?: return BUILT_IN
        return if (token in all) token else BUILT_IN
    }
}

data class TerminalSettings(val fontFamily: String, val fontSize: Double, val colorScheme: String,
    val backgroundColor: String, val foregroundColor: String, val cursorColor: String,
    val backend: String = TerminalBackends.BUILT_IN,
    val scrollbackLines: Int = 10000, val cursorStyle: String = "block", val cursorBlink: Boolean = true) {
    companion object { val Default = TerminalSettings("Cascadia Mono", 14.0, "Campbell", "#0C0C0C", "#CCCCCC", "#FFFFFF", TerminalBackends.BUILT_IN) }
}
object TerminalSettingsWire {
    fun route(id: String): String {
        val parsed = UUID.fromString(id); require(parsed.toString().equals(id, ignoreCase = true))
        return "/api/v1.0/workspaces/$parsed/terminal-settings"
    }
    fun validate(s: TerminalSettings) {
        require(s.fontFamily.isNotBlank() && s.fontFamily.length <= 128 && s.colorScheme.isNotBlank() && s.colorScheme.length <= 64)
        require(s.fontSize.isFinite() && s.fontSize in 8.0..40.0)
        listOf(s.backgroundColor, s.foregroundColor, s.cursorColor).forEach { require(Regex("#[0-9a-fA-F]{6}").matches(it)) }
        require(s.scrollbackLines in 0..100000 && s.cursorStyle in listOf("block", "underline", "bar"))
        require(TerminalBackends.isKnown(s.backend)) { "Unsupported terminal backend." }
    }
    fun parse(raw: String): TerminalSettings = JSONObject(raw).let { j ->
        require(j.get("fontSize") is Number)
        require(j.get("scrollbackLines") is Number && j.getDouble("scrollbackLines") == j.getInt("scrollbackLines").toDouble())
        require(j.get("cursorBlink") is Boolean)
        TerminalSettings(j.getString("fontFamily"), j.getDouble("fontSize"),
        j.getString("colorScheme"), j.getString("backgroundColor"), j.getString("foregroundColor"), j.getString("cursorColor"),
        TerminalBackends.normalize(readBackend(j)), j.getInt("scrollbackLines"), j.getString("cursorStyle"), j.getBoolean("cursorBlink")).also(::validate) }
    /** The server writes either a string token or nothing at all; any other JSON type is a malformed document. */
    private fun readBackend(j: JSONObject): String? {
        val value = j.opt("backend")
        require(value == null || value === JSONObject.NULL || value is String) { "Unsupported terminal backend." }
        return if (value === JSONObject.NULL) null else value as String?
    }
    fun body(s: TerminalSettings): JsonBody { validate(s); return JsonBody().string("fontFamily", s.fontFamily)
        .raw("fontSize", s.fontSize.toString()).string("colorScheme", s.colorScheme).string("backgroundColor", s.backgroundColor)
        .string("foregroundColor", s.foregroundColor).string("cursorColor", s.cursorColor).string("backend", s.backend)
        .raw("scrollbackLines", s.scrollbackLines.toString()).string("cursorStyle", s.cursorStyle).raw("cursorBlink", s.cursorBlink.toString()) }
    fun scheme(current: TerminalSettings, name: String): TerminalSettings = when (name) {
        "One Half Dark" -> current.copy(colorScheme = name, backgroundColor = "#282C34", foregroundColor = "#DCDFE4", cursorColor = "#FFFFFF")
        "Solarized Dark" -> current.copy(colorScheme = name, backgroundColor = "#002B36", foregroundColor = "#839496", cursorColor = "#93A1A1")
        "Light" -> current.copy(colorScheme = name, backgroundColor = "#FFFFFF", foregroundColor = "#1E1E1E", cursorColor = "#000000")
        "Campbell" -> current.copy(colorScheme = name, backgroundColor = "#0C0C0C", foregroundColor = "#CCCCCC", cursorColor = "#FFFFFF")
        else -> error("Unsupported terminal preset.")
    }
}
