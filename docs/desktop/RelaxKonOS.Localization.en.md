# RelaxKonOS localization flow

RelaxKonOS uses BCP-47 language names (`en-US`, `zh-CN`, `ja-JP`) and English source strings/keys as the fallback baseline.

## Client text

`LocalizationService` owns the active language, loads JSON language packs from `Client/RelaxKonOS.Client/Localization`, and raises `LanguageChanged`. The required migration pattern is a stable key plus an English fallback through `LocalizationService.Get(key, englishFallback)`; AXAML binds to a localized view-model property and code-created controls use the same method. There is no visual-tree scan or source-sentence lookup, so a language change is handled only by the owner of each displayed value.

The login view uses the local language before authentication. `LocalLanguageStore` writes only that BCP-47 name below local application data. After authentication, `PreferencesSync` loads `WorkspacePreferencesDto.Language` for the current user-workspace; Settings writes subsequent changes to that workspace preference. Alongside the three explicit languages, Settings offers the synchronized `follow-system` value: Chinese system languages resolve to `zh-CN`, Japanese to `ja-JP`, and all other languages to `en-US`. SSH desktops always use that same device-local mapping and never read or write a workspace preference. Logging out restores the local login language.

## Resource-key verification

Besides requiring identical key sets across the three languages, `Tools/verify-localization.py` requires **every key the client
source asks for to exist in `en-US`**. It scans the `.cs` and `.axaml` files under `Client/RelaxKonOS.Client/**` and recognises
`{loc:Loc …}`, `LocalizedText.Get/Ref/RefWithFallback/Format`, `LocalizedStatus.Key/Format`, `xxxLocalization.Get`, and the
helpers that take only the last segment and prepend their own namespace (`ComponentObservationText.Key`,
`TunnelProblemText.Format`). Keys composed by concatenation (`"component_center.badge." + kind`, `"login.local_manage_" + key`)
cannot be resolved statically; only their prefix is required to match an existing key.

Run it after adding, renaming, or removing a resource key:

```bash
python Tools/verify-localization.py
```

The same script also checks that the **format arguments** line up across the three languages (`{0}` at the end of a string counts,
and format specifiers such as `{0:yyyy-MM-dd}` are recognised), so a translation that drops a placeholder fails instead of
silently losing a value at runtime.

`LocalizationResourceChecks` in `Tests/Client/RelaxKonOS.WindowPreviews.Tests` mirrors those rules inside the test suite
(run with `--project-editor-only`): it compares the **key sets of the three languages** (a missing or stray key throws and lists
the first ten), compares format arguments, and proves that the 20 `code_editor.context.*` entries load in each active language.
It reads the packs from the build output directory, so it only covers what actually shipped; whether a key referenced by the
source exists is still something only the python script can answer.

A mistyped key does not fail the build, and where python is unavailable it does not break an existing test either — the UI just
shows `[missing: <key>]` (AXAML) or the key itself (`LocalizedText.Get(key)`). The existence check is the python script's job;
do not bypass it when adding call sites.

## API text

Every typed HTTP client is wrapped by `AcceptLanguageHandler`, which adds the active language as `Accept-Language`. The server echoes its selected request language in `Content-Language`. API-owned presentation metadata, such as RFC 7807 `ProblemDetails` titles, is localized by `ApiLocalizer`; user/domain values such as usernames, file paths, bookmark names, and raw host error text are not translated or mutated.

`ApiLocalizer` is used only for the authentication titles the server renders itself (11 keys, call site in `AuthEndpoints`). Every other endpoint uses the stable problem code as `title` (`title: code`) and repeats that code in the `type` suffix and the `problemCode` extension; `detail` is an English diagnostic, not a sentence for the user. The client maps the code to its own localized copy and only shows `detail` in diagnostics.

Server source therefore contains no Chinese user-facing text: new endpoints pass `title: code`, and exception messages are English. Audit with `grep -P '[\x{4e00}-\x{9fff}]' RelaxKonOS.Server/**/*.cs`; matches are only allowed in comments and in `Settings/ApplicationCatalog`'s multi-language labels and search keywords.

Third-party packages receive `IExternalAppContext.SystemLanguage` and should localize their own resources, then refresh on `LanguageChanged`.
