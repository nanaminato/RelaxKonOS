package app.relaxkonos.mobile.ui.connect

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.CertificateReview
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class CertificateConsentDialogTest {
    @get:Rule val rule = createComposeRule()

    @Test fun replacedWindowAnswersItsOwnRequestEvenWhenReviewValuesAreEqual() {
        val review = CertificateReview("https://test.invalid", "test-subject", "test-issuer", "A".repeat(64), null, "from", "until")
        val first = CertificateConsentRequest(review)
        val second = CertificateConsentRequest(review)
        val current = mutableStateOf<CertificateConsentRequest?>(first)
        val answers = mutableListOf<Pair<CertificateConsentRequest, Boolean>>()
        rule.setContent { MaterialTheme {
            Text("Parent page")
            current.value?.let { CertificateConsentDialog(it) { request, accepted ->
                answers += request to accepted; current.value = null
            } }
        } }
        rule.onNodeWithText("test-subject").assertIsDisplayed()
        rule.runOnIdle { current.value = second }
        val trust = InstrumentationRegistry.getInstrumentation().targetContext.getString(R.string.login_certificate_trust)
        rule.waitUntil(5_000) { rule.onNodeWithText(trust).isDisplayed() }
        rule.onNodeWithText(trust).performClick()
        rule.waitUntil(5_000) { rule.onAllNodesWithText(trust).fetchSemanticsNodes().isEmpty() }
        rule.runOnIdle { assertEquals(1, answers.size); assertSame(second, answers.single().first); assertTrue(answers.single().second) }
        rule.onNodeWithText("Parent page").assertIsDisplayed()
    }
}
