package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PasswordUserInfoTest {

    @Test
    fun `supplies the password to a single hidden keyboard interactive prompt`() {
        val userInfo = PasswordUserInfo("correct horse battery staple".toCharArray())

        val response = userInfo.promptKeyboardInteractive(
            destination = "deploy@example.test",
            name = "Password authentication",
            instruction = null,
            prompt = arrayOf("Password: "),
            echo = booleanArrayOf(false),
        )

        assertArrayEquals(arrayOf("correct horse battery staple"), response)
    }

    @Test
    fun `refuses keyboard interactive challenges it cannot answer safely`() {
        val userInfo = PasswordUserInfo("secret".toCharArray())

        assertNull(userInfo.promptKeyboardInteractive(null, null, null, arrayOf("OTP: "), booleanArrayOf(true)))
        assertNull(
            userInfo.promptKeyboardInteractive(
                null,
                null,
                null,
                arrayOf("Password: ", "OTP: "),
                booleanArrayOf(false, false),
            ),
        )
    }
}
