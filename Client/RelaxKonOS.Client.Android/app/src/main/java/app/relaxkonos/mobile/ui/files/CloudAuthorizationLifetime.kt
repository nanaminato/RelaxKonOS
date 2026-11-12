package app.relaxkonos.mobile.ui.files

/** A callback belongs to one visible dialog, login identity and provider session, never its successor. */
internal class CloudAuthorizationLifetime(private val identity: Any) {
    private var active = true
    private var sessionId: String? = null
    fun live(currentIdentity: Any) = active && identity == currentIdentity
    fun bind(id: String) { check(active); sessionId = id }
    fun matches(currentIdentity: Any, id: String) = live(currentIdentity) && sessionId == id
    fun close() { active = false; sessionId = null }
}
