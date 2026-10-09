package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.RemoteEntry
import kotlinx.coroutines.CancellationException

data class FileBatchFailure(val path: String, val result: ApiResult<Nothing>, val unknown: Boolean)
data class FileBatchReport(val total: Int, val completed: List<String>, val failures: List<FileBatchFailure>, val skipped: List<String>)

/** Sequential, stop-between-items execution. A failed transport is never replayed or called a failure to apply. */
class FileBatchRunner(private val files: FilesRepository, private val session: AuthSession) {
    suspend fun run(owner: SessionState.Active, entries: List<RemoteEntry>, action: FileBatchAction,
        destination: (RemoteEntry) -> String, provider: ElevationAnswerProvider,
        stop: () -> Boolean, progress: (Int, RemoteEntry) -> Unit): FileBatchReport {
        val snapshot = FileBrowserPolicy.snapshot(entries)
        val completed = mutableListOf<String>()
        val failures = mutableListOf<FileBatchFailure>()
        var attempted = 0
        for (entry in snapshot) {
            if (session.state.value !== owner) throw CancellationException("File operation owner changed.")
            if (stop()) break
            progress(attempted, entry)
            val result = try { when (action) {
                FileBatchAction.Copy -> files.copy(entry.path, destination(entry), provider)
                FileBatchAction.Move -> files.move(entry.path, destination(entry), provider)
                FileBatchAction.Delete -> files.delete(entry.path, provider)
            } } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                // Dispatch may already have changed the host; retain confirmed items and stop.
                ApiResult.Transport(null)
            }
            if (session.state.value !== owner) throw CancellationException("File operation owner changed.")
            attempted++
            when (result) {
                is ApiResult.Success -> completed += entry.path
                is ApiResult.Transport -> { failures += FileBatchFailure(entry.path, result, true); break }
                is ApiResult.Problem -> {
                    val unknown = result.status >= 500
                    failures += FileBatchFailure(entry.path, result, unknown)
                    // Do not keep prompting once an authorization was declined, or queue new work on an unknown result.
                    if (unknown || result.code == ProblemCodes.ELEVATION_REQUIRED || result.status in setOf(401, 403)) break
                }
            }
        }
        return FileBatchReport(snapshot.size, completed, failures, snapshot.drop(attempted).map { it.path })
    }
}
