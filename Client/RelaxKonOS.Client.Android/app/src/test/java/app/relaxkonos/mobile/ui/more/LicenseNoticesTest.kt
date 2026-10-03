package app.relaxkonos.mobile.ui.more

import org.junit.Assert.assertEquals
import org.junit.Test

class LicenseNoticesTest {
    @Test
    fun componentsKeepTheirLicenseAndPurposeInSourceOrder() {
        val result = parseLicenseNotices("""
            # Notices
            Introduction

            ## Packages
            Package introduction

            | Package | License | Purpose |
            | --- | --- | --- |
            | `First` | MIT | UI |
            | Second | Apache-2.0 | Runtime |

            ### Special terms
            Keep these terms.
        """.trimIndent())
        assertEquals(listOf("Notices", "Packages", "First", "Second", "Special terms"), result.map { it.title })
        assertEquals(listOf("License：MIT", "Purpose：UI"), result[2].blocks)
        assertEquals(listOf("Keep these terms."), result.last().blocks)
    }

    @Test
    fun fencedLegalTextIsNotParsedAsHeadingsOrTables() {
        val result = parseLicenseNotices("## Component\n\n```\n# Legal text\n\n| retain this |\n```\n")
        assertEquals(1, result.size)
        assertEquals(listOf("# Legal text\n\n| retain this |"), result.single().blocks)
    }
}
