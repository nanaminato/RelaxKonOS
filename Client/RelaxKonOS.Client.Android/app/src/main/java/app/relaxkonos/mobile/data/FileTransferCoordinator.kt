package app.relaxkonos.mobile.data

import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

data class Transfer(
    val label: String,
    val kind: TransferKind,
    val transferredBytes: Long = 0,
    val totalBytes: Long? = null,
    val collapsed: Boolean = false,
) {
    val progress: Float? get() = totalBytes?.takeIf { it > 0 }?.let { (transferredBytes.toDouble() / it).coerceIn(0.0, 1.0).toFloat() }
}

enum class TransferKind { Download, Upload, Move, Copy }

/** Owns single-request transfers beyond any screen or Activity; no replay after cancellation. */
class FileTransferCoordinator(
    private val scope: CoroutineScope,
    private val activeOwner: () -> Any?,
    private val startKeeper: (Long) -> Unit,
    private val onFailure: (TransferKind, Exception) -> Unit,
) {
    private val mutable = MutableStateFlow<Transfer?>(null)
    val state = mutable.asStateFlow()
    private var job: Job? = null
    private var owner: Any? = null
    @Volatile var lease = 0L
        private set
    val isRunning: Boolean get() = mutable.value != null

    fun isCurrent(expectedLease: Long): Boolean = expectedLease == lease && owner != null && activeOwner() === owner && isRunning

    fun start(initial: Transfer, action: suspend (Long) -> Unit): Boolean {
        if (isRunning) return false
        owner = activeOwner() ?: return false
        val epoch = ++lease
        mutable.value = initial
        job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                startKeeper(epoch)
                if (!isCurrent(epoch)) throw CancellationException("File transfer session changed")
                action(epoch)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                if (isCurrent(epoch)) onFailure(initial.kind, error)
            } finally {
                if (lease == epoch) { mutable.value = null; job = null; owner = null }
            }
        }.also { it.start() }
        return true
    }

    fun update(expectedLease: Long, change: (Transfer) -> Transfer) {
        mutable.update { current -> if (current != null && isCurrent(expectedLease)) change(current) else current }
    }

    fun cancel(expectedLease: Long = lease) {
        if (expectedLease != lease) return
        lease++
        job?.cancel()
        job = null
        mutable.value = null
        owner = null
    }

    fun sessionChanged() { if (owner != null && activeOwner() !== owner) cancel() }
}
