package app.relaxkonos.mobile.core.net

/**
 * The one step every flow that talks to a server over TLS must run before it sends a request.
 *
 * A first HTTPS handshake against an un-pinned, self-signed leaf fails inside
 * [ServerCertificateTrust]: the review is recorded and the handshake throws, instead of trusting the
 * certificate. The pin is written only by [ServerCertificateTrust.trust], and only after the user
 * answered that review. A flow that never looks at the review cannot offer the decision at all — its
 * request fails and the failure reads as an unreachable server.
 *
 * Password sign-in has always probed its address first ([ServerEndpointDiscovery]), and that probe is
 * what produces the review. Owner-device pairing and owner-device sign-in send their request straight
 * to the gateway, so a self-signed server could not be paired with and nothing on screen said why.
 * Both run [reviewFor] first now.
 */
object ServerCertificateConfirmation {

    /**
     * The review an earlier probe left behind, or `null` when this address has nothing to ask about.
     *
     * [ServerEndpointDiscovery.discover] records the review when its own handshake fails, so a caller
     * that already probed only has to look. Probing again would be a second request that can only
     * repeat the same answer.
     */
    fun pending(candidates: List<String>): CertificateReview? =
        candidates.filter { java.net.URI(it).scheme.equals("https", ignoreCase = true) }.firstNotNullOfOrNull { ServerCertificateTrust.review(it) }

    /**
     * The review the user must answer before [candidates] may be used, or `null` when there is
     * nothing to ask: the origin is already pinned, or it never presented a TLS certificate.
     *
     * [probe] is the request that makes the first handshake happen. It runs only when no review is
     * pending, so a caller that already probed never probes twice.
     */
    suspend fun reviewFor(candidates: List<String>, probe: suspend () -> Unit): CertificateReview? {
        pending(candidates)?.let { return it }
        probe()
        return pending(candidates)
    }

    /**
     * Writes the pin once the user consented.
     *
     * `false` is not a failure of consent: it means the certificate observed now is no longer the one
     * that was reviewed, which is exactly what [ServerCertificateTrust.trust] refuses to pin.
     */
    fun consent(review: CertificateReview): Boolean =
        runCatching { ServerCertificateTrust.trust(review) }.isSuccess
}
