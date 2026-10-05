package app.relaxkonos.mobile.ui.more

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
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
        execute {
            references = repository.references(owner)
            when(kind) {
                HostSettingKind.Time -> consume(repository.time(owner)) { time = it; value = it.zoneId }
                HostSettingKind.Identity -> consume(repository.identity(owner)) { identity = it; value = it.pendingName }
                HostSettingKind.Environment -> loadEnvironment()

            }
        }
    }
    private suspend fun loadEnvironment() {
        environment = null
        environmentAuthorizationRequired = false
        windowsEnvironments = emptyMap()
        environmentAuthorizationScopes = emptySet()
        val scopes = if (windows) listOf(HostEnvironmentScope.HostUser, HostEnvironmentScope.HostMachine) else listOf(environmentScope)
        for (targetScope in scopes) {
            val result = repository.environment(owner,targetScope,true,provider)
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
            try { consume(repository.apply(owner,kind,reviewed,provider)) { operations = operations + (it.id to it) } }
            finally { if (current() && currentCoroutineContext().isActive) { references = repository.references(owner); discard(); environment = null; time = null; identity = null; value = ""; if (kind == HostSettingKind.Environment) loadEnvironment() } }
        }
    }
    fun query(ref: HostSettingsReference) { if (resumed && !busy) execute { consume(repository.operation(owner,ref)) { operations = operations + (it.id to it) }; references = repository.references(owner) } }
    fun reviewRollback(ref: HostSettingsReference) { val op = operations[ref.id] ?: return; if (!busy && references.none { it.unresolved } && op.state == "applied" && op.observedRevision != null) rollbackReview = ref to op }
    fun dismissRollback() { rollbackReview = null }
    fun rollback() {
        val review = rollbackReview ?: return; rollbackReview = null
        if (!resumed || busy) return
        execute(true) { try { consume(repository.rollback(owner,review.first,review.second,provider)) { operations = operations + (it.id to it) } }
            finally { if(current() && currentCoroutineContext().isActive) { references = repository.references(owner); discard(); time = null; identity = null; environment = null; value = ""; if (kind == HostSettingKind.Environment) loadEnvironment() } } }
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
fun HostSettingsScreen(onBack: (() -> Unit)?, onOpenServerHttps: () -> Unit = {}, modifier: Modifier = Modifier) {
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
        if (ServerCapabilities.CERTIFICATES in owner.capabilities) {
            OutlinedButton(onClick = { editor.leave(onOpenServerHttps) }, enabled = !editor.busy) { Text(stringResource(R.string.server_https_title)) }
        }
        FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
            HostSettingKind.entries.forEach { kind -> FilterChip(selected=kind == editor.kind,onClick={editor.select(kind)},enabled=!editor.busy&&!editor.dirty,
                label={Text(stringResource(kind.label()))}) }
        }
        OperationMessageDialog(editor.error?.takeUnless { editor.busy }?.let { stringResource(it) })
        if (editor.references.any { it.unresolved }) Text(stringResource(R.string.host_settings_pending),color=MaterialTheme.colorScheme.error)
        RefreshProgressIndicator(visible = editor.busy)
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
                Text(stringResource(R.string.host_settings_environment_note),style=MaterialTheme.typography.bodySmall)
                if (!owner.serverPlatform.equals("windows",true) && editor.environmentAuthorizationRequired && !editor.busy) {
                    Text(stringResource(R.string.host_settings_environment_authorization_required),color=MaterialTheme.colorScheme.error)
                    Button(onClick={editor.load()},enabled=!editor.dirty) { Text(stringResource(R.string.host_settings_environment_retry)) }
                }
                if (owner.serverPlatform.equals("windows",true)) {
                    listOf(HostEnvironmentScope.HostUser, HostEnvironmentScope.HostMachine).forEach { targetScope ->
                        WindowsEnvironmentGroup(editor, targetScope)
                    }
                } else {
                editor.environment?.let { facts ->
                    Text(facts.target.resourceId,style=MaterialTheme.typography.bodySmall)
                    Text(stringResource(effectLabel(facts.effectiveState)))
                    KeyValueRow(stringResource(R.string.host_settings_observed),facts.observedAt)
                    Text(stringResource(R.string.host_settings_variable_count,facts.variables.size))
                    facts.variables.forEach { variable ->
                        TextButton(onClick={editor.beginEnvironmentEdit(editor.environmentScope,variable)},enabled=!editor.busy && !editor.dirty && !editor.environmentEditing && variable.rawValue != null) { Text(variable.name) }
                        Text(variable.rawValue ?: stringResource(R.string.host_settings_masked),style=MaterialTheme.typography.bodySmall)
                        variable.expandedPreview?.takeIf { it!=variable.rawValue }?.let { Text(stringResource(R.string.host_settings_expanded,it),style=MaterialTheme.typography.bodySmall) }
                        if(variable.warnings.isNotEmpty()) Text(stringResource(R.string.host_settings_warning),style=MaterialTheme.typography.bodySmall)
                    }
                    TextButton(onClick={editor.beginEnvironmentEdit(editor.environmentScope,null)},enabled=!editor.busy && !editor.dirty && !editor.environmentEditing) { Text(stringResource(R.string.host_settings_path_new)) }
                }
                }

            }
        }
        PageActionRow(refresh = {
                TextButton(onClick={editor.load()},enabled=!editor.busy&&!editor.dirty) { ActionLabel(R.string.common_refresh) }
        }, actions = {
            TextButton(onClick={editor.discard()},enabled=!editor.busy&&editor.dirty) { Text(stringResource(R.string.host_settings_discard)) }
            if (editor.kind != HostSettingKind.Environment)
                Button(onClick={editor.preview()},enabled=editor.canPreview) { Text(stringResource(R.string.host_settings_preview)) }
        })
        val visibleReferences = editor.references.filter { it.kind == editor.kind }.take(20)
        if (visibleReferences.isNotEmpty()) Text(stringResource(R.string.host_settings_history),style=MaterialTheme.typography.titleMedium)
        visibleReferences.forEach { ref ->
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
    if (editor.environmentEditing && editor.resumed && editor.plan == null) {
        SettingsReview(
            title=stringResource(if(editor.environmentScope == HostEnvironmentScope.HostUser) R.string.host_settings_user else R.string.host_settings_machine),
            confirm=stringResource(R.string.host_settings_preview), onDismiss={if(!editor.busy) editor.discard()},
            onConfirm=editor::preview, confirmEnabled=editor.canPreview, dismissEnabled=!editor.busy
        ) {
            OutlinedTextField(editor.name,{if(it.length<=255) { editor.name=it }},
                readOnly=editor.existingVariable,label={Text(stringResource(R.string.host_settings_variable))},
                singleLine=true,enabled=!editor.busy,modifier=Modifier.fillMaxWidth())
            if (editor.delete) {
                Text(stringResource(R.string.host_settings_delete))
                Text(editor.name)
            } else {
                if(editor.name.equals("PATH",ignoreCase=editor.environment?.caseSensitiveNames == false)) {
                    key(editor.name,editor.environmentScope) { EnvironmentPathList(editor.value,requireNotNull(editor.environment).pathSeparator,!editor.busy) { editor.value=it } }
                } else {
                    OutlinedTextField(editor.value,{if(it.length<=32767) editor.value=it},
                        label={Text(stringResource(R.string.host_settings_value))},enabled=!editor.busy,
                        minLines=6,maxLines=12,modifier=Modifier.fillMaxWidth())
                }
                if(owner.serverPlatform.equals("windows",true)) {
                Row(verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
                    Checkbox(editor.expand,{editor.expand=it},enabled=!editor.busy)
                    Text(stringResource(R.string.host_settings_expand))
                }
                Text(stringResource(R.string.host_settings_expand_hint),style=MaterialTheme.typography.bodySmall)
                }
                if(!owner.serverPlatform.equals("windows",true) && editor.existingVariable) {
                    Row(verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
                        Checkbox(editor.delete,{editor.delete=it},enabled=!editor.busy)
                        Text(stringResource(R.string.host_settings_delete))
                    }
                }
            }
            if(editor.delete && !owner.serverPlatform.equals("windows",true)) {
                TextButton(onClick={editor.delete=false},enabled=!editor.busy) { Text(stringResource(R.string.common_edit)) }
            }
            if(editor.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
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
private fun SettingsReview(title:String,confirm:String,onDismiss:()->Unit,onConfirm:()->Unit,confirmEnabled:Boolean=true,dismissEnabled:Boolean=true,content:@Composable ColumnScope.()->Unit) {
    Dialog(onDismissRequest={if(dismissEnabled) onDismiss()},properties=DialogProperties(usePlatformDefaultWidth=false)) {
        BoxWithConstraints(Modifier.imePadding().padding(Spacing.md)) {
            Surface(Modifier.widthIn(max=560.dp).fillMaxWidth().heightIn(max=maxHeight * 0.85f),shape=MaterialTheme.shapes.large) {
                Column(Modifier.padding(Spacing.lg),verticalArrangement=Arrangement.spacedBy(Spacing.md)) {
                    Text(title,style=MaterialTheme.typography.titleLarge)
                    Column(Modifier.weight(1f,fill=false).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(Spacing.sm),content=content)
                    FlowRow(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(Spacing.sm,androidx.compose.ui.Alignment.End)) {
                        TextButton(onClick=onDismiss,enabled=dismissEnabled) { Text(stringResource(R.string.common_cancel)) }
                        Button(onClick=onConfirm,enabled=confirmEnabled) { Text(confirm) }
                    }
                }
            }
        }
    }
}
private fun HostSettingKind.label() = when(this) { HostSettingKind.Time -> R.string.host_settings_time; HostSettingKind.Identity -> R.string.host_settings_identity; HostSettingKind.Environment -> R.string.host_settings_environment }
private fun effectLabel(value:String) = when(value) { "immediate" -> R.string.host_settings_immediate; "newProcess" -> R.string.host_settings_new_process; "newLogin" -> R.string.host_settings_new_login; "serviceRestart" -> R.string.host_settings_service_restart; "hostRestart" -> R.string.host_settings_host_restart; else -> R.string.host_settings_unknown }
private fun stateLabel(value:String) = when(value) { "prepared" -> R.string.host_settings_prepared; "applying" -> R.string.host_settings_applying; "applied" -> R.string.host_settings_applied; "failed" -> R.string.host_settings_failed; "partiallyApplied" -> R.string.host_settings_partial; "unknown" -> R.string.host_settings_unknown; "awaitingConfirmation" -> R.string.host_settings_awaiting; "rolledBack" -> R.string.host_settings_rolled_back; "recoveryRequired" -> R.string.host_settings_recovery; else -> R.string.host_settings_unknown }

@Composable
private fun EnvironmentPathList(value: String, separator: String, enabled: Boolean, onValueChange: (String) -> Unit) {
    var selected by remember { mutableIntStateOf(-1) }
    val entryFocus = remember { FocusRequester() }
    val entries = value.split(separator)
    val listState = androidx.compose.foundation.lazy.rememberLazyListState()
    fun update(next: List<String>, index: Int) {
        val raw = next.joinToString(separator)
        if (raw.length <= 32767) {
            selected = index.coerceAtMost(next.lastIndex)
            onValueChange(raw)
        }
    }
    LaunchedEffect(selected, value) {
        if (selected in entries.indices) listState.animateScrollToItem(selected)
    }
    val hasSelection = selected in entries.indices
    Text(stringResource(R.string.host_settings_path_entries), style=MaterialTheme.typography.titleSmall)
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val wide = maxWidth >= 600.dp
        val actions: @Composable () -> Unit = {
            TextButton(onClick={ update(entries + "", entries.size) },enabled=enabled) { Text(stringResource(R.string.host_settings_path_new)) }
            TextButton(onClick={entryFocus.requestFocus()},enabled=enabled && hasSelection) { Text(stringResource(R.string.common_edit)) }
            TextButton(onClick={ update(entries.filterIndexed { index, _ -> index != selected }, selected) },enabled=enabled && hasSelection) { ActionLabel(R.string.common_delete) }
            TextButton(onClick={
                val next=entries.toMutableList(); val index=selected
                java.util.Collections.swap(next,index,index-1); update(next,index-1)
            },enabled=enabled && hasSelection && selected>0) { Text(stringResource(R.string.host_settings_path_up)) }
            TextButton(onClick={
                val next=entries.toMutableList(); val index=selected
                java.util.Collections.swap(next,index,index+1); update(next,index+1)
            },enabled=enabled && hasSelection && selected<entries.lastIndex) { Text(stringResource(R.string.host_settings_path_down)) }
        }
        Column(verticalArrangement=Arrangement.spacedBy(Spacing.sm)) {
            Row(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
                LazyColumn(Modifier.weight(1f).heightIn(max=320.dp),state=listState) {
                    itemsIndexed(entries) { index, entry ->
                        Surface(onClick={selected=index},enabled=enabled,
                            color=if(selected==index) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface,
                            modifier=Modifier.fillMaxWidth()) {
                            Text(entry.ifEmpty { stringResource(R.string.host_settings_path_empty) },
                                modifier=Modifier.padding(Spacing.md),style=MaterialTheme.typography.bodyMedium)
                        }
                    }
                }
                if(wide) Column(content={actions()})
            }
            if(!wide) FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm),content={actions()})
            OutlinedTextField(if(hasSelection) entries[selected] else "",{ text ->
                if(!text.contains(separator) && hasSelection) {
                    val next=entries.toMutableList(); next[selected]=text; update(next,selected)
                }
            },enabled=enabled && hasSelection,label={Text(stringResource(R.string.host_settings_value))},modifier=Modifier.fillMaxWidth().focusRequester(entryFocus))
        }
    }
}

@Composable
private fun WindowsEnvironmentGroup(editor: HostSettingsEditor, targetScope: HostEnvironmentScope) {
    val facts=editor.windowsEnvironments[targetScope]
    var selectedName by remember(facts?.revision,targetScope) { mutableStateOf<String?>(null) }
    val selected=facts?.variables?.firstOrNull { it.name==selectedName }
    val enabled=!editor.busy && !editor.dirty && !editor.environmentEditing
    SectionCard(stringResource(if(targetScope==HostEnvironmentScope.HostUser) R.string.host_settings_user else R.string.host_settings_machine)) {
        if(facts!=null) {
            LazyColumn(Modifier.fillMaxWidth().heightIn(max=280.dp)) {
                itemsIndexed(facts.variables,key={_,variable -> variable.name}) { _,variable ->
                    Surface(onClick={selectedName=variable.name},enabled=enabled,
                        color=if(selectedName==variable.name) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface,
                        modifier=Modifier.fillMaxWidth()) {
                        Row(Modifier.padding(Spacing.sm),horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
                            Text(variable.name,Modifier.weight(0.4f),maxLines=1,overflow=TextOverflow.Ellipsis)
                            Text(variable.rawValue ?: stringResource(R.string.host_settings_masked),Modifier.weight(0.6f),maxLines=1,overflow=TextOverflow.Ellipsis)
                        }
                    }
                }
            }
            FlowRow(horizontalArrangement=Arrangement.spacedBy(Spacing.sm)) {
                TextButton(onClick={editor.beginEnvironmentEdit(targetScope,null)},enabled=enabled) { Text(stringResource(R.string.host_settings_path_new)) }
                TextButton(onClick={editor.beginEnvironmentEdit(targetScope,selected)},enabled=enabled && selected!=null && selected.rawValue!=null) { Text(stringResource(R.string.common_edit)) }
                TextButton(onClick={editor.beginEnvironmentEdit(targetScope,selected,true)},enabled=enabled && selected!=null) { ActionLabel(R.string.common_delete) }
            }
        } else if(targetScope in editor.environmentAuthorizationScopes) {
            Text(stringResource(R.string.host_settings_environment_authorization_required),color=MaterialTheme.colorScheme.error)
            TextButton(onClick=editor::load,enabled=enabled) { Text(stringResource(R.string.host_settings_environment_retry)) }
        } else if(!editor.busy) {
            Text(stringResource(R.string.host_settings_unavailable))
        }
    }
}
