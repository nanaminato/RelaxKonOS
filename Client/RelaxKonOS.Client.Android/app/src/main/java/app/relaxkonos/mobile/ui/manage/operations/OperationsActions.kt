package app.relaxkonos.mobile.ui.manage.operations

import android.content.ContentResolver
import android.net.Uri
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.data.ObservedOperation
import app.relaxkonos.mobile.data.OperationCenterSnapshot
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

internal interface OperationsActions {
    suspend fun refresh(owner: SessionState.Active): OperationCenterSnapshot
    suspend fun diagnostics(owner: SessionState.Active, item: ObservedOperation): ApiResult<List<String>>
    suspend fun cancel(owner: SessionState.Active, item: ObservedOperation): ApiResult<*>
    suspend fun hide(owner: SessionState.Active, item: ObservedOperation)
    suspend fun recoverInstallation(owner: SessionState.Active, id: String): ApiResult<*>
    suspend fun exportReport(uri: Uri, report: String): Boolean
}

internal class RepositoryOperationsActions(private val container: AppContainer, private val resolver: ContentResolver) : OperationsActions {
    override suspend fun refresh(owner: SessionState.Active) = container.operationCenter.refresh(owner)
    override suspend fun diagnostics(owner: SessionState.Active, item: ObservedOperation) = container.operationCenter.diagnostics(owner, item)
    override suspend fun cancel(owner: SessionState.Active, item: ObservedOperation) = container.operationCenter.cancel(owner, item)
    override suspend fun hide(owner: SessionState.Active, item: ObservedOperation) { container.operationCenter.hide(owner, item) }
    override suspend fun recoverInstallation(owner: SessionState.Active, id: String) = container.installations.recoverById(owner, id, null)
    override suspend fun exportReport(uri: Uri, report: String): Boolean = try {
        withContext(Dispatchers.IO) {
            val output = resolver.openOutputStream(uri, "w") ?: error("Unable to open diagnostic destination")
            output.use { it.write(report.toByteArray(Charsets.UTF_8)) }
        }
        true
    } catch (cancelled: CancellationException) { throw cancelled }
    catch (_: Exception) { false }
}
