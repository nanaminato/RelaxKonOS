package app.relaxkonos.mobile.core.net

import org.json.JSONObject

data class CatalogApplicationUpdatePreview(
    val applicationId: String,
    val expectedUpdatedAt: String,
    val expectedRevisionId: String?,
    val currentTemplateVersion: String,
    val target: CatalogTemplate,
    val currentImageReference: String?,
    val targetImageReference: String,
    val updateNotes: String,
    val blockers: List<String>,
)

internal object CatalogApplicationUpdateWire {
    fun preview(payload: String): CatalogApplicationUpdatePreview = JSONObject(payload).let { json ->
        fun nullable(key: String): String? {
            require(json.has(key))
            return if (json.isNull(key)) null else json.getString(key)
        }
        CatalogApplicationUpdatePreview(json.getString("applicationId"), json.getString("expectedUpdatedAt").also { IsoInstant.requireEpochMillis(it) },
            nullable("expectedRevisionId"), json.getString("currentTemplateVersion"),
            ApplicationDeploymentWire.catalog("[${json.getJSONObject("target")}]").single(), nullable("currentImageReference"),
            json.getString("targetImageReference"), json.getString("updateNotes"), json.getJSONArray("blockers").let { array -> (0 until array.length()).map(array::getString) })
    }
}
