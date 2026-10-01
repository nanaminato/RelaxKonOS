package app.relaxkonos.mobile.ui.terminal

import android.graphics.Typeface
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.*
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.TerminalSettings
import app.relaxkonos.mobile.core.net.TerminalSettingsWire
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.theme.Spacing

internal fun terminalColor(hex: String) = Color(0xff000000L or hex.removePrefix("#").toLong(16))
internal fun terminalFont(requested: String): FontFamily = when (requested.lowercase()) {
    "monospace", "sans-serif-monospace" -> FontFamily(Typeface.create(requested, Typeface.NORMAL))
    else -> FontFamily.Monospace // Desktop-installed fonts are not Android system fonts.
}

internal val TerminalExtendedKeys = listOf("Esc" to "\u001b", "Tab" to "\t", "Ctrl+C" to "\u0003", "Ctrl+D" to "\u0004", "Ctrl+Z" to "\u001a",
    "↑" to "\u001b[A", "↓" to "\u001b[B", "←" to "\u001b[D", "→" to "\u001b[C", "Home" to "\u001b[H", "End" to "\u001b[F",
    "PgUp" to "\u001b[5~", "PgDn" to "\u001b[6~", "Ins" to "\u001b[2~", "Del" to "\u001b[3~", "⌫" to "\u007f",
    "F1" to "\u001bOP", "F2" to "\u001bOQ", "F3" to "\u001bOR", "F4" to "\u001bOS", "F5" to "\u001b[15~", "F6" to "\u001b[17~",
    "F7" to "\u001b[18~", "F8" to "\u001b[19~", "F9" to "\u001b[20~", "F10" to "\u001b[21~", "F11" to "\u001b[23~", "F12" to "\u001b[24~")

/** xterm's modifier parameter is 1 + Shift + 2*Alt + 4*Ctrl. */
internal fun terminalKeyPayload(base: String, ctrl: Boolean = false, alt: Boolean = false, shift: Boolean = false, applicationCursor: Boolean = false): String {
    val modifier = 1 + (if (shift) 1 else 0) + (if (alt) 2 else 0) + (if (ctrl) 4 else 0)
    if (base.startsWith("\u001b[") && base.last() in "ABCDHF") {
        val final = base.last()
        return if (modifier > 1) "\u001b[1;${modifier}$final" else if (applicationCursor) "\u001bO$final" else base
    }
    if (base.startsWith("\u001b[") && base.endsWith("~") && modifier > 1) return base.dropLast(1) + ";${modifier}~"
    if (base.startsWith("\u001bO") && base.last() in "PQRS" && modifier > 1) return "\u001b[1;${modifier}${base.last()}"
    if (base == "\t" && shift) return "\u001b[Z"
    if (base == "\u007f" && ctrl) return if (alt) "\u001b\b" else "\b"
    return if (alt && !base.startsWith("\u001b")) "\u001b$base" else base
}

internal fun terminalHardwarePayload(event: KeyEvent, applicationCursor: Boolean): String? {
    val base = when (event.key) {
        Key.Escape -> "\u001b"; Key.Tab -> "\t"
        Key.DirectionUp -> "\u001b[A"; Key.DirectionDown -> "\u001b[B"
        Key.DirectionLeft -> "\u001b[D"; Key.DirectionRight -> "\u001b[C"
        Key.MoveHome -> "\u001b[H"; Key.MoveEnd -> "\u001b[F"
        Key.PageUp -> "\u001b[5~"; Key.PageDown -> "\u001b[6~"
        Key.Insert -> "\u001b[2~"; Key.Delete -> "\u001b[3~"; Key.Backspace -> "\u007f"
        Key.F1 -> "\u001bOP"; Key.F2 -> "\u001bOQ"; Key.F3 -> "\u001bOR"; Key.F4 -> "\u001bOS"
        Key.F5 -> "\u001b[15~"; Key.F6 -> "\u001b[17~"; Key.F7 -> "\u001b[18~"; Key.F8 -> "\u001b[19~"
        Key.F9 -> "\u001b[20~"; Key.F10 -> "\u001b[21~"; Key.F11 -> "\u001b[23~"; Key.F12 -> "\u001b[24~"
        else -> null
    }
    if (base != null) return terminalKeyPayload(base, event.isCtrlPressed, event.isAltPressed, event.isShiftPressed, applicationCursor)
    val native = event.nativeKeyEvent
    val code = native.getUnicodeChar(native.metaState and (android.view.KeyEvent.META_CTRL_MASK or android.view.KeyEvent.META_ALT_MASK).inv())
    if (code !in 32..0x10ffff) return null
    val value = String(Character.toChars(code))
    return if (event.isCtrlPressed || event.isAltPressed) TerminalInputPolicy.payload(value, event.isCtrlPressed, event.isAltPressed) else null
}

