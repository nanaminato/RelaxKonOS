package app.relaxkonos.mobile.ui.more

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.*

internal class HostSettingsEditor(val owner: SessionState.Active, private val repository: HostSettingsRepository,
    private val current: () -> Boolean, private val scope: CoroutineScope, private val provider: ElevationAnswerProvider) {
    var kind by mutableStateOf(HostSettingKind.Time); private set
    var environmentScope by mutableStateOf(HostEnvironmentScope.HostMachine); private set
    var time by mutableStateOf<HostTimeSettings?>(null); private set
    var identity by mutableStateOf<HostIdentitySettings?>(null); private set
    var environment by mutableStateOf<HostEnvironmentSettings?>(null); private set
    var value by mutableStateOf("")
    var name by mutableStateOf("")
    var delete by mutableStateOf(false)
    var expand by mutableStateOf(false)
    var highImpact by mutableStateOf(false)
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
        HostSettingKind.Environment -> environment != null && HostSettingsRules.mutation(HostEnvironmentMutation(name,delete,if(delete) null else value,expand && !delete),windows) &&
            (!HostSettingsRules.highImpact(name,windows) || highImpact)
    }
    fun lifecycle(active: Boolean) {
        if (closed || active == resumed) return
        resumed = active
        if (active) load(false, false) else {
            generation++; job?.cancel(); busy = false; environment = null; time = null; identity = null; value = ""; name = ""; plan = null
            rollbackReview = null; leaveReview = false; delete = false; expand = false; highImpact = false
        }
    }
    fun close() { lifecycle(false); closed = true }
    fun select(kind: HostSettingKind, scope: HostEnvironmentScope = environmentScope) {
        if (busy || dirty || !resumed) return
        this.kind = kind; environmentScope = scope; time = null; identity = null; environment = null
        value = ""; name = ""; delete = false; expand = false; highImpact = false; plan = null
        load(false,false)
    }
    fun load(reveal: Boolean, authorize: Boolean) {
        if (!resumed || busy || dirty) return
        execute {
            references = repository.references(owner)
            when(kind) {
                HostSettingKind.Time -> consume(repository.time(owner)) { time = it; value = it.zoneId }
                HostSettingKind.Identity -> consume(repository.identity(owner)) { identity = it; value = it.pendingName }
                HostSettingKind.Environment -> consume(repository.environment(owner,environmentScope,reveal,if(authorize) provider else ElevationAnswerProvider.Declines)) { environment = it }
            }
        }
    }
    fun discard() { name = ""; value = when(kind) { HostSettingKind.Time -> time?.zoneId.orEmpty(); HostSettingKind.Identity -> identity?.pendingName.orEmpty(); else -> "" }; delete = false; expand = false; highImpact = false; plan = null }
    fun editVariable(v: HostEnvironmentVariable) { if (!busy) { name = v.name; value = v.rawValue.orEmpty(); delete = false; expand = v.valueKind == "expandString"; highImpact = false } }
    fun preview() {
        if (!canPreview) return
        val selected = kind
        val revision = when(kind) { HostSettingKind.Time -> requireNotNull(time).revision; HostSettingKind.Identity -> requireNotNull(identity).revision; HostSettingKind.Environment -> requireNotNull(environment).revision }
        val target = when(kind) { HostSettingKind.Time -> requireNotNull(time).target; HostSettingKind.Identity -> requireNotNull(identity).target; HostSettingKind.Environment -> requireNotNull(environment).target }
        val mutation = if(kind == HostSettingKind.Environment) HostEnvironmentMutation(name,delete,if(delete) null else value,expand && !delete) else null
        val entered = if(mutation == null) value else null
        val confirmed = highImpact
        execute { consume(repository.preview(owner,selected,revision,target,entered,environmentScope,mutation,confirmed,provider)) { plan = it } }
    }
    fun dismissPlan() { plan = null }
    fun apply() {
        val reviewed = plan ?: return
        if (!resumed || busy) return
        plan = null
        execute(true) {
            try { consume(repository.apply(owner,kind,reviewed,provider)) { operations = operations + (it.id to it) } }
            finally { if (current() && currentCoroutineContext().isActive) { references = repository.references(owner); discard(); environment = null; time = null; identity = null; value = "" } }
        }
    }
    fun query(ref: HostSettingsReference) { if (resumed && !busy) execute { consume(repository.operation(owner,ref)) { operations = operations + (it.id to it) }; references = repository.references(owner) } }
    fun reviewRollback(ref: HostSettingsReference) { val op = operations[ref.id] ?: return; if (!busy && references.none { it.unresolved } && op.state == "applied" && op.observedRevision != null) rollbackReview = ref to op }
    fun dismissRollback() { rollbackReview = null }
    fun rollback() {
        val review = rollbackReview ?: return; rollbackReview = null
        if (!resumed || busy) return
        execute(true) { try { consume(repository.rollback(owner,review.first,review.second,provider)) { operations = operations + (it.id to it) } }
            finally { if(current() && currentCoroutineContext().isActive) { references = repository.references(owner); discard(); time = null; identity = null; environment = null; value = "" } } }
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

private class HostSettingsOwnerKey(private val owner: SessionState.Active) {
    override fun equals(other: Any?) = other is HostSettingsOwnerKey && other.owner === owner
    override fun hashCode() = System.identityHashCode(owner)
}
@Composable
fun HostSettingsScreen(onBack: (() -> Unit)?, modifier: Modifier = Modifier) {
    val container = appContainer(); val owner = container.activeSession ?: return
    val scope = rememberCoroutineScope()
    val editor = remember(HostSettingsOwnerKey(owner),scope) { HostSettingsEditor(owner,container.hostSettings,{container.activeSession === owner},scope,container.elevationAnswers) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(editor,lifecycle) {
        val observer = LifecycleEventObserver { _,_ -> editor.lifecycle(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
        lifecycle.addObserver(observer); editor.lifecycle(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
        onDispose { lifecycle.removeObserver(observer); editor.close() }
    }
    BackHandler(onBack != null) { onBack?.let { editor.leave(it) } }
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),verticalArrangement=Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.host_settings_title), onBack = onBack?.let { { editor.leave(it) } })
        Text(stringResource(R.string.host_settings_scope),style=MaterialTheme.typography.bodySmall)
        Text(owner.serviceId,style=MaterialTheme.typography.bodySmall)
        FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
            HostSettingKind.entries.forEach { kind -> FilterChip(selected=kind == editor.kind,onClick={editor.select(kind)},enabled=!editor.busy&&!editor.dirty,
                label={Text(stringResource(kind.label()))}) }
        }
        editor.error?.let { Text(stringResource(it),color=MaterialTheme.colorScheme.error) }
        if (editor.references.any { it.unresolved }) Text(stringResource(R.string.host_settings_pending),color=MaterialTheme.colorScheme.error)
        if (editor.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        when(editor.kind) {
            HostSettingKind.Time -> editor.time?.let { facts ->
                KeyValueRow(stringResource(R.string.host_settings_current), facts.zoneId)
                KeyValueRow(stringResource(R.string.host_settings_observed), facts.observedAt)
                Text(stringResource(effectLabel(facts.effectiveState)))
                Text(stringResource(R.string.host_settings_zones, facts.zones.size))
                OutlinedTextField(editor.value,{ if(it.length<=256) editor.value=it },label={Text(stringResource(R.string.host_settings_zone))},enabled=!editor.busy,singleLine=true,modifier=Modifier.fillMaxWidth())
                // Filter suggestions rather than placing the remote zone database in a menu.
                facts.zones.filter { it.contains(editor.value,ignoreCase=true) }.take(12).forEach { zone ->
                    TextButton(onClick={editor.value=zone},enabled=!editor.busy) { Text(zone) }
                }
            }
            HostSettingKind.Identity -> editor.identity?.let { facts ->
                KeyValueRow(stringResource(R.string.host_settings_current),facts.name)
                KeyValueRow(stringResource(R.string.host_settings_pending_name),facts.pendingName)
                KeyValueRow(stringResource(R.string.host_settings_observed),facts.observedAt)
                Text(stringResource(effectLabel(facts.effectiveState)))
                OutlinedTextField(editor.value,{ if(it.length<=facts.maximumLength) editor.value=it },label={Text(stringResource(R.string.host_settings_hostname))},enabled=!editor.busy,singleLine=true,modifier=Modifier.fillMaxWidth())
                Text(stringResource(R.string.host_settings_hostname_rule,facts.maximumLength),style=MaterialTheme.typography.bodySmall)
            }
            HostSettingKind.Environment -> {
                if (owner.serverPlatform.equals("windows",true)) FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
                    HostEnvironmentScope.entries.forEach { s -> FilterChip(selected=editor.environmentScope==s,onClick={editor.select(HostSettingKind.Environment,s)},enabled=!editor.busy&&!editor.dirty,
                        label={Text(stringResource(if(s==HostEnvironmentScope.HostMachine) R.string.host_settings_machine else R.string.host_settings_user))}) }
                }
                Text(stringResource(R.string.host_settings_environment_note),style=MaterialTheme.typography.bodySmall)
                FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
                    TextButton(onClick={editor.load(false,true)},enabled=!editor.busy&&!editor.dirty) { Text(stringResource(R.string.host_settings_read)) }
                    TextButton(onClick={editor.load(true,true)},enabled=!editor.busy&&!editor.dirty) { Text(stringResource(R.string.host_settings_reveal)) }
                }
                editor.environment?.let { facts ->
                    Text(facts.target.resourceId,style=MaterialTheme.typography.bodySmall)
                    Text(stringResource(effectLabel(facts.effectiveState)))
                    KeyValueRow(stringResource(R.string.host_settings_observed),facts.observedAt)
                    Text(stringResource(R.string.host_settings_variable_count,facts.variables.size))
                    facts.variables.take(100).forEach { variable ->
                        TextButton(onClick={editor.editVariable(variable)},enabled=!editor.busy) { Text(variable.name) }
                        Text(variable.rawValue ?: stringResource(R.string.host_settings_masked),style=MaterialTheme.typography.bodySmall)
                        variable.expandedPreview?.takeIf { it!=variable.rawValue }?.let { Text(stringResource(R.string.host_settings_expanded,it),style=MaterialTheme.typography.bodySmall) }
                        if(variable.warnings.isNotEmpty()) Text(stringResource(R.string.host_settings_warning),style=MaterialTheme.typography.bodySmall)
                    }
                    if(facts.variables.size>100) Text(stringResource(R.string.host_settings_limit))
                    OutlinedTextField(editor.name,{if(it.length<=255) editor.name=it; editor.highImpact=false},label={Text(stringResource(R.string.host_settings_variable))},singleLine=true,enabled=!editor.busy,modifier=Modifier.fillMaxWidth())
                    Row { Checkbox(editor.delete,{editor.delete=it},enabled=!editor.busy); Text(stringResource(R.string.host_settings_delete)) }
                    if(!editor.delete) {
                        OutlinedTextField(editor.value,{if(it.length<=32767) editor.value=it},label={Text(stringResource(R.string.host_settings_value))},enabled=!editor.busy,maxLines=8,modifier=Modifier.fillMaxWidth())
                        if(owner.serverPlatform.equals("windows",true)) Row { Checkbox(editor.expand,{editor.expand=it},enabled=!editor.busy); Text(stringResource(R.string.host_settings_expand)) }
                    }
                    if(HostSettingsRules.highImpact(editor.name,owner.serverPlatform.equals("windows",true))) Row { Checkbox(editor.highImpact,{editor.highImpact=it},enabled=!editor.busy); Text(stringResource(R.string.host_settings_high_impact)) }
                }
            }
        }
        FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
            TextButton(onClick={editor.load(false,false)},enabled=!editor.busy&&!editor.dirty) { Text(stringResource(R.string.common_refresh)) }
            TextButton(onClick={editor.discard()},enabled=!editor.busy&&editor.dirty) { Text(stringResource(R.string.host_settings_discard)) }
            Button(onClick={editor.preview()},enabled=editor.canPreview) { Text(stringResource(R.string.host_settings_preview)) }
        }
        Text(stringResource(R.string.host_settings_history),style=MaterialTheme.typography.titleMedium)
        editor.references.take(20).forEach { ref ->
            SectionCard(stringResource(ref.kind.label())) {
                Text(ref.id,style=MaterialTheme.typography.bodySmall)
                Text(ref.target.resourceId,style=MaterialTheme.typography.bodySmall)
                val op = editor.operations[ref.id]
                op?.let { Text(stringResource(stateLabel(it.state))); Text(stringResource(effectLabel(it.effectiveState))) }
                FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
                    TextButton(onClick={editor.query(ref)},enabled=!editor.busy) { Text(stringResource(R.string.host_settings_query)) }
                    if(op?.state=="applied"&&op.observedRevision!=null) TextButton(onClick={editor.reviewRollback(ref)},enabled=!editor.busy&&owner.privilegedOperations&&editor.references.none { it.unresolved }) { Text(stringResource(R.string.host_settings_rollback)) }
                }
            }
        }
    }
    editor.plan?.let { plan ->
        SettingsReview(title=stringResource(R.string.host_settings_preview),confirm=stringResource(R.string.host_settings_apply),onDismiss=editor::dismissPlan,onConfirm=editor::apply) {
            Text(plan.target.resourceId); Text(stringResource(effectLabel(plan.effectiveState)))
            Text(stringResource(R.string.host_settings_review_note))
            plan.differences.forEach { diff ->
                Text(if(editor.kind==HostSettingKind.Environment) diff.settingId else stringResource(editor.kind.label()))
                val before = if(diff.before=="[configured]") stringResource(R.string.host_settings_configured) else diff.before ?: "—"
                val after = if(diff.after=="[configured]") stringResource(R.string.host_settings_configured) else diff.after ?: "—"
                Text("$before → $after")
            }
            Text(stringResource(R.string.host_settings_plan_expiry,plan.expiresAt))
        }
    }
    editor.rollbackReview?.let { review -> SettingsReview(stringResource(R.string.host_settings_rollback),stringResource(R.string.host_settings_rollback),editor::dismissRollback,editor::rollback) {
        Text(review.first.id); Text(review.first.target.resourceId); Text(stringResource(R.string.host_settings_rollback_note))
    } }
    if(editor.leaveReview) SettingsReview(stringResource(R.string.host_settings_discard),stringResource(R.string.host_settings_discard),editor::dismissLeave,{editor.discard();editor.dismissLeave();onBack?.invoke()}) { Text(stringResource(R.string.host_settings_leave)) }
}
@Composable
private fun SettingsReview(title:String,confirm:String,onDismiss:()->Unit,onConfirm:()->Unit,content:@Composable ColumnScope.()->Unit) {
    Dialog(onDismissRequest=onDismiss,properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxWidth().fillMaxHeight(0.85f).imePadding().padding(Spacing.md),shape=MaterialTheme.shapes.large) {
            Column(Modifier.padding(Spacing.lg),verticalArrangement=Arrangement.spacedBy(Spacing.md)) {
                Text(title,style=MaterialTheme.typography.titleLarge)
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(Spacing.sm),content=content)
                FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
                    TextButton(onClick=onDismiss) { Text(stringResource(R.string.common_cancel)) }
                    Button(onClick=onConfirm) { Text(confirm) }
                }
            }
        }
    }
}
private fun HostSettingKind.label() = when(this) { HostSettingKind.Time -> R.string.host_settings_time; HostSettingKind.Identity -> R.string.host_settings_identity; HostSettingKind.Environment -> R.string.host_settings_environment }
private fun effectLabel(value:String) = when(value) { "immediate" -> R.string.host_settings_immediate; "newProcess" -> R.string.host_settings_new_process; "newLogin" -> R.string.host_settings_new_login; "serviceRestart" -> R.string.host_settings_service_restart; "hostRestart" -> R.string.host_settings_host_restart; else -> R.string.host_settings_unknown }
private fun stateLabel(value:String) = when(value) { "prepared" -> R.string.host_settings_prepared; "applying" -> R.string.host_settings_applying; "applied" -> R.string.host_settings_applied; "failed" -> R.string.host_settings_failed; "partiallyApplied" -> R.string.host_settings_partial; "unknown" -> R.string.host_settings_unknown; "awaitingConfirmation" -> R.string.host_settings_awaiting; "rolledBack" -> R.string.host_settings_rolled_back; "recoveryRequired" -> R.string.host_settings_recovery; else -> R.string.host_settings_unknown }
