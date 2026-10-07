package app.relaxkonos.mobile.ui.manage.websites

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.WebServer
import org.junit.Assert.assertEquals
import org.junit.Test

class WebsitePublicationPrerequisiteTest {
    @Test fun refreshKeepsOnlyAnAvailablePublicationTarget() {
        assertEquals("current", websitePublicationSelection("current", listOf("first", "current")))
        assertEquals("first", websitePublicationSelection("removed", listOf("first")))
        assertEquals(null, websitePublicationSelection("removed", emptyList()))
    }
    @Test fun loadingDoesNotReportMissingResources() {
        assertEquals(R.string.common_loading, websitePublicationPrerequisite(WebsitesState(loading = true)))
    }

    @Test fun failedReadAndMissingApplicationCapabilityCanBeRetried() {
        assertEquals(R.string.websites_load_failed, websitePublicationPrerequisite(WebsitesState(
            servers = ApiResult.Transport(null), applications = ApiResult.Success(emptyList()))))
        assertEquals(R.string.websites_load_failed, websitePublicationPrerequisite(WebsitesState(
            servers = ApiResult.Success(emptyList()), applications = null)))
    }

    @Test fun missingWebServerHasSpecificNextStep() {
        assertEquals(R.string.websites_publish_no_eligible_server, websitePublicationPrerequisite(WebsitesState(
            servers = ApiResult.Success(emptyList()), applications = ApiResult.Success(emptyList()))))
    }

    @Test fun webServerWithoutRunningApplicationHasSpecificNextStep() {
        val server = WebServer("nginx", "nginx", "managed", null, true, true,
            "nginx", "/usr/sbin/nginx", null, 0L, true, true, true, true, false)
        val facts = WebsiteServerState(server, ApiResult.Transport(null), null, ApiResult.Success(emptyList()))
        assertEquals(R.string.websites_publish_no_running_application, websitePublicationPrerequisite(WebsitesState(
            servers = ApiResult.Success(listOf(facts)), applications = ApiResult.Success(emptyList()))))
    }
}
