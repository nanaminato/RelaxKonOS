package app.relaxkonos.mobile.ui.manage.scripts

/** In-memory form data; approval passwords never belong to a retained draft. */
data class ScriptDraft(
    val runAs: String,
    val executable: String = "",
    val arguments: String = "",
    val directory: String = "",
    val environment: String = "",
    val timeout: String = "300",
    val adminName: String = "",
) {
    fun dirty(account: String) = this != ScriptDraft(account)
}
