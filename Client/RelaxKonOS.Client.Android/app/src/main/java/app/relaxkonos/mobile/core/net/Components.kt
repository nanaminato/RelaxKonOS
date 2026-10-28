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
    private val ownershipValues = setOf("unknown", "absent", "external", "managed")
    private val modeValues = setOf("managedArchive", "systemPackage")

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
                ownership = stateValue.getString("ownership").also {
                    require(it in ownershipValues) { "Unknown component ownership" }
                },
                mode = stateValue.getString("mode").also {
                    require(it in modeValues) { "Unknown component mode" }
                },
                target = listOf("os", "architecture", "serviceHost").joinToString(" / ") { target.getString(it) },
                activeVersion = stateValue.optNullableString("activeVersion"),
                recoveryRequired = stateValue.getBoolean("recoveryRequired"),
            )
        }

        val modeArray = value.optJSONArray("modes") ?: JSONArray()
        val modes = (0 until modeArray.length()).map { index ->
            val mode = modeArray.getJSONObject(index)
            ComponentModeSupport(
                mode = mode.getString("mode").also { require(it in modeValues) { "Unknown component mode support" } },
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
}
