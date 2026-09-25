package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The wire helpers, asserted because each of them exists to protect a rule: [JsonBody] so a password is
 * never turned into a `String`, and [IsoInstant] so a timestamp the client cannot parse is omitted
 * rather than shown wrong.
 */
class WireTest {
    private fun body(builder: JsonBody): String = String(builder.toByteArray(), Charsets.UTF_8)

    @Test
    fun `a secret is written straight from the character array`() {
        assertEquals("""{"password":"s3cret"}""", body(JsonBody().secret("password", "s3cret".toCharArray())))
    }

    @Test
    fun `quotes, backslashes and newlines in a secret are escaped`() {
        assertEquals(
            """{"password":"a\"b\\c\nd"}""",
            body(JsonBody().secret("password", "a\"b\\c\nd".toCharArray())),
        )
    }

    @Test
    fun `a null secret is written as json null`() {
        assertEquals("""{"password":null}""", body(JsonBody().nullableSecret("password", null)))
        assertEquals("""{"password":null}""", body(JsonBody().nullableSecret("password", CharArray(0))))
    }

    @Test
    fun `a non-empty secret is written as a string`() {
        assertEquals("""{"password":"x"}""", body(JsonBody().nullableSecret("password", "x".toCharArray())))
    }

    @Test
    fun `a null string field is omitted`() {
        assertEquals("{}", body(JsonBody().string("capability", null)))
    }

    @Test
    fun `fields are separated by commas and keep their order`() {
        val built = JsonBody()
            .string("identifier", "nana")
            .secret("password", "x".toCharArray())
            .bool("includeDescendants", false)
            .int("page", 2)

        assertEquals("""{"identifier":"nana","password":"x","includeDescendants":false,"page":2}""", body(built))
    }

    @Test
    fun `control characters become unicode escapes`() {
        assertEquals("""{"value":"\u0001"}""", body(JsonBody().string("value", "\u0001")))
    }

    @Test
    fun `raw json is embedded unchanged`() {
        assertEquals("""{"relatedPaths":["/a","/b"]}""", body(JsonBody().raw("relatedPaths", """["/a","/b"]""")))
    }

    @Test
    fun `a zulu instant is parsed`() {
        assertEquals(0L, IsoInstant.toEpochMillis("1970-01-01T00:00:00Z"))
        // The expectation is derived from the platform calendar so the test does not depend on the
        // arithmetic of the parser under test.
        assertEquals(utcMillis(2026, 3, 22, 0, 0, 0), IsoInstant.toEpochMillis("2026-03-22T00:00:00Z"))
        assertEquals(utcMillis(2026, 3, 22, 13, 45, 30), IsoInstant.toEpochMillis("2026-03-22T13:45:30Z"))
    }

    @Test
    fun `fractional seconds are parsed`() {
        assertEquals(1_500L, IsoInstant.toEpochMillis("1970-01-01T00:00:01.500Z"))
        assertEquals(1_123L, IsoInstant.toEpochMillis("1970-01-01T00:00:01.123456Z"))
    }

    @Test
    fun `an explicit offset is applied`() {
        assertEquals(0L, IsoInstant.toEpochMillis("1970-01-01T08:00:00+08:00"))
        assertEquals(0L, IsoInstant.toEpochMillis("1969-12-31T16:00:00-08:00"))
    }

    @Test
    fun `a missing or unusable value yields null instead of a wrong instant`() {
        assertNull(IsoInstant.toEpochMillis(null))
        assertNull(IsoInstant.toEpochMillis(""))
        assertNull(IsoInstant.toEpochMillis("   "))
        assertNull(IsoInstant.toEpochMillis("2026-03-22"))
        assertNull(IsoInstant.toEpochMillis("not a timestamp"))
    }

    /** Independent expectation for [IsoInstant.toEpochMillis], built from the platform calendar. */
    private fun utcMillis(year: Int, month: Int, day: Int, hour: Int, minute: Int, second: Int): Long =
        java.util.Calendar.getInstance(java.util.TimeZone.getTimeZone("UTC")).apply {
            clear()
            set(year, month - 1, day, hour, minute, second)
        }.timeInMillis
}
