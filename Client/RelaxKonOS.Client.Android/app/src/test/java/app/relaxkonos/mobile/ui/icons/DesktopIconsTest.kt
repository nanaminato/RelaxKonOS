package app.relaxkonos.mobile.ui.icons

import app.relaxkonos.mobile.R
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * File-type parity with the desktop Explorer.
 *
 * `DesktopIcons.fileFor` is a port of `ExplorerIconAssetResolver.ForEntry` in the desktop client. A
 * port that drifts is worse than no port — the two clients would then show a different glyph for the
 * same file — so the cases below pin the desktop's own ordering: name first, then the specific
 * extensions, then the category.
 */
class DesktopIconsTest {
    @Test
    fun `a directory is always a folder`() {
        assertEquals(R.drawable.ic_sys_file_folder, DesktopIcons.fileFor("src", isDirectory = true))
        assertEquals(R.drawable.ic_sys_file_folder, DesktopIcons.fileFor("archive.tar.gz", isDirectory = true))
    }

    @Test
    fun `a whole name can carry the meaning`() {
        assertEquals(R.drawable.ic_sys_file_dockerfile, DesktopIcons.fileFor("Dockerfile", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_makefile, DesktopIcons.fileFor("Makefile", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_cmake, DesktopIcons.fileFor("CMakeLists.txt", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_package, DesktopIcons.fileFor("package.json", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_git_config, DesktopIcons.fileFor(".gitignore", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_env, DesktopIcons.fileFor(".env", isDirectory = false))
    }

    @Test
    fun `a language extension gets its own artwork`() {
        assertEquals(R.drawable.ic_sys_file_kotlin, DesktopIcons.fileFor("Main.kt", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_kotlin, DesktopIcons.fileFor("build.gradle.kts", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_csharp, DesktopIcons.fileFor("Program.cs", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_python, DesktopIcons.fileFor("tool.py", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_react, DesktopIcons.fileFor("App.tsx", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_dotnet_project, DesktopIcons.fileFor("App.csproj", isDirectory = false))
    }

    @Test
    fun `an unmapped extension falls into its category`() {
        assertEquals(R.drawable.ic_sys_file_document, DesktopIcons.fileFor("README.md", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_spreadsheet, DesktopIcons.fileFor("sheet.csv", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_image, DesktopIcons.fileFor("photo.PNG", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_video, DesktopIcons.fileFor("clip.mkv", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_archive, DesktopIcons.fileFor("bundle.tar.gz", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_application, DesktopIcons.fileFor("setup.msi", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_database, DesktopIcons.fileFor("data.sqlite3", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_config, DesktopIcons.fileFor("service.log", isDirectory = false))
    }

    @Test
    fun `an extension-less source file counts as code`() {
        assertEquals(R.drawable.ic_sys_file_code_generic, DesktopIcons.fileFor("Rakefile", isDirectory = false))
    }

    @Test
    fun `anything else is a generic file`() {
        assertEquals(R.drawable.ic_sys_file_generic, DesktopIcons.fileFor("notes.unknownext", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_generic, DesktopIcons.fileFor("LICENSE", isDirectory = false))
        // A dotfile is a name, not an extension; the desktop leaves it generic too.
        assertEquals(R.drawable.ic_sys_file_generic, DesktopIcons.fileFor(".bashrc", isDirectory = false))
    }

    @Test
    fun `case and surrounding whitespace do not change the answer`() {
        assertEquals(R.drawable.ic_sys_file_kotlin, DesktopIcons.fileFor("  MAIN.KT  ", isDirectory = false))
        assertEquals(R.drawable.ic_sys_file_dockerfile, DesktopIcons.fileFor("dockerfile", isDirectory = false))
    }
}
