package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import java.util.UUID

data class TerminalSettings(val fontFamily: String, val fontSize: Double, val colorScheme: String,
    val backgroundColor: String, val foregroundColor: String, val cursorColor: String) {
    companion object { val Default = TerminalSettings("Cascadia Mono", 14.0, "Campbell", "#0C0C0C", "#CCCCCC", "#FFFFFF") }
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
    }
    fun parse(raw: String): TerminalSettings = JSONObject(raw).let { j ->
        require(j.get("fontSize") is Number)
        TerminalSettings(j.getString("fontFamily"), j.getDouble("fontSize"),
        j.getString("colorScheme"), j.getString("backgroundColor"), j.getString("foregroundColor"), j.getString("cursorColor")).also(::validate) }
    fun body(s: TerminalSettings): JsonBody { validate(s); return JsonBody().string("fontFamily", s.fontFamily)
        .raw("fontSize", s.fontSize.toString()).string("colorScheme", s.colorScheme).string("backgroundColor", s.backgroundColor)
        .string("foregroundColor", s.foregroundColor).string("cursorColor", s.cursorColor) }
    fun scheme(current: TerminalSettings, name: String): TerminalSettings = when (name) {
        "One Half Dark" -> current.copy(colorScheme = name, backgroundColor = "#282C34", foregroundColor = "#DCDFE4", cursorColor = "#FFFFFF")
        "Solarized Dark" -> current.copy(colorScheme = name, backgroundColor = "#002B36", foregroundColor = "#839496", cursorColor = "#93A1A1")
        "Light" -> current.copy(colorScheme = name, backgroundColor = "#FFFFFF", foregroundColor = "#1E1E1E", cursorColor = "#000000")
        "Campbell" -> current.copy(colorScheme = name, backgroundColor = "#0C0C0C", foregroundColor = "#CCCCCC", cursorColor = "#FFFFFF")
        else -> error("Unsupported terminal preset.")
    }
}
