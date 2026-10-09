package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.key
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Consent for a server certificate this device has not pinned yet.
 *
 * It is drawn once, with the shell's other global overlays, and never inside the screen that happens
 * to be asking (`Shell.Design.md` §3.4). The state lives on the shared [LoginViewModel], and the
 * flow waiting for the answer may be the owner-device pairing screen — which is an alternative to
 * the login screen, not something composed with it. Drawn from one screen alone, the pairing flow
 * asked a question nothing rendered, and the request it was waiting on never ran.
 *
 * The fingerprint is the whole point of the prompt, so the dialog shows it in full and offers no way
 * to skip it: "trust" pins exactly this leaf for exactly this origin and port.
 */
@Composable
fun ServerCertificatePrompt(viewModel: LoginViewModel) {
    val request = viewModel.certificatePrompt ?: return
    CertificateConsentDialog(request, viewModel::answerCertificate)
}

@Composable
internal fun CertificateConsentDialog(request: CertificateConsentRequest, onAnswer: (CertificateConsentRequest, Boolean) -> Unit) {
    val review = request.review
    key(request) {
    AlertDialog(
        onDismissRequest = { onAnswer(request, false) },
        title = { Text(stringResource(R.string.login_certificate_title)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.login_certificate_scope))
                Text(review.origin)
                Text(review.subject)
                Text(review.issuer)
                Text("${review.validFrom}\n${review.validUntil}")
                Text("SHA-256\n${review.fingerprint}")
                review.previous?.let { Text(stringResource(R.string.login_certificate_previous, it)) }
            }
        },
        confirmButton = { TextButton(onClick = { onAnswer(request, true) }) { Text(stringResource(R.string.login_certificate_trust)) } },
        dismissButton = { TextButton(onClick = { onAnswer(request, false) }) { Text(stringResource(R.string.common_cancel)) } },
    )
    }
}
