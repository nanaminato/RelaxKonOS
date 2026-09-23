package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.recordId

/**
 * Canonical form of a server address.
 *
 * There is exactly one definition of "the same server", and it is the one the session and the vault
 * already use: surrounding whitespace removed and no trailing slash. Case is deliberately *not*
 * folded — an address may carry a case-sensitive path, and two identifiers that differ only in case may
 * be two different accounts on the server, so folding either one would merge records that the server
 * keeps apart.
 */
fun normalizeServerUrl(value: String): String = value.trim().trimEnd('/')

/**
 * The stable local identity of one login: `serverUrl|identifier`.
 *
 * This is the key the profile list is deduplicated by, and the account half of the vault record that
 * holds the password. `CredentialKey` (see [SelectedLogin.credentialKey]) is the vault-side name for
 * the same pair.
 */
fun loginId(serverUrl: String, identifier: String): String =
    "${normalizeServerUrl(serverUrl)}|${identifier.trim()}"

/**
 * The identity the user is about to sign in as: the `(Service, Username)` pair of the design document,
 * which in RelaxKonOS is `(server address, login identifier)`.
 *
 * Holds raw field text so the form can bind to it directly; every derived value normalises on read, so
 * there is no way to hold a key that disagrees with what the vault looks up
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §2.1).
 *
 * `ServiceA + root`, `ServiceA + nanami` and `ServiceB + nanami` are three unrelated identities, which
 * is why nothing in this client may ever delete or read credentials by server address alone.
 */
data class SelectedLogin(val serverUrl: String, val identifier: String) {
    /** The address as the session and the vault store it. */
    val normalizedServerUrl: String get() = normalizeServerUrl(serverUrl)

    /** The account as the session and the vault store it. */
    val normalizedIdentifier: String get() = identifier.trim()

    /** True when both fields carry something to send. */
    val isComplete: Boolean get() = serverUrl.isNotBlank() && identifier.isNotBlank()

    /** Stable local identity, `serverUrl|identifier`. */
    val id: String get() = loginId(normalizedServerUrl, normalizedIdentifier)

    /** The vault record this identity's saved password lives in, and nothing else. */
    val credentialKey: String
        get() = recordId(VaultKind.Connection, normalizedServerUrl, normalizedIdentifier)
}
