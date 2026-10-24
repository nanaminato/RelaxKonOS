package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.nav.MobileNavigator
import app.relaxkonos.mobile.ui.nav.Routes
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ApplicationDesktopTest {
    @get:Rule val rule = createComposeRule()

    @Test fun terminalOpensFromHomeAndBackReturnsToDesktop() {
        val navigator = MobileNavigator(Routes.HOME)
        var terminal = ""
        rule.setContent { MaterialTheme {
            terminal = stringResource(R.string.nav_terminal)
            ApplicationTiles(mobileApplications(setOf(ServerCapabilities.TERMINAL)) { navigator.push(it) })
        } }
        rule.onNodeWithText(terminal).assertIsDisplayed().performClick()
        rule.runOnIdle {
            assertEquals(Routes.HOME, navigator.currentDestination)
            assertEquals(Routes.TERMINAL, navigator.route)
            assertTrue(navigator.pop())
            assertEquals(Routes.HOME, navigator.route)
        }
    }

    @Test fun directoryOnlyOffersCapabilitiesOfCurrentServer() {
        var routes = emptySet<String>()
        rule.setContent { MaterialTheme {
            val applications = mobileApplications(setOf(ServerCapabilities.FILES)) {}
            routes = applications.map { it.id }.toSet()
            Box(Modifier.width(360.dp)) { ApplicationTiles(applications) }
        } }
        rule.runOnIdle { assertEquals(setOf(R.string.nav_files.toString()), routes) }
    }
}
