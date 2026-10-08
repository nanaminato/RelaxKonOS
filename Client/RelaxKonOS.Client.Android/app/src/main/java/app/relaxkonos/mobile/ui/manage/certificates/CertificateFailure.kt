package app.relaxkonos.mobile.ui.manage.certificates

import app.relaxkonos.mobile.core.net.ApiResult

internal data class CertificateFailure(val problemCode: String?, val uncertain: Boolean)

internal fun certificateFailure(result: ApiResult<*>, writeResult: Boolean): CertificateFailure =
    when (result) {
        is ApiResult.Transport -> CertificateFailure(if (writeResult) null else "certificate.read_failed", writeResult)
        is ApiResult.Problem -> CertificateFailure(result.code, false)
        is ApiResult.Success -> CertificateFailure(null, false)
    }
