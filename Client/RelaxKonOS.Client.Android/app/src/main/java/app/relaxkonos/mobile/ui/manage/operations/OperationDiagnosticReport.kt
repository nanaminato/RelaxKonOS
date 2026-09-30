package app.relaxkonos.mobile.ui.manage.operations

import app.relaxkonos.mobile.data.ObservedOperation
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import org.json.JSONArray
import org.json.JSONObject

internal object OperationDiagnosticReport {
    fun create(item: ObservedOperation, diagnostics: List<String>, diagnosticsAvailable: Boolean): String {
        val lines = diagnostics.take(100).map { it.take(512) }
        return JSONObject()
            .put("format", "relaxkonos-operation-diagnostics")
            .put("formatVersion", 1)
            .put("exportedAtUtc", utc(System.currentTimeMillis()))
            .put("serviceId", item.reference.serviceId)
            .put("domain", item.reference.domain.name)
            .put("resourceId", item.reference.resourceId)
            .put("operationId", item.reference.operationId)
            .put("observedFromUtc", utc(item.reference.seenAtMillis))
            .put("lastVerifiedAtUtc", item.checkedAtMillis?.let(::utc) ?: JSONObject.NULL)
            .put("check", item.check.name)
            .put("state", item.state ?: JSONObject.NULL)
            .put("stage", item.stage ?: JSONObject.NULL)
            .put("installationService", item.installation?.service?.wire ?: JSONObject.NULL)
            .put("installationKind", item.installation?.kind?.wire ?: JSONObject.NULL)
            .put("stageProgress", item.installation?.progress ?: JSONObject.NULL)
            .put("problemCode", item.problemCode ?: JSONObject.NULL)
            .put("diagnosticsAvailable", diagnosticsAvailable)
            .put("diagnostics", JSONArray(lines))
            .put("diagnosticsTruncated", diagnostics.size > lines.size || diagnostics.take(100).any { it.length > 512 })
            .toString(2)
    }

    private fun utc(millis: Long): String = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.ROOT)
        .apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date(millis))
}
