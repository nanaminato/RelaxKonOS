package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/**
 * Host component state, mirrored from `RelaxKonOS.Protocol.ManagedRuntimes.ComponentLifecycleContracts`.
 *
 * The component center reads one normalized state per component, freezes the change the user chose and
 * submits it through the owning domain's installation ledger. It never invents a package, version,
 * download URL or executable path: a package change freezes the host's own candidate and an archive
 * change names one of the host's pinned releases.
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

/** One trusted pinned archive the host offers for this component and platform. */
data class ComponentPinnedArchive(
    val releaseId: String,
    val version: String,
    val rid: String,
    val sourceUri: String,
    val sha256: String,
    val format: String,
    val maximumBytes: Long,
    val affectedComponents: List<String>,
)

/**
 * Either the state or the reasons it is unavailable. [modes] is present even without a state, so the
 * screen can distinguish "this platform does not support the component" from "this build cannot read
 * it"; [problems] carries the stable `component.*` codes. [pinnedArchives] is the host's own trusted
 * list for an archive component: the client never invents a version or a download location.
 */
data class ComponentStateView(
    val componentId: String,
    val state: ComponentState?,
    val modes: List<ComponentModeSupport>,
    val problems: List<String>,
    val pendingOperationId: String?,
    val pinnedArchives: List<ComponentPinnedArchive> = emptyList(),
    val pinRevision: Long = 0,
    val committedOperationId: String? = null,
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
        "nativeinstaller" -> "nativeInstaller"
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

        val pinArray = value.optJSONArray("pinnedArchives") ?: JSONArray()
        val pins = (0 until pinArray.length()).map { index ->
            val pin = pinArray.getJSONObject(index)
            val releaseId = pin.getString("releaseId")
            val version = pin.getString("version")
            val sha256 = pin.getString("sha256")
            val sourceUri = pin.getString("sourceUri")
            require(releaseId.isNotBlank() && version.isNotBlank()) { "Pinned archive identity is incomplete" }
            require(sha256.length == 64 && sha256.all { it in "0123456789abcdefABCDEF" }) { "Pinned archive digest is invalid" }
            require(sourceUri.startsWith("https://")) { "Pinned archive source is not HTTPS" }
            val affected = pin.optJSONArray("affectedComponents") ?: JSONArray()
            ComponentPinnedArchive(
                releaseId = releaseId,
                version = version,
                rid = pin.getString("rid"),
                sourceUri = sourceUri,
                sha256 = sha256.lowercase(),
                format = pin.getString("format"),
                maximumBytes = pin.optLong("maximumBytes", 0),
                affectedComponents = (0 until affected.length()).map { affected.getString(it) },
            )
        }
        // A pinned bundle may serve several components, but it may never be applied to one it does not name.
        require(pins.all { expectedComponentId in it.affectedComponents }) {
            "Pinned archive does not apply to this component"
        }

        return ComponentStateView(
            componentId = expectedComponentId,
            state = state,
            modes = modes,
            problems = problems,
            pendingOperationId = value.optNullableString("pendingOperationId"),
            pinnedArchives = pins,
            pinRevision = value.optLong("pinRevision", 0),
            committedOperationId = value.optNullableString("committedOperationId"),
        )
    }

    /**
     * Builds the freeze request from the state the user was shown. Package components freeze the
     * host's own candidate, so no source/release id is invented on the client; an archive component
     * names one of the host's pinned releases and the pin revision it was read from. The expected
     * revision makes a concurrent host change a conflict instead of a silently different plan.
     */
    fun planBody(componentId: String, change: ComponentChange, state: ComponentState,
        keepData: Boolean, idempotencyKey: String, pin: ComponentPinnedArchive? = null,
        pinRevision: Long = 0): JsonBody = JsonBody()
        .string("componentId", componentId)
        .string("hostId", state.hostId)
        .string("change", change.wire)
        .raw("expectedRevision", state.revision.toString())
        .string("mode", when (state.mode) {
            "systemPackage" -> "SystemPackage"
            "managedArchive" -> "ManagedArchive"
            "nativeInstaller" -> "NativeInstaller"
            else -> throw IllegalArgumentException("Unknown component mode")
        })
        .string("sourceId", pin?.let { "host-pins" })
        .raw("expectedSourceRevision", (if (pin != null) pinRevision else 0).toString())
        .raw("expectedTrustRevision", "0")
        .string("releaseId", pin?.releaseId)
        .string("requestedVersion", pin?.version)
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
            if (componentMode(request.getString("mode")) == "nativeInstaller" && target != null) {
                require(expectedComponentId == "tailscale" && componentMode(target.getString("mode")) == "nativeInstaller")
                require(target.optJSONObject("archive") == null && target.optJSONObject("package") == null && target.optJSONObject("pin") == null)
                val installer = target.getJSONObject("installer")
                val version = installer.getString("version")
                val arch = when (installer.getString("rid")) { "win-x64" -> "amd64"; "win-arm64" -> "arm64"; else -> error("Unsupported MSI architecture") }
                require(version.matches(Regex("[0-9]+\\.[0-9]+\\.[0-9]+")) && installer.getString("format") == "msi")
                val file = "tailscale-setup-$version-$arch.msi"
                require(installer.getString("fileName") == file && installer.getString("downloadUrl") == "https://pkgs.tailscale.com/stable/$file")
                require(installer.getString("sha256").matches(Regex("[a-fA-F0-9]{64}")))
            }
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
    private val artifact: JSONObject? get() = targetJson?.let(::JSONObject)?.let {
        it.optJSONObject("installer") ?: it.optJSONObject("package") ?: it.optJSONObject("pin")
    }
    val version: String? get() = artifact?.optNullableString("version")
    val source: String? get() = artifact?.let { it.optNullableString("downloadUrl") ?: it.optNullableString("sourceUri") ?: it.optNullableString("baseUri") }
    val sha256: String? get() = artifact?.optNullableString("sha256")
}

