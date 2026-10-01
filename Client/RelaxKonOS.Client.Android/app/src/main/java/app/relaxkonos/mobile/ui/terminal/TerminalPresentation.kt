package app.relaxkonos.mobile.ui.terminal

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.TerminalSettings

data class TerminalPasteReview(val sessionId: String, val payload: String, val clearDraft: Boolean)
data class TerminalMatch(val start: Int, val end: Int)

/** Presentation-only in-memory state. Drafts are never commands until an explicit send is accepted. */
class TerminalPresentation {
    private var owner: SessionState.Active? = null
    private var session: String? = null
    private val drafts = linkedMapOf<String, String>()
    var input by mutableStateOf("")
        private set
    var ctrlNext by mutableStateOf(false)
    var altNext by mutableStateOf(false)
    var pasteReview by mutableStateOf<TerminalPasteReview?>(null)
    var searchOpen by mutableStateOf(false)
    var search by mutableStateOf("")
    var searchIndex by mutableStateOf(0)
    var followOutput by mutableStateOf(true)
    var settings by mutableStateOf(TerminalSettings.Default)
    var localFontSize by mutableStateOf<Double?>(null)
    var settingsOpen by mutableStateOf(false)
    var settingsVerified by mutableStateOf(false)
    var settingsBusy by mutableStateOf(false)
    var settingsMessage by mutableStateOf<app.relaxkonos.mobile.ui.common.UiMessage?>(null)
    var clipboardFailed by mutableStateOf(false)

    fun bindOwner(active: SessionState.Active?) {
        if (owner === active) return
        owner = active; session = null; drafts.clear(); input = ""
        ctrlNext = false; altNext = false; pasteReview = null
        search = ""; searchOpen = false; searchIndex = 0; followOutput = true
        localFontSize = null; settings = TerminalSettings.Default; settingsOpen = false; settingsVerified = false
        settingsBusy = false; settingsMessage = null; clipboardFailed = false
    }
    fun matchesOwner(active: SessionState.Active) = owner === active
    fun bindSession(id: String?) {
        if (session == id) return
        session?.let { drafts[it] = input }
        while (drafts.size > 32) drafts.remove(drafts.keys.first())
        session = id; input = drafts[id].orEmpty()
        ctrlNext = false; altNext = false; pasteReview = null; followOutput = true; searchIndex = 0
    }
    fun edit(value: String) { input = TerminalInputPolicy.boundedText(value, MAX_INPUT) }
    fun payload(): String = TerminalInputPolicy.payload(input, ctrlNext, altNext)
    fun preparePaste(value: String): Boolean {
        val id = session ?: return false
        if (value.isEmpty() || value.length > MAX_INPUT) return false
        pasteReview = TerminalPasteReview(id, value, false); return true
    }
    fun prepareDraftReview(): Boolean {
        val id = session ?: return false
        pasteReview = TerminalPasteReview(id, payload(), true); return true
    }
    fun acceptedPaste() { if (pasteReview?.clearDraft == true) sent() else pasteReview = null }
    fun canPaste(id: String?) = pasteReview?.sessionId == id && id != null
    fun sent() { input = ""; session?.let { drafts.remove(it) }; ctrlNext = false; altNext = false; pasteReview = null }
    companion object { const val MAX_INPUT = 16_384 }
}

object TerminalInputPolicy {
    fun boundedText(value: String, maximum: Int): String {
        val end = value.length.coerceAtMost(maximum)
        return value.substring(0, if (end > 0 && end < value.length && value[end - 1].isHighSurrogate() && value[end].isLowSurrogate()) end - 1 else end)
    }
    fun payload(text: String, ctrl: Boolean, alt: Boolean): String {
        val payload = if (ctrl && text == " ") "\u0000" else if (ctrl && text == "?") "\u007f"
            else if (ctrl && text.length == 1 && text[0] in '@'..'_') ((text[0].code) and 0x1f).toChar().toString()
            else if (ctrl && text.length == 1 && text[0] in 'a'..'z') ((text[0].uppercaseChar().code) and 0x1f).toChar().toString()
            else if (ctrl || alt) text else text + "\r"
        return if (alt) "\u001b$payload" else payload
    }
    fun needsReview(text: String): Boolean = text.any { it == '\n' || it == '\r' || it == '\u001b' || it == '\u0000' }
    fun matches(output: String, query: String): List<TerminalMatch> {
        if (query.isEmpty()) return emptyList()
        val results = mutableListOf<TerminalMatch>(); var at = 0
        while (at < output.length && results.size < 5000) {
            val start = output.indexOf(query, at, ignoreCase = true)
            if (start < 0) break
            results += TerminalMatch(start, start + query.length); at = start + query.length
        }
        return results
    }
}
