package app.relaxkonos.mobile.ui.icons

import androidx.annotation.DrawableRes
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R

/**
 * The desktop client's icon set, addressed by meaning.
 *
 * ## Why these are bitmaps rather than vectors
 *
 * A user who moves between the desktop client and the phone should not have to learn two icon
 * languages, so Android does not draw an icon set of its own. The artwork lives in
 * `Client/RelaxKonOS.Client/Assets` and is mirrored into `res/drawable-nodpi` by
 * `Tools/Mobile/sync-desktop-icons.py`; this object is the only place that maps an Android meaning onto
 * one of those files, which is what keeps the two clients in step and keeps a rename in one place.
 *
 * ## Two groups, used for two different things
 *
 * The desktop partitions the same way, and the split is worth keeping:
 *
 *  - `ic_app_*` are self-contained rounded-square tiles (the desktop's dock, launchpad and window
 *    marks). They answer "which destination is this", so they are used for the shell's top-level
 *    destinations and the product mark — never inside a page, where they would nest inside the badge
 *    the row already draws.
 *  - `ic_sys_*` are transparent glyphs (the desktop Explorer's toolbar and file types). They answer
 *    "which row or which action is this", so they are what every list row, header and button uses.
 *
 * ## Where the desktop set has no counterpart
 *
 * Two cases are worth knowing about rather than rediscovering:
 *
 *  - **Upload and download.** The desktop keeps both in a menu with no icons, so there is nothing to
 *    copy. Android borrows the set's only up arrow for upload and its only inbound arrow for download.
 *  - **Reveal password.** The desktop login has no reveal control at all, so `ic_password_visible` /
 *    `ic_password_hidden` remain the app's own vectors.
 */
object DesktopIcons {

    // ---- Product mark -------------------------------------------------

    /** The mark the desktop uses for its window, tray and taskbar entries. */
    @DrawableRes
    val brand = R.drawable.ic_app_brand

    // ---- Top-level destinations ---------------------------------------

    @DrawableRes
    val navHome = R.drawable.ic_app_welcome

    @DrawableRes
    val navFiles = R.drawable.ic_app_explorer

    @DrawableRes
    val navManage = R.drawable.ic_app_taskmanager

    @DrawableRes
    val navMore = R.drawable.ic_app_settings

    @DrawableRes
    val navTerminal = R.drawable.ic_app_terminal

    // ---- Page headers and settings rows -------------------------------

    /** The host this session is on; leads the home identity panel. */
    @DrawableRes
    val host = R.drawable.ic_app_webservers

    @DrawableRes
    val system = R.drawable.ic_sys_navigation_computer

    @DrawableRes
    val deployments = R.drawable.ic_app_application_deployments

    @DrawableRes
    val storage = R.drawable.ic_sys_navigation_drive

    @DrawableRes
    val connections = R.drawable.ic_sys_navigation_network

    /** The vaults and the fingerprint switch; the desktop Explorer draws `.env` files the same way. */
    @DrawableRes
    val credentials = R.drawable.ic_sys_file_env

    @DrawableRes
    val capabilities = R.drawable.ic_sys_toolbar_list_view

    /** Recorded operations; the desktop's lined document reads as a log. */
    @DrawableRes
    val history = R.drawable.ic_sys_file_config

    @DrawableRes
    val processes = R.drawable.ic_sys_toolbar_list_view

    @DrawableRes
    val diagnostics = R.drawable.ic_sys_toolbar_info

    @DrawableRes
    val appearance = R.drawable.ic_sys_toolbar_settings

    @DrawableRes
    val about = R.drawable.ic_sys_file_document

    /** "Nothing here" and "the server cannot serve this" placeholders. */
    @DrawableRes
    val notice = R.drawable.ic_sys_toolbar_info

    // ---- Actions ------------------------------------------------------

    @DrawableRes
    val refresh = R.drawable.ic_sys_toolbar_refresh

    @DrawableRes
    val search = R.drawable.ic_sys_toolbar_search

    @DrawableRes
    val newFolder = R.drawable.ic_sys_toolbar_new_folder

    @DrawableRes
    val rename = R.drawable.ic_sys_toolbar_rename

