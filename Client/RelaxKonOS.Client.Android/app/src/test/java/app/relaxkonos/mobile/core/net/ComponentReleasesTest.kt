package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

class ComponentReleasesTest {
    private fun github(draft: Boolean = false, pre: Boolean = false, name: String = "frp_0.70.0_linux_arm64.tar.gz", url: String = "https://github.com/fatedier/frp/releases/download/v0.70.0/$name") =
        """[{"draft":$draft,"prerelease":$pre,"tag_name":"v0.70.0","assets":[{"name":"$name","browser_download_url":"$url"}]}]"""
    @Test fun `github matches host architecture and fixed official asset`() {
        assertEquals("v0.70.0", ComponentReleases.github(github(), "frpc", "linux-arm64").single().version)
        assertTrue(ComponentReleases.github(github(), "frps", "linux-x64").isEmpty())
        assertTrue(ComponentReleases.github(github(url = "https://evil.invalid/archive"), "frpc", "linux-arm64").isEmpty())
    }
    @Test fun `drafts and prereleases are excluded`() {
        assertTrue(ComponentReleases.github(github(draft = true), "frpc", "linux-arm64").isEmpty())
        assertTrue(ComponentReleases.github(github(pre = true), "frpc", "linux-arm64").isEmpty())
    }
    @Test fun `nginx discovers windows archives and deduplicates versions`() {
        val html = """<a href="/download/nginx-1.30.2.zip">one</a><a href="https://nginx.org/download/nginx-1.30.2.zip">two</a>"""
        assertEquals("1.30.2", ComponentReleases.nginx(html, "win-x64").single().version)
    }
    @Test fun `comparison is numeric and excludes same version downgrade and unknown current`() {
        assertTrue(ComponentReleases.newer("v0.100.0", "0.99.9"))
        assertFalse(ComponentReleases.newer("v0.70.0", "0.70.0"))
        assertFalse(ComponentReleases.newer("v0.69.0", "v0.70.0"))
        assertFalse(ComponentReleases.newer("v0.70.0", null))
        assertFalse(ComponentReleases.newer("v0.70.0-rc1", "0.69.0"))
    }
    @Test(expected = IllegalArgumentException::class) fun `server response cannot change platform`() {
        ComponentReleases.server("""[{"version":"v0.70.0","rid":"win-x64","fileName":"frp_0.70.0_linux_arm64.tar.gz","downloadUrl":"https://github.com/fatedier/frp/releases/download/v0.70.0/frp_0.70.0_linux_arm64.tar.gz"}]""", "frpc", "linux-arm64")
    }
}
