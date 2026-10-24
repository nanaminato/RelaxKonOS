package app.relaxkonos.mobile.ui.manage.scripts

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.ui.manage.guardian.guardianApprovalRequired

/** In-memory form data; approval passwords never belong to a retained draft. */
data class ScriptDraft(
    val runAs: String,
    val executable: String = "",
    val arguments: List<String> = emptyList(),
    val directory: String = "",
    val environment: String = "",
    val timeout: String = "300",
    val adminName: String = "",
) {
    fun dirty(account: String) = this != ScriptDraft(account)
    val validArguments get() = arguments.size <= 64 && arguments.all { it.length <= 4096 && '\u0000' !in it }
    val validPaths get() = executable.isNotBlank() && directory.isNotBlank() && '\u0000' !in executable && '\u0000' !in directory
    val validRunAs get() = '\u0000' !in runAs && !runAs.trim().startsWith('-')
    fun executionAccount(account: String) = runAs.trim().ifBlank { account.trim() }
    fun requiresApproval(owner: SessionState.Active) = guardianApprovalRequired(owner, executionAccount(owner.userName))
}
