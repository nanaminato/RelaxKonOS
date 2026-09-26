package app.relaxkonos.mobile.security

/**
 * Debug-only trace of the Keystore path behind the credential vaults.
 *
 * `AndroidKeyStore` cannot be exercised by a JVM unit test, and four very different platform
 * refusals — "the device cannot satisfy the policy", "the key is missing", "the key was invalidated"
 * and "the record was tampered with" — all reach the user as one short sentence each. Telling them
 * apart therefore needs a real device and a real log, which is all this exists for. It is what caught
 * the vault reporting "your fingerprint changed" for a key that had simply never been created.
 *
 * Nothing here ever receives a secret: no password, no account, no server address, no `Cipher` and no
 * key material — by design, the vault's own logging is limited to the vault kind, the provider
 * decisions, exception classes and outcome enums (`RelaxKonOS.Mobile.V1.Design.md` §5.2,
 * `RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.3).
 *
 * The sink is installed by the application, so this object stays loadable in JVM tests: with no sink
 * installed every call is a no-op and nothing touches `android.util.Log`.
 */
internal object VaultDiagnostics {
    /** Logcat tag. Filter with `adb logcat -s RelaxKonVault:D`. */
    const val TAG = "RelaxKonVault"

    /**
     * Where events go, or `null` to discard them. Installed once per process, and only in a debug
     * build: a release build keeps [sink] permanently `null` rather than paying for log calls.
     */
    var sink: ((event: String, detail: String?) -> Unit)? = null

    /** Records a decision that changes what the vault will do. [detail] must never carry a secret. */
    fun trace(event: String, detail: String? = null) {
        sink?.invoke(event, detail)
    }

    /** Records a platform refusal together with the raw type, which is the part the UI cannot show. */
    fun failure(event: String, error: Throwable) {
        sink?.invoke(event, "${error.javaClass.name}: ${error.message}")
    }
}
