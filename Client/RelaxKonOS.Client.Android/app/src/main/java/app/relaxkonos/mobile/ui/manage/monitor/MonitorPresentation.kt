package app.relaxkonos.mobile.ui.manage.monitor

import app.relaxkonos.mobile.core.net.*

internal enum class PerformanceKind { Cpu, Memory, Filesystem, Disk, Network }
internal data class PerformanceResource(val kind: PerformanceKind, val id: String, val name: String?) {
    val key: String get() = "${kind.name}:$id"
}
internal fun performanceResources(info: PerformanceInfo?, snapshot: PerformanceSnapshot?): List<PerformanceResource> = buildList {
    if (info == null && snapshot == null) return@buildList
    add(PerformanceResource(PerformanceKind.Cpu, "cpu", null)); add(PerformanceResource(PerformanceKind.Memory, "memory", null))
    fun addResources(kind: PerformanceKind, names: Map<String, String>, ids: List<String>) {
        (names.keys + ids).distinct().forEach { add(PerformanceResource(kind, it, names[it])) }
    }
    addResources(PerformanceKind.Filesystem, info?.filesystems?.associate { it.id to it.name }.orEmpty(), snapshot?.filesystems?.map { it.id }.orEmpty())
    addResources(PerformanceKind.Disk, info?.disks?.associate { it.id to it.name }.orEmpty(), snapshot?.disks?.map { it.id }.orEmpty())
    addResources(PerformanceKind.Network, info?.networks?.associate { it.id to it.name }.orEmpty(), snapshot?.networks?.map { it.id }.orEmpty())
}
internal fun monitorUsesTwoPanes(widthDp: Float, heightDp: Float, fontScale: Float): Boolean =
    widthDp >= maxOf(840f, 720f * fontScale) && heightDp >= 240f * fontScale
internal data class TrendPoint(val timeMillis: Long, val value: Double?)
internal fun trendPoints(history: List<PerformanceSnapshot>, value: (PerformanceSnapshot) -> Double?): List<TrendPoint> = history.map {
    TrendPoint(IsoInstant.requireEpochMillis(it.timestamp), if (it.health.isStale) null else value(it))
}
internal fun trendSegments(points: List<TrendPoint>): List<List<TrendPoint>> {
    val segments = mutableListOf<MutableList<TrendPoint>>()
    var current: MutableList<TrendPoint>? = null
    for (point in points) {
        val value = point.value
        if (value == null || !value.isFinite() || value < 0) { current = null; continue }
        val previous = current?.lastOrNull()
        if (previous == null || point.timeMillis <= previous.timeMillis || point.timeMillis - previous.timeMillis > 2_500) {
            current = mutableListOf(); segments.add(current)
        }
        current!!.add(point)
    }
    return segments
}
