package app.relaxkonos.mobile.ui.connect

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.net.CertificateReview
import kotlinx.coroutines.CompletableDeferred

class CertificateConsentRequest internal constructor(val review: CertificateReview) {
    internal val answer = CompletableDeferred<Boolean>()
}

internal class CertificateConsentWaiter {
    var current by mutableStateOf<CertificateConsentRequest?>(null)
        private set

    suspend fun ask(review: CertificateReview): Boolean {
        val request = CertificateConsentRequest(review)
        val previous = current
        current = request
        previous?.answer?.complete(false)
        return try { request.answer.await() }
        finally { if (current === request) current = null }
    }

    fun answer(request: CertificateConsentRequest, accept: Boolean) {
        if (current === request) request.answer.complete(accept)
    }
}
