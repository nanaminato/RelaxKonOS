package app.relaxkonos.mobile.ui.more

import androidx.compose.runtime.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.ui.common.*
import kotlinx.coroutines.*
internal class HostSettingsEditor(val owner: SessionState.Active, private val repository: HostSettingsRepository,
    private val current: () -> Boolean, private val scope: CoroutineScope, private val provider: ElevationAnswerProvider) {
    var kind by mutableStateOf(HostSettingKind.Time); private set
    var environmentScope by mutableStateOf(HostEnvironmentScope.HostMachine); private set
    var time by mutableStateOf<HostTimeSettings?>(null); private set
    var identity by mutableStateOf<HostIdentitySettings?>(null); private set
    var environment by mutableStateOf<HostEnvironmentSettings?>(null); private set
    var windowsEnvironments by mutableStateOf<Map<HostEnvironmentScope, HostEnvironmentSettings>>(emptyMap()); private set
    var environmentAuthorizationScopes by mutableStateOf<Set<HostEnvironmentScope>>(emptySet()); private set
    var environmentEditing by mutableStateOf(false); private set
    var existingVariable by mutableStateOf(false); private set
    var environmentAuthorizationRequired by mutableStateOf(false); private set
    var value by mutableStateOf("")
    var name by mutableStateOf("")
    var delete by mutableStateOf(false)
    var expand by mutableStateOf(false)
    var busy by mutableStateOf(false); private set
    var resumed by mutableStateOf(false); private set
    var error by mutableStateOf<Int?>(null); private set
    var plan by mutableStateOf<HostSettingsPlan?>(null); private set
    var references by mutableStateOf<List<HostSettingsReference>>(emptyList()); private set
    var operations by mutableStateOf<Map<String, HostSettingsOperation>>(emptyMap()); private set
    var rollbackReview by mutableStateOf<Pair<HostSettingsReference, HostSettingsOperation>?>(null); private set
    var leaveReview by mutableStateOf(false); private set
    private var job: Job? = null
    private var generation = 0
    private var closed = false
    private var writeInFlight = false
    private val windows get() = owner.serverPlatform.equals("windows", true)
    val dirty get() = if (kind == HostSettingKind.Environment) name.isNotEmpty() || value.isNotEmpty() else
        value != when(kind) { HostSettingKind.Time -> time?.zoneId.orEmpty(); HostSettingKind.Identity -> identity?.pendingName.orEmpty(); else -> "" }
    val canPreview get() = resumed && !busy && owner.privilegedOperations && references.none { it.unresolved } && when(kind) {
        HostSettingKind.Time -> time?.let { value in it.zones && value != it.zoneId } == true
        HostSettingKind.Identity -> identity?.let { HostSettingsRules.hostname(value,it.maximumLength) && value != it.pendingName } == true
        HostSettingKind.Environment -> environment != null && HostSettingsRules.mutation(HostEnvironmentMutation(name,delete,if(delete) null else value,expand && !delete),windows)
    }
    fun lifecycle(active: Boolean) {
        if (closed || active == resumed) return
        resumed = active
        if (active) load() else {
            generation++; job?.cancel(); busy = false; windowsEnvironments = emptyMap(); environmentAuthorizationScopes = emptySet(); environmentEditing = false; environment = null; time = null; identity = null; value = ""; name = ""; plan = null
            rollbackReview = null; leaveReview = false; delete = false; expand = false
        }
    }
    fun close() { lifecycle(false); closed = true }
    fun select(kind: HostSettingKind, scope: HostEnvironmentScope = environmentScope) {
        if (busy || dirty || !resumed) return
        this.kind = kind; environmentScope = scope; time = null; identity = null; environment = null
        value = ""; name = ""; delete = false; expand = false; plan = null
        load()
    }
    fun load() {
        if (!resumed || busy || dirty) return
        execute { references = repository.references(owner); reloadFacts() }
    }
    /**
     * Read the facts of the selected kind back from the host.
     *
     * [load] cannot be reused right after a write: it refuses while the editor is busy or dirty, and both
     * are still true at that moment — the write is the request in flight and the field still holds the
     * submitted value. Dropping the facts instead, which is what this used to do, left the page with
     * nothing to show until the user pressed refresh, so an applied change looked like no change at all.
     */
    private suspend fun reloadFacts() {
        when(kind) {
            HostSettingKind.Time -> consume(repository.time(owner)) { time = it; value = it.zoneId }
            HostSettingKind.Identity -> consume(repository.identity(owner)) { identity = it; value = it.pendingName }
            HostSettingKind.Environment -> loadEnvironment()
        }
    }
    private suspend fun loadEnvironment() {
        environment = null
        environmentAuthorizationRequired = false
        windowsEnvironments = emptyMap()
        environmentAuthorizationScopes = emptySet()
        val scopes = if (windows) listOf(HostEnvironmentScope.HostUser, HostEnvironmentScope.HostMachine) else listOf(environmentScope)
        for (targetScope in scopes) {
            val result = repository.environment(owner,targetScope,provider)
            if (result is ApiResult.Problem && (result.status in setOf(401,403) ||
                    result.code in setOf(ProblemCodes.ELEVATION_REQUIRED,"settings.elevation_required","settings.environment.authorization_required"))) {
                if (current() && resumed) {
                    environmentAuthorizationScopes = environmentAuthorizationScopes + targetScope
                    environmentAuthorizationRequired = true
                }
            } else consume(result) {
                windowsEnvironments = windowsEnvironments + (targetScope to it)
                if (targetScope == environmentScope) environment = it
            }
        }
    }
    fun beginEnvironmentEdit(scope: HostEnvironmentScope, variable: HostEnvironmentVariable?, removing: Boolean = false) {
        if (busy || dirty || !resumed) return
        val facts = windowsEnvironments[scope] ?: return
        environmentScope = scope; environment = facts
        name = variable?.name.orEmpty(); value = variable?.rawValue.orEmpty()
        expand = variable?.valueKind == "expandString"; delete = removing
        existingVariable = variable != null; environmentEditing = true
    }
    fun discard() { environmentEditing = false; existingVariable = false; name = ""; value = when(kind) { HostSettingKind.Time -> time?.zoneId.orEmpty(); HostSettingKind.Identity -> identity?.pendingName.orEmpty(); else -> "" }; delete = false; expand = false; plan = null }
    fun preview() {
        if (!canPreview) return
        val selected = kind
        val revision = when(kind) { HostSettingKind.Time -> requireNotNull(time).revision; HostSettingKind.Identity -> requireNotNull(identity).revision; HostSettingKind.Environment -> requireNotNull(environment).revision }
        val target = when(kind) { HostSettingKind.Time -> requireNotNull(time).target; HostSettingKind.Identity -> requireNotNull(identity).target; HostSettingKind.Environment -> requireNotNull(environment).target }
        val mutation = if(kind == HostSettingKind.Environment) HostEnvironmentMutation(name,delete,if(delete) null else value,expand && !delete) else null
        val entered = if(mutation == null) value else null
        // Opening the preview leads to the explicit apply confirmation; no extra checkbox.
        val confirmed = mutation != null && HostSettingsRules.highImpact(mutation.name,windows)
        execute { consume(repository.preview(owner,selected,revision,target,entered,environmentScope,mutation,confirmed,provider)) { plan = it } }
    }
    fun dismissPlan() { plan = null }
    fun apply() {
        val reviewed = plan ?: return
        if (!resumed || busy) return
        plan = null
        execute(true) {
            try { consume(repository.apply(owner,kind,reviewed,provider)) { operations = operations + (it.id to it); writeInFlight = false } }
            // The host is read back before the busy flag drops, so an applied change is visible on this
            // page immediately instead of after a manual refresh. The write itself is over, so a failure
            // here is a read problem and must not be reported as an unknown write outcome.
            finally { if (current() && currentCoroutineContext().isActive) {
                references = repository.references(owner); environment = null; time = null; identity = null; discard(); reloadFacts()
            } }
        }
    }
    fun query(ref: HostSettingsReference) { if (resumed && !busy) execute { consume(repository.operation(owner,ref)) { operations = operations + (it.id to it) }; references = repository.references(owner) } }
    fun reviewRollback(ref: HostSettingsReference) { val op = operations[ref.id] ?: return; if (!busy && references.none { it.unresolved } && op.state == "applied" && op.observedRevision != null) rollbackReview = ref to op }
    fun dismissRollback() { rollbackReview = null }
    fun rollback() {
        val review = rollbackReview ?: return; rollbackReview = null
        if (!resumed || busy) return
        execute(true) { try { consume(repository.rollback(owner,review.first,review.second,provider)) { operations = operations + (it.id to it); writeInFlight = false } }
            finally { if(current() && currentCoroutineContext().isActive) {
                references = repository.references(owner); time = null; identity = null; environment = null; discard(); reloadFacts()
            } } }
    }
    fun leave(onBack: () -> Unit) { if (!busy) { if(dirty) leaveReview = true else onBack() } }
    fun dismissLeave() { leaveReview = false }
    private suspend fun <T> consume(result: ApiResult<T>, action: (T)->Unit) {
        currentCoroutineContext().ensureActive()
        if (!current() || !resumed) return
        when(result) {
            is ApiResult.Success -> action(result.value)
            is ApiResult.Transport -> error = if(writeInFlight) R.string.host_settings_unknown else R.string.error_connectivity
            is ApiResult.Problem -> error = when {
                result.code in setOf(ProblemCodes.ELEVATION_REQUIRED,"settings.elevation_required","settings.environment.authorization_required") -> R.string.error_elevation_required
                result.code == "settings.revision_conflict" -> R.string.host_settings_conflict
                result.code == "settings.plan_expired" -> R.string.host_settings_expired
                result.code.contains("helper") || result.code.contains("unsupported") -> R.string.host_settings_unavailable
                else -> R.string.error_generic
            }
        }
    }
    private fun execute(write: Boolean = false, action: suspend () -> Unit) {
        if (closed || !resumed || busy || !current()) return
        busy = true; writeInFlight = write; error = null; val version = ++generation
        job = scope.launch {
            try { action() }
            catch(cancelled: CancellationException) { throw cancelled }
            catch(_: Exception) { if (current() && version == generation) error = if(write) R.string.host_settings_unknown else R.string.error_generic }
            finally { if (current() && version == generation) busy = false }
        }
    }
}