@Composable
internal fun TerminalOutputToolbar(p: TerminalPresentation, state: ServerTerminalState, count: Int,
    onCopy: () -> Unit, onPaste: () -> Unit, onClear: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    if (p.searchOpen) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        OutlinedTextField(p.search, { p.search = TerminalInputPolicy.boundedText(it, 256); p.searchIndex = 0 }, singleLine = true, modifier = Modifier.weight(1f),
            label = { Text(stringResource(R.string.terminal_search_count, if (count == 0) 0 else p.searchIndex + 1, count)) })
        TextButton(onClick = { p.searchIndex = (p.searchIndex - 1 + count) % count }, enabled = count > 0) { Text("‹") }
        TextButton(onClick = { p.searchIndex = (p.searchIndex + 1) % count }, enabled = count > 0) { Text("›") }
        TextButton(onClick = { p.searchOpen = false; p.search = "" }) { Text("×") }
    } else Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState())) {
        TextButton(onClick = { p.searchOpen = true }) { Text(stringResource(R.string.terminal_search)) }
        TextButton(onClick = onCopy, enabled = state.output.isNotEmpty()) { Text(stringResource(R.string.terminal_copy_output)) }
        TextButton(onClick = onPaste, enabled = state.canInput) { Text(stringResource(R.string.terminal_paste)) }
        Box {
            TextButton(onClick = { menu = true }) { Text(stringResource(R.string.terminal_tools)) }
            DropdownMenu(menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text(stringResource(R.string.terminal_clear_local)) }, onClick = { menu = false; p.followOutput = true; onClear() })
                DropdownMenuItem(text = { Text(stringResource(R.string.terminal_settings)) }, onClick = { menu = false; p.settingsOpen = true })
            }
        }
    }
    if (p.clipboardFailed) TextButton(onClick = { p.clipboardFailed = false }) { Text(stringResource(R.string.terminal_clipboard_failed)) }
}

@Composable
internal fun TerminalAppearanceDialog(p: TerminalPresentation, onRead: () -> Unit, onSave: (TerminalSettings) -> Unit) {
    var value by remember(p.settings) { mutableStateOf(p.settings.copy(fontSize = p.localFontSize ?: p.settings.fontSize)) }
    var size by remember(p.settings) { mutableStateOf(value.fontSize.toString()) }
    val valid = runCatching { TerminalSettingsWire.validate(value.copy(fontSize = size.toDouble())) }.isSuccess
    AlertDialog(onDismissRequest = { if (!p.settingsBusy) p.settingsOpen = false },
        title = { Text(stringResource(R.string.terminal_settings)) }, text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.terminal_settings_scope))
                p.settingsMessage?.let { Text(it.text()) }
                if (!p.settingsVerified) OutlinedButton(onClick = onRead, enabled = !p.settingsBusy) { Text(stringResource(R.string.common_refresh)) }
                OutlinedTextField(value.fontFamily, { value = value.copy(fontFamily = it) }, singleLine = true, enabled = !p.settingsBusy,
                    label = { Text(stringResource(R.string.terminal_font_family)) })
                Text(stringResource(R.string.terminal_font_android))
                OutlinedTextField(size, { size = it }, singleLine = true, enabled = !p.settingsBusy,
                    label = { Text(stringResource(R.string.terminal_font_size)) }, isError = size.toDoubleOrNull()?.let { !it.isFinite() || it !in 8.0..40.0 } != false)
                listOf("Campbell", "One Half Dark", "Solarized Dark", "Light").forEach { name ->
                    TextButton(onClick = { value = TerminalSettingsWire.scheme(value, name) }, enabled = !p.settingsBusy) {
                        Text(if (value.colorScheme == name) "$name ✓" else name)
                    }
                }
                listOf(R.string.terminal_background to value.backgroundColor, R.string.terminal_foreground to value.foregroundColor,
                    R.string.terminal_cursor_color to value.cursorColor).forEachIndexed { index, (label, color) ->
                    OutlinedTextField(color, { next -> value = when (index) {
                        0 -> value.copy(backgroundColor = next); 1 -> value.copy(foregroundColor = next); else -> value.copy(cursorColor = next)
                    } }, singleLine = true, enabled = !p.settingsBusy, label = { Text(stringResource(label)) })
                }
            }
        }, confirmButton = { Button(onClick = { onSave(value.copy(fontSize = size.toDouble(), fontFamily = value.fontFamily.trim(),
            backgroundColor = value.backgroundColor.uppercase(), foregroundColor = value.foregroundColor.uppercase(), cursorColor = value.cursorColor.uppercase())) },
            enabled = valid && p.settingsVerified && !p.settingsBusy) { Text(stringResource(R.string.common_save)) } },
        dismissButton = { TextButton(onClick = { p.settingsOpen = false }, enabled = !p.settingsBusy) { Text(stringResource(R.string.common_close)) } })
}
