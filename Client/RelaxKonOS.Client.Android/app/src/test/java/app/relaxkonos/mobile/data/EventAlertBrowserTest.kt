package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
class EventAlertBrowserTest {
    private val gateway = FakeGateway(); private val session = AuthSession(gateway)
    private val repository = EventAlertRepository(gateway, session)
    private fun alert(id: String) = OperationalAlert(id, "deployment.operation_failed", "error", "open", "failure", 1, 1,
        "applicationDeployment", "app", null, 0, "e", null, null, null)
    private suspend fun owner(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        gateway.onEventSummary = { ApiResult.Success(EventAlertSummary(2, 0, 0, "error", 1)) }
        return session.state.value as SessionState.Active
    }
    private var writes = 0
    private suspend fun TestScope.reviewBrowser(): EventAlertBrowser {
        val owner = owner()
        gateway.onAlerts = { _, _ -> ApiResult.Success(OperationalAlertPage(listOf(alert("a"), alert("b")), null)) }
        gateway.onAlertDetail = { id -> ApiResult.Success(OperationalAlertDetail(alert(id), emptyList(), emptyList())) }
        gateway.onMutateAlert = { _, _, _, _ -> writes++; ApiResult.Problem(403, "forbidden", null) }
        return EventAlertBrowser(repository, backgroundScope).also {
            it.activate(owner); runCurrent(); it.select("a"); runCurrent()
        }
    }
    @Test fun `failed reconciliation keeps unknown target and gives each failure a new feedback event`() = runTest {
        val browser = reviewBrowser()
        gateway.onMutateAlert = { _, _, _, _ -> writes++; throw IllegalStateException("private send detail") }
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        browser.mutate(requireNotNull(browser.state.value.confirmation), null, null); runCurrent()
        gateway.onAlertDetail = { throw IllegalStateException("private reconciliation detail") }
        browser.reconcile(); runCurrent()
        val first = browser.state.value.detailFeedbackVersion
        assertEquals(ApiResult.Transport(null), browser.state.value.detail)
        browser.reconcile(); runCurrent()
        assertTrue(browser.state.value.detailFeedbackVersion > first)
        assertEquals("a", browser.state.value.selectedId); assertTrue(browser.state.value.unknown)
        assertFalse(browser.state.value.detailLoading); assertEquals(1, writes)
        gateway.onAlertDetail = { ApiResult.Success(OperationalAlertDetail(alert("a").copy(status = "acknowledged"), emptyList(), emptyList())) }
        browser.reconcile(); runCurrent()
        assertFalse(browser.state.value.unknown); assertTrue(browser.state.value.detail is ApiResult.Success)
        assertEquals(1, writes); browser.stop()
    }
    @Test fun `observation preserves open confirmation and resumes after dismissal`() = runTest {
        val browser = reviewBrowser(); var reads = 0
        gateway.onAlerts = { _, _ -> reads++; ApiResult.Success(OperationalAlertPage(listOf(alert("a")), null)) }
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val review = requireNotNull(browser.state.value.confirmation)
        advanceTimeBy(15_000); runCurrent()
        assertSame(review, browser.state.value.confirmation); assertEquals(0, reads)
        browser.dismissMutation(); advanceTimeBy(15_000); runCurrent()
        assertEquals(1, reads); assertEquals(0, writes); browser.stop()
    }
    @Test fun `dismissed review cannot borrow a fresh confirmation for identical alert`() = runTest {
        val browser = reviewBrowser()
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val old = requireNotNull(browser.state.value.confirmation)
        browser.dismissMutation(); browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val fresh = requireNotNull(browser.state.value.confirmation)
        browser.mutate(old, null, null); runCurrent(); assertEquals(0, writes)
        browser.mutate(fresh, null, null); runCurrent(); assertEquals(1, writes); browser.stop()
    }
    @Test fun `confirmation cannot be reused after completed rejection`() = runTest {
        val browser = reviewBrowser()
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val review = requireNotNull(browser.state.value.confirmation)
        browser.mutate(review, null, null); runCurrent()
        browser.mutate(review, null, null); runCurrent()
        assertEquals(1, writes); assertNull(browser.state.value.confirmation); browser.stop()
    }
    @Test fun `selection round trip invalidates original alert confirmation`() = runTest {
        val browser = reviewBrowser()
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val review = requireNotNull(browser.state.value.confirmation)
        browser.select("b"); runCurrent(); browser.select("a"); runCurrent()
        browser.mutate(review, null, null); runCurrent()
        assertEquals(0, writes); assertNull(browser.state.value.confirmation); browser.stop()
    }
    @Test fun `refresh and stopped page both withdraw reviewed action`() = runTest {
        val browser = reviewBrowser()
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val beforeRefresh = requireNotNull(browser.state.value.confirmation)
        browser.refresh(); runCurrent(); browser.mutate(beforeRefresh, null, null); runCurrent()
        assertEquals(0, writes)
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val beforeStop = requireNotNull(browser.state.value.confirmation)
        val owner = requireNotNull(browser.state.value.owner)
        browser.stop(); browser.activate(owner); runCurrent()
        browser.mutate(beforeStop, null, null); runCurrent(); assertEquals(0, writes); browser.stop()
    }
    @Test fun `unexpected page and detail failures remain recoverable without escaping browser`() = runTest {
        val owner = owner()
        gateway.onAlerts = { _, _ -> throw IllegalStateException("private page detail") }
        val browser = EventAlertBrowser(repository, backgroundScope)
        browser.activate(owner); runCurrent()
        assertEquals(ApiResult.Transport(null), browser.state.value.readResult)
        assertFalse(browser.state.value.loading)
        gateway.onAlerts = { _, _ -> ApiResult.Success(OperationalAlertPage(listOf(alert("a")), null)) }
        browser.refresh(); runCurrent()
        gateway.onAlertDetail = { throw IllegalStateException("private detail") }
        browser.select("a"); runCurrent()
        assertEquals(ApiResult.Transport(null), browser.state.value.detail); assertFalse(browser.state.value.detailLoading)
        gateway.onAlertDetail = { ApiResult.Success(OperationalAlertDetail(alert("a"), emptyList(), emptyList())) }
        browser.reconcile(); runCurrent()
        assertTrue(browser.state.value.detail is ApiResult.Success); browser.stop()
    }
    @Test fun `unexpected send failure reaches unknown state and browser never replays`() = runTest {
        val owner = owner(); var writes = 0
        gateway.onAlerts = { _, _ -> ApiResult.Success(OperationalAlertPage(listOf(alert("a")), null)) }
        gateway.onAlertDetail = { ApiResult.Success(OperationalAlertDetail(alert("a"), emptyList(), emptyList())) }
        gateway.onMutateAlert = { _, _, _, _ -> writes++; throw IllegalStateException("private send detail") }
        val browser = EventAlertBrowser(repository, backgroundScope)
        browser.activate(owner); runCurrent(); browser.select("a"); runCurrent()
        browser.requestMutation(alert("a"), AlertMutation.Acknowledge)
        val review = requireNotNull(browser.state.value.confirmation)
        browser.mutate(review, null, null); runCurrent()
        assertTrue(browser.state.value.unknown); assertFalse(browser.state.value.actionBusy)
        assertEquals(ApiResult.Transport(null), browser.state.value.actionResult)
        browser.mutate(review, null, null); browser.reconcile(); runCurrent()
        assertEquals(1, writes); assertTrue(browser.state.value.unknown); browser.stop()
    }
    @Test fun `cursor paging deduplicates and stops a repeating cursor`() = runTest {
        val owner = owner(); val cursors = mutableListOf<String?>()
        gateway.onAlerts = { cursor, _ -> cursors += cursor; ApiResult.Success(OperationalAlertPage(
            if (cursor == null) listOf(alert("a")) else listOf(alert("a"), alert("b")), "next")) }
        val browser = EventAlertBrowser(repository, backgroundScope)
        browser.activate(owner); runCurrent(); browser.more(); runCurrent()
        assertEquals(listOf(null, "next"), cursors); assertEquals(listOf("a", "b"), browser.state.value.alerts.map { it.id })
        assertNull(browser.state.value.nextCursor); browser.stop()
    }
    @Test fun `new filters discard old cursor and old rows`() = runTest {
        val owner = owner(); val queries = mutableListOf<Pair<String?, AlertQuery>>()
        gateway.onAlerts = { cursor, query -> queries += cursor to query; ApiResult.Success(OperationalAlertPage(listOf(alert(query.status?.wire ?: "all")), "next")) }
        val browser = EventAlertBrowser(repository, backgroundScope)
        browser.activate(owner); runCurrent()
        browser.filters(false, AlertQuery(status = AlertStatusFilter.Resolved), EventQuery()); runCurrent()
        assertEquals(null to AlertQuery(status = AlertStatusFilter.Resolved), queries.last())
        assertEquals(listOf("resolved"), browser.state.value.alerts.map { it.id }); browser.stop()
    }
    @Test fun `failed next page preserves current rows and retry cursor`() = runTest {
        val owner = owner(); gateway.onAlerts = { cursor, _ -> if (cursor == null) ApiResult.Success(OperationalAlertPage(listOf(alert("a")), "next")) else ApiResult.Transport(null) }
        val browser = EventAlertBrowser(repository, backgroundScope); browser.activate(owner); runCurrent(); browser.more(); runCurrent()
        assertEquals("a", browser.state.value.alerts.single().id); assertEquals("next", browser.state.value.nextCursor)
        assertTrue(browser.state.value.readResult is ApiResult.Transport); browser.stop()
    }
    @Test fun `leaving page cancels late reads and prevents foreground polling`() = runTest {
        val owner = owner(); var reads = 0
        gateway.onAlerts = { _, _ -> reads++; ApiResult.Success(OperationalAlertPage(listOf(alert("a")), null)) }
        val browser = EventAlertBrowser(repository, backgroundScope); browser.activate(owner); runCurrent(); browser.stop()
        advanceTimeBy(60_000); runCurrent()
        assertEquals(1, reads); assertFalse(browser.state.value.active); assertTrue(browser.state.value.alerts.isEmpty())
    }
}
