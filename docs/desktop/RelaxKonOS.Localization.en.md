# RelaxKonOS localization flow

RelaxKonOS uses BCP-47 language names (`en-US`, `zh-CN`, `ja-JP`) and English source strings/keys as the fallback baseline.

## Client text

`LocalizationService` owns the active language, loads JSON language packs from `Client/RelaxKonOS.Client/Localization`, and raises `LanguageChanged`. The required migration pattern is a stable key plus an English fallback through `LocalizationService.Get(key, englishFallback)`; AXAML binds to a localized view-model property and code-created controls use the same method. There is no visual-tree scan or source-sentence lookup, so a language change is handled only by the owner of each displayed value.

The login view uses the local language before authentication. `LocalLanguageStore` writes only that BCP-47 name below local application data. After authentication, `PreferencesSync` loads `WorkspacePreferencesDto.Language` for the current user-workspace; Settings writes subsequent changes to that workspace preference. Logging out restores the local login language.

## API text

Every typed HTTP client is wrapped by `AcceptLanguageHandler`, which adds the active language as `Accept-Language`. The server echoes its selected request language in `Content-Language`. API-owned presentation metadata, such as RFC 7807 `ProblemDetails` titles, is localized by `ApiLocalizer`; user/domain values such as usernames, file paths, bookmark names, and raw host error text are not translated or mutated.

Third-party packages receive `IExternalAppContext.SystemLanguage` and should localize their own resources, then refresh on `LanguageChanged`.
