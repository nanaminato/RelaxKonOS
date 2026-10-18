package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.servercenter.SshTerminalUiState

internal data class SshTerminalReviewOwner(val hostId: String, val sessionId: String, val revision: Long, val draft: String) {
    constructor(state: SshTerminalUiState) : this(state.hostId, state.sessionId, state.inputRevision, state.draft)
    fun matches(state: SshTerminalUiState?) = state != null && sessionId.isNotEmpty() &&
        state.hostId == hostId && state.sessionId == sessionId && state.inputRevision == revision && state.draft == draft
}
internal class SshPasteReview(val owner: SshTerminalReviewOwner, val payload: String)
internal class SshEndReview(val owner: SshTerminalReviewOwner, val number: Int, val hostLabel: String)

internal fun sshTerminalLinePayload(input: String, ctrl: Boolean, alt: Boolean): String {
    val value = if (ctrl && input.length == 1) (input[0].uppercaseChar().code and 0x1f).toChar().toString()
        else if (ctrl || alt) input else input + "\r"
    return if (alt) "\u001b$value" else value
}
internal fun sshTerminalNeedsReview(input: String) = input.any { it == '\r' || it == '\n' }
