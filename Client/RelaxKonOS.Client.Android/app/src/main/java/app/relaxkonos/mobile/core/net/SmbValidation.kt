package app.relaxkonos.mobile.core.net

object SmbValidation {
    fun password(value: CharArray) = value.size in 12..1024 && value.none(Char::isISOControl)
    private fun safe(value: String) = value.none(Char::isISOControl) && !value.contains("[global]", true) && !value.contains("include", true) && !value.contains('=') && !value.startsWith('-')
    fun share(value: SmbShareRequest, windows: Boolean): Boolean {
        if (!value.name.matches(Regex("[\\p{L}\\p{N}][\\p{L}\\p{N}\\p{M}._ -]{0,79}")) || !safe(value.name)) return false
        if (value.description?.let { it.length > 256 || !safe(it) } == true || !safe(value.path)) return false
        val absolute = if (windows) value.path.matches(Regex("[A-Za-z]:[\\\\/].*")) || value.path.matches(Regex("\\\\\\\\[^\\\\/]+[\\\\/][^\\\\/]+.*")) else value.path.startsWith('/')
        if (!absolute || value.permissions.size > 128 || value.permissions.map { it.principal.lowercase() }.distinct().size != value.permissions.size) return false
        return value.permissions.all { permission ->
            safe(permission.principal) && permission.principal.matches(Regex("[A-Za-z0-9._@\\\\-]{1,256}")) &&
                (!windows || permission.principal.matches(Regex("S-[0-9]+(-[0-9]+)+"))) &&
                (windows || !value.guestAllowed || permission.access != SmbAccess.ReadWrite || permission.principal.matches(Regex("[a-z_][a-z0-9_-]{0,63}")) && permission.principal != "nobody")
        }
    }
    fun outsideSuggestedRoot(path: String, windows: Boolean): Boolean {
        val normalized = if (windows) path.replace('/', '\\').trimEnd('\\').lowercase() else path.trimEnd('/')
        val separator = if (windows) '\\' else '/'
        val root = if (windows) "d:\\relaxkonosshares" else "/srv/relaxkonos-shares"
        return normalized.split(separator).contains("..") || !(normalized == root || normalized.startsWith(root + separator))
    }
}
