package app.relaxkonos.mobile.ui.manage.certificates

import androidx.compose.foundation.layout.Column
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import java.text.DateFormat
import java.util.Date

enum class CertificateSelectionProblem { Unverified, Missing, Required, Ineligible }
fun certificateSelectionProblem(result: ApiResult<List<ManagedCertificate>>?, id: String?, domains: List<String>, nowMillis: Long = System.currentTimeMillis()): CertificateSelectionProblem? {
    if (id == null) return CertificateSelectionProblem.Required
    if (result !is ApiResult.Success) return CertificateSelectionProblem.Unverified
    val certificate = result.value.firstOrNull { it.id == id } ?: return CertificateSelectionProblem.Missing
    return if (certificate.usageProblem(domains, nowMillis) != null) CertificateSelectionProblem.Ineligible else null
}
@Composable
internal fun certificateUsageLabel(problem: CertificateUsageProblem): String = stringResource(when (problem) {
    CertificateUsageProblem.MetadataUnavailable -> R.string.certificates_binding_unverified
    CertificateUsageProblem.UnusableStatus -> R.string.certificates_binding_status
    CertificateUsageProblem.NotYetValid -> R.string.certificates_binding_future
    CertificateUsageProblem.Expired -> R.string.certificates_expired_warning
    CertificateUsageProblem.DomainMismatch -> R.string.certificates_binding_domain
})
@Composable
internal fun ManagedCertificatePicker(result: ApiResult<List<ManagedCertificate>>?, domains: List<String>, selectedId: String?, enabled: Boolean, select: (String?) -> Unit) {
    Text(stringResource(R.string.certificates_binding_select))
    when (certificateSelectionProblem(result, selectedId, domains)) {
        CertificateSelectionProblem.Required -> Text(stringResource(R.string.certificates_binding_required))
        CertificateSelectionProblem.Unverified -> Text(stringResource(R.string.certificates_binding_unverified), color = MaterialTheme.colorScheme.error)
        CertificateSelectionProblem.Missing -> Text(stringResource(R.string.certificates_binding_missing, selectedId.orEmpty()), color = MaterialTheme.colorScheme.error)
        else -> Unit
    }
    if (result is ApiResult.Success) {
        if (result.value.isEmpty()) Text(stringResource(R.string.certificates_empty))
        result.value.forEach { certificate -> Column {
            val problem = certificate.usageProblem(domains)
            OutlinedButton(enabled = enabled && problem == null, onClick = { select(certificate.id) }) {
                Text((if (certificate.id == selectedId) "✓ " else "") + certificate.primaryDomain)
            }
            Text(certificate.id, style = MaterialTheme.typography.bodySmall)
            Text(certificate.subjectAlternativeNames.joinToString(", "), style = MaterialTheme.typography.bodySmall)
            Text(certificateStatusLabel(certificate.status), style = MaterialTheme.typography.bodySmall)
            certificate.notAfterMillis?.let { Text(stringResource(R.string.certificates_expires, DateFormat.getDateTimeInstance().format(Date(it))), style = MaterialTheme.typography.bodySmall) }
            certificate.fingerprintSha256?.let { Text(stringResource(R.string.certificates_fingerprint, it), style = MaterialTheme.typography.bodySmall) }
            if (problem != null) Text(certificateUsageLabel(problem), color = MaterialTheme.colorScheme.error)
            if (certificate.kind == CertificateKind.SelfSigned) Text(stringResource(R.string.certificates_self_signed_note), style = MaterialTheme.typography.bodySmall)
        } }
    } else Text(stringResource(R.string.certificates_binding_unverified), color = MaterialTheme.colorScheme.error)
    if (selectedId != null) TextButton(enabled = enabled, onClick = { select(null) }) { Text(stringResource(R.string.certificates_binding_clear)) }
    Text(stringResource(R.string.certificates_binding_evidence), style = MaterialTheme.typography.bodySmall)
}
@Composable
internal fun SiteCertificateBinding(site: WebServerSite, result: ApiResult<List<ManagedCertificate>>?) {
    if (!site.httpsEnabled) { Text(stringResource(R.string.websites_tls_disabled)); return }
    if (site.certificateId == null) { Text(stringResource(R.string.certificates_binding_host_files)); return }
    val certificate = (result as? ApiResult.Success)?.value?.firstOrNull { it.id == site.certificateId }
    when (certificateSelectionProblem(result, site.certificateId, site.bindings.map { it.domain })) {
        CertificateSelectionProblem.Missing -> Text(stringResource(R.string.certificates_binding_missing, site.certificateId), color = MaterialTheme.colorScheme.error)
        CertificateSelectionProblem.Unverified -> Text(stringResource(R.string.certificates_binding_unverified), color = MaterialTheme.colorScheme.error)
        else -> Unit
    }
    certificate?.let {
        Text(it.primaryDomain + " · " + certificateStatusLabel(it.status))
        it.notAfterMillis?.let { expiry -> Text(stringResource(R.string.certificates_expires, DateFormat.getDateTimeInstance().format(Date(expiry)))) }
        it.usageProblem(site.bindings.map { binding -> binding.domain })?.let { problem -> Text(certificateUsageLabel(problem), color = MaterialTheme.colorScheme.error) }
        if (it.kind == CertificateKind.SelfSigned) Text(stringResource(R.string.certificates_self_signed_note))
    }
    Text(stringResource(R.string.certificates_binding_evidence), style = MaterialTheme.typography.bodySmall)
}