    @DrawableRes
    val delete = R.drawable.ic_sys_toolbar_delete

    @DrawableRes
    val copy = R.drawable.ic_sys_toolbar_copy

    /** Moving an entry is cutting it from where it is. */
    @DrawableRes
    val move = R.drawable.ic_sys_toolbar_cut

    /** No dedicated artwork exists; see the class note above. */
    @DrawableRes
    val upload = R.drawable.ic_sys_toolbar_up

    /** No dedicated artwork exists; see the class note above. */
    @DrawableRes
    val download = R.drawable.ic_sys_toolbar_forward

    @DrawableRes
    val back = R.drawable.ic_sys_toolbar_back

    /** The chevron on a row that opens something. */
    @DrawableRes
    val disclosure = R.drawable.ic_sys_toolbar_forward

    @DrawableRes
    val parentDirectory = R.drawable.ic_sys_toolbar_up

    @DrawableRes
    val overflow = R.drawable.ic_sys_toolbar_more

    @DrawableRes
    val signOut = R.drawable.ic_sys_toolbar_forward

    // ---- File system --------------------------------------------------

    @DrawableRes
    val folder = R.drawable.ic_sys_file_folder

    @DrawableRes
    val file = R.drawable.ic_sys_file_generic

    /**
     * The glyph a directory entry is shown with.
     *
     * A port of `ExplorerIconAssetResolver.ForEntry` in the desktop client, so a `.kt` file looks the
     * same on the phone as it does on the desktop. Two passes, in the desktop's order: a handful of
     * names and extensions get their own artwork, and everything else falls into a category. Keeping
     * the same two passes is what stops the two clients from drifting apart on a file type.
     */
    @DrawableRes
    fun fileFor(name: String, isDirectory: Boolean): Int {
        if (isDirectory) {
            return folder
        }
        val normalized = name.trim().lowercase()
        return namedFile(normalized) ?: extensionFile(normalized) ?: categoryFile(normalized)
    }

    /** Files whose whole name carries the meaning, before any extension is considered. */
    @DrawableRes
    private fun namedFile(name: String): Int? = when (name) {
        "dockerfile" -> R.drawable.ic_sys_file_dockerfile
        "makefile" -> R.drawable.ic_sys_file_makefile
        "cmakelists.txt" -> R.drawable.ic_sys_file_cmake
        "package.json", "package-lock.json", "composer.json", "gemfile" -> R.drawable.ic_sys_file_package
        ".gitignore", ".gitattributes", ".gitmodules" -> R.drawable.ic_sys_file_git_config
        ".env", ".env.local", ".env.production" -> R.drawable.ic_sys_file_env
        else -> null
    }

    /** Extensions the desktop gives language- or format-specific artwork. */
    @DrawableRes
    private fun extensionFile(name: String): Int? = when (extensionOf(name)) {
        ".c", ".h" -> R.drawable.ic_sys_file_c
        ".cpp", ".cxx", ".cc", ".hpp", ".hxx" -> R.drawable.ic_sys_file_cpp
        ".cs", ".csx" -> R.drawable.ic_sys_file_csharp
        ".fs", ".fsx", ".fsi" -> R.drawable.ic_sys_file_fsharp
        ".vb" -> R.drawable.ic_sys_file_visual_basic
        ".java" -> R.drawable.ic_sys_file_java
        ".kt", ".kts" -> R.drawable.ic_sys_file_kotlin
        ".go" -> R.drawable.ic_sys_file_go
        ".rs" -> R.drawable.ic_sys_file_rust
        ".py", ".pyw" -> R.drawable.ic_sys_file_python
        ".js", ".mjs", ".cjs" -> R.drawable.ic_sys_file_javascript
        ".ts" -> R.drawable.ic_sys_file_typescript
        ".tsx", ".jsx" -> R.drawable.ic_sys_file_react
        ".html", ".htm" -> R.drawable.ic_sys_file_html
        ".css" -> R.drawable.ic_sys_file_css
        ".scss", ".sass" -> R.drawable.ic_sys_file_sass
        ".less" -> R.drawable.ic_sys_file_css_alt
        ".php" -> R.drawable.ic_sys_file_php
        ".rb" -> R.drawable.ic_sys_file_ruby
        ".swift" -> R.drawable.ic_sys_file_swift
        ".dart" -> R.drawable.ic_sys_file_dart
        ".sh", ".bash", ".zsh", ".fish" -> R.drawable.ic_sys_file_shell
        ".ps1", ".psm1" -> R.drawable.ic_sys_file_powershell
        ".sql" -> R.drawable.ic_sys_file_sql
        ".json" -> R.drawable.ic_sys_file_json
        ".xml", ".xaml" -> R.drawable.ic_sys_file_xml
        ".yml", ".yaml" -> R.drawable.ic_sys_file_yaml
        ".toml" -> R.drawable.ic_sys_file_toml
        ".ini", ".cfg", ".conf", ".properties" -> R.drawable.ic_sys_file_config
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx" -> R.drawable.ic_sys_file_dotnet_project
        else -> null
    }

