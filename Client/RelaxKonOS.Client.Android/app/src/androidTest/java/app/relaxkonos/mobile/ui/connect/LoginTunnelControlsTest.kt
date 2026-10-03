package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import kotlin.math.abs

class LoginTunnelControlsTest {
    @get:Rule val rule = createComposeRule()

    @Test fun wrappedCheckboxLabelIsCenteredAndClickable() {
        val checked = mutableStateOf(false)
        val label = "SSH 使用与 Server 相同的用户名和密码"
        rule.setContent {
            MaterialTheme {
                Box(Modifier.width(220.dp)) {
                    LoginCheckboxRow(checked.value, label, true) { checked.value = it }
                }
            }
        }
        val checkbox = rule.onNodeWithTag("login-checkbox-control", useUnmergedTree = true).fetchSemanticsNode().boundsInRoot
        val text = rule.onNodeWithTag("login-checkbox-label", useUnmergedTree = true).fetchSemanticsNode().boundsInRoot
        assertTrue("Wrapped text and checkbox must have the same vertical center", abs(checkbox.center.y - text.center.y) < 1f)
        rule.onNodeWithText(label).performClick()
        rule.runOnIdle { assertTrue(checked.value) }
    }

    @Test fun reuseUsesServerIdentityAndPasswordWithoutMutatingInput() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val tunnel = LoginTunnelController(app.container)
        rule.runOnIdle {
            tunnel.host = "example.com"; tunnel.userName = "ssh-user"
            tunnel.secret = "private-key"; tunnel.passphrase = "key-passphrase"; tunnel.usePrivateKey = true
            tunnel.setReuseServerCredentials(true)
            assertFalse(tunnel.usePrivateKey)
            assertEquals("", tunnel.secret)
            assertEquals("server-user", tunnel.profile("http://127.0.0.1:5000", "server-user").userName)
            val sharedProfile = tunnel.profile("http://127.0.0.1:5000", "server-user")
            tunnel.select(sharedProfile)
            assertTrue("Saved shared profiles must restore the single-vault login path", tunnel.useServerCredentials)
            val password = "shared-password".toCharArray()
            val credential = tunnel.enteredCredential(password)!!
            assertEquals(SshCredentialKind.Password, credential.kind)
            assertArrayEquals(password, credential.secret)
            credential.clear()
            assertEquals("shared-password", String(password))
            password.fill('\u0000')
            try {
                tunnel.enteredCredential(charArrayOf())
                fail("Missing Server password must be rejected")
            } catch (error: LoginTunnelFailure) {
                assertEquals(R.string.login_tunnel_server_password_required, error.messageResource)
            }
            tunnel.setReuseServerCredentials(false)
            tunnel.userName = "ssh-user"
            tunnel.secret = "independent-password"
            assertEquals("ssh-user", tunnel.profile("http://127.0.0.1:5000", "server-user").userName)
            val independent = tunnel.enteredCredential(charArrayOf())!!
            assertEquals("independent-password", String(independent.secret))
            independent.clear()
            tunnel.secret = ""
        }
    }
}
