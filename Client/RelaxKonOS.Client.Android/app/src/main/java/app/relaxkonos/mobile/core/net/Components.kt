package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/**
 * Host component state, mirrored from `RelaxKonOS.Protocol.ManagedRuntimes.ComponentLifecycleContracts`.
 *
 * The component center is an observation surface on Android: it reads one normalized state per
 * component and never freezes a plan or executes a change. Installation, update, repair, rollback and
 * uninstall remain in the desktop/server component center, where the frozen plan and recovery ledger
 * are visible.
 */
object ComponentRoutes {
    const val ROOT = "/api/v1.0/components"
    private val idRule = Regex("[a-z0-9][a-z0-9-]{1,39}")
    fun state(componentId: String): String {
        require(idRule.matches(componentId)) { "Invalid component id" }
        return "$ROOT/$componentId/state"
    }

    fun plan(componentId: String): String {
        require(idRule.matches(componentId)) { "Invalid component id" }
        return "$ROOT/$componentId/plan"
    }
}

/** The three changes the Android component center can freeze; repair/rollback stay in the runtime pages. */
enum class ComponentChange(val wire: String) {
    Install("Install"),
    Update("Update"),
    Uninstall("Uninstall"),
}

/** One declared install mode for this host, even when the state itself cannot be read. */
data class ComponentModeSupport(
    val mode: String,
    val platform: String,
    val available: Boolean,
    val problemCode: String?,
)

/** The normalized lifecycle state one adapter reported. */
data class ComponentState(
    val componentId: String,
    val hostId: String,
    val revision: Long,
    val ownership: String,
    val mode: String,
    val target: String,
    val activeVersion: String?,
    val recoveryRequired: Boolean,
)

/**
 * Either the state or the reasons it is unavailable. [modes] is present even without a state, so the
 * screen can distinguish "this platform does not support the component" from "this build cannot read
 * it"; [problems] carries the stable `component.*` codes.
 */
data class ComponentStateView(
    val componentId: String,
    val state: ComponentState?,
    val modes: List<ComponentModeSupport>,
    val problems: List<String>,
    val pendingOperationId: String?,
) {
    val recoveryRequired: Boolean get() = state?.recoveryRequired == true || pendingOperationId != null

    val version: String? get() = state?.activeVersion

    val ownership: String? get() = state?.ownership

    val unsupported: Boolean get() = state == null && modes.isNotEmpty() && modes.none { it.available }

    val installed: Boolean get() = ownership == "managed" || ownership == "external"
}

object ComponentWire {
    /** Server enums use JsonStringEnumConverter without a naming policy: "Managed", "SystemPackage". */
    private fun componentOwnership(raw: String): String = when (raw.lowercase()) {
        "unknown", "absent", "external", "managed" -> raw.lowercase()
        else -> throw IllegalArgumentException("Unknown component ownership")
    }

    private fun componentMode(raw: String): String = when (raw.lowercase()) {
        "systempackage" -> "systemPackage"
        "managedarchive" -> "managedArchive"
        else -> throw IllegalArgumentException("Unknown component mode")
    }

    fun stateView(payload: String, expectedComponentId: String): ComponentStateView {
        val value = JSONObject(payload)
        require(value.getString("componentId") == expectedComponentId) { "Component identity changed" }

        val state = if (value.isNull("state")) null else {
            val stateValue = value.getJSONObject("state")
            require(stateValue.getString("componentId") == expectedComponentId) { "Component state identity changed" }
            val target = stateValue.getJSONObject("target")
            ComponentState(
                componentId = expectedComponentId,
                hostId = stateValue.getString("hostId"),
                revision = stateValue.getLong("revision"),
                ownership = componentOwnership(stateValue.getString("ownership")),
                mode = componentMode(stateValue.getString("mode")),
                target = listOf("os", "architecture", "serviceHost").joinToString(" / ") { target.getString(it) },
                activeVersion = stateValue.optNullableString("activeVersion"),
                recoveryRequired = stateValue.getBoolean("recoveryRequired"),
            )
        }

        val modeArray = value.optJSONArray("modes") ?: JSONArray()
        val modes = (0 until modeArray.length()).map { index ->
            val mode = modeArray.getJSONObject(index)
            ComponentModeSupport(
                mode = componentMode(mode.getString("mode")),
                platform = mode.getString("platform"),
                available = mode.getBoolean("available"),
                problemCode = mode.optNullableString("problemCode"),
            )
        }

        val problemArray = value.optJSONArray("problems") ?: JSONArray()
        val problems = (0 until problemArray.length()).map { problemArray.getString(it) }

        return ComponentStateView(
            componentId = expectedComponentId,
            state = state,
            modes = modes,
            problems = problems,
            pendingOperationId = value.optNullableString("pendingOperationId"),
        )
    }

    /**
     * Builds the freeze request from the state the user was shown. Package components freeze the
     * host's own candidate, so no source/release id is invented on the client; the expected revision
     * makes a concurrent host change a conflict instead of a silently different plan.
     */
    fun planBody(componentId: String, change: ComponentChange, state: ComponentState,
        keepData: Boolean, idempotencyKey: String): JsonBody = JsonBody()
        .string("componentId", componentId)
        .string("hostId", state.hostId)
        .string("change", change.wire)
        .raw("expectedRevision", state.revision.toString())
        .string("mode", if (state.mode == "systemPackage") "SystemPackage" else "ManagedArchive")
        .raw("sourceId", "null")
        .raw("expectedSourceRevision", "0")
        .raw("expectedTrustRevision", "0")
        .raw("releaseId", "null")
        .raw("requestedVersion", "null")
        .bool("keepData", keepData)
        .string("idempotencyKey", idempotencyKey)

    /**
     * A frozen proposal. `request` and `target` are kept as the host returned them and are submitted
     * back inside [ComponentFrozenInstallationRequest]; [problems] is empty only when the change is
     * allowed.
     */
    fun plan(payload: String, expectedComponentId: String, expectedChange: ComponentChange): ComponentPlanResult {
        val value = JSONObject(payload)
        val request = if (value.isNull("request")) null else value.getJSONObject("request")
        val target = if (value.isNull("target")) null else value.getJSONObject("target")
        if (request != null) {
            require(request.getString("componentId") == expectedComponentId) { "Planned component changed" }
            require(request.getString("change").equals(expectedChange.wire, ignoreCase = true)) { "Planned change changed" }
        }
        val problemArray = value.optJSONArray("problems") ?: JSONArray()
        val problems = (0 until problemArray.length()).map { problemArray.getString(it) }
        val noteArray = value.optJSONArray("notes") ?: JSONArray()
        val notes = (0 until noteArray.length()).map { noteArray.getString(it) }
        return ComponentPlanResult(
            componentId = expectedComponentId,
            change = expectedChange,
            problems = problems,
            notes = notes,
            requestJson = request?.toString(),
            targetJson = target?.toString(),
        )
    }
}

/** One frozen component change; submission goes through the owning domain's installation ledger. */
data class ComponentPlanResult(
    val componentId: String,
    val change: ComponentChange,
    val problems: List<String>,
    val notes: List<String>,
    val requestJson: String?,
    val targetJson: String?,
) {
    val allowed: Boolean get() = problems.isEmpty() && requestJson != null && targetJson != null
}