    /** Everything else, grouped the way `ExplorerFileIconKindResolver` groups it. */
    @DrawableRes
    private fun categoryFile(name: String): Int = when (extensionOf(name)) {
        ".doc", ".docx", ".odt", ".rtf", ".pages", ".epub", ".mobi",
        ".txt", ".md", ".markdown", ".rst", ".tex" -> R.drawable.ic_sys_file_document

        ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv", ".tsv" -> R.drawable.ic_sys_file_spreadsheet
        ".ppt", ".pptx", ".pps", ".ppsx", ".odp", ".key" -> R.drawable.ic_sys_file_presentation
        ".pdf" -> R.drawable.ic_sys_file_pdf

        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
        ".tif", ".tiff", ".heic", ".avif", ".raw", ".psd" -> R.drawable.ic_sys_file_image

        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".aiff" -> R.drawable.ic_sys_file_audio
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpeg", ".mpg" -> R.drawable.ic_sys_file_video

        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst", ".cab", ".jar", ".war", ".whl" ->
            R.drawable.ic_sys_file_archive

        ".exe", ".msi", ".msix", ".appx", ".appimage", ".deb", ".rpm", ".apk", ".dmg", ".pkg" ->
            R.drawable.ic_sys_file_application

        ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".qcow", ".qcow2" -> R.drawable.ic_sys_file_disk_image

        ".cs", ".csx", ".fs", ".vb", ".c", ".h", ".cpp", ".cxx", ".hpp", ".java", ".kt", ".kts",
        ".go", ".rs", ".py", ".rb", ".php", ".swift", ".scala", ".sh", ".bash", ".ps1", ".bat", ".cmd",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".html", ".htm", ".css",
        ".scss", ".sass", ".less", ".sql" -> R.drawable.ic_sys_file_code_generic

        ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".env", ".log" ->
            R.drawable.ic_sys_file_config

        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".dbf" -> R.drawable.ic_sys_file_database
        ".ttf", ".otf", ".woff", ".woff2", ".eot" -> R.drawable.ic_sys_file_font

        else -> if (name in SOURCE_NAMES) R.drawable.ic_sys_file_code_generic else file
    }

    /** Extension-less source files the desktop also treats as code. */
    private val SOURCE_NAMES = setOf(
        "dockerfile",
        "makefile",
        "cmakelists.txt",
        "gemfile",
        "rakefile",
        "procfile",
    )

    /** `ExplorerPath.Extension`: everything from the last dot, or nothing when there is none. */
    private fun extensionOf(name: String): String {
        val index = name.lastIndexOf('.')
        return if (index < 0) "" else name.substring(index)
    }
}

/**
 * Draws one desktop asset.
 *
 * `Image` rather than `Icon`, and never a tint: the artwork carries its own colours and its own shape,
 * so a theme colour applied on top would flatten it into a silhouette. Sizing is the caller's job
 * because the set is used from 16dp inline up to 40dp in a panel.
 */
@Composable
fun DesktopIcon(
    @DrawableRes icon: Int,
    modifier: Modifier = Modifier,
    size: Dp = 24.dp,
    contentDescription: String? = null,
) {
    Image(
        painter = painterResource(icon),
        contentDescription = contentDescription,
        modifier = modifier.size(size),
    )
}
