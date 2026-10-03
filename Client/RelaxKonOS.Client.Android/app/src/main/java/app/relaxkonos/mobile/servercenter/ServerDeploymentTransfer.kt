package app.relaxkonos.mobile.servercenter

internal fun readDeploymentTransfer(json: String, operationId: String): ServerDeploymentTransfer? {
    val fields = ServerCenterJson.parse(json, 1024).asObject()
    require(fields.keys == setOf("operationId", "bytes", "total", "active"))
    require(fields.required("operationId").asString().equals(operationId, ignoreCase = true))
    val bytes = fields.required("bytes").asLong()
    val total = fields.nullableLong("total")
    require(bytes in 0..8_589_934_592L && (total == null || total in 0..8_589_934_592L))
    return if (fields.required("active").asBoolean()) ServerDeploymentTransfer(bytes, total) else null
}
