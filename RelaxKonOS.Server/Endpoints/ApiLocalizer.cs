using Microsoft.AspNetCore.Http;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Localizes API-owned metadata. User and file data is intentionally never translated.</summary>
internal static class ApiLocalizer
{
    public static string Get(HttpContext context, string key, string english)
    {
        var language = context.Request.GetTypedHeaders().AcceptLanguage?.FirstOrDefault()?.Value.Value ?? "en";
        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return key switch
            {
                "invalid-input" => "\u8f93\u5165\u65e0\u6548",
                "invalid-credential" => "\u51ed\u636e\u65e0\u6548",
                "account-locked" => "\u8d26\u6237\u5df2\u9501\u5b9a",
                "account-disabled" => "\u8d26\u6237\u5df2\u7981\u7528",
                "password-expired" => "\u5bc6\u7801\u5df2\u8fc7\u671f",
                "account-expired" => "\u8d26\u6237\u5df2\u8fc7\u671f",
                "account-restriction" => "\u8d26\u6237\u53d7\u9650",
                "auth-failed" => "\u8ba4\u8bc1\u5931\u8d25",
                "not-found" => "\u672a\u627e\u5230",
                "access-denied" => "\u8bbf\u95ee\u88ab\u62d2\u7edd",
                "invalid-path" => "\u8def\u5f84\u65e0\u6548",
                _ => english,
            };

        if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return key switch
            {
                "invalid-input" => "\u5165\u529b\u304c\u7121\u52b9\u3067\u3059",
                "invalid-credential" => "\u8cc7\u683c\u60c5\u5831\u304c\u7121\u52b9\u3067\u3059",
                "account-locked" => "\u30a2\u30ab\u30a6\u30f3\u30c8\u306f\u30ed\u30c3\u30af\u3055\u308c\u3066\u3044\u307e\u3059",
                "account-disabled" => "\u30a2\u30ab\u30a6\u30f3\u30c8\u306f\u7121\u52b9\u3067\u3059",
                "password-expired" => "\u30d1\u30b9\u30ef\u30fc\u30c9\u306e\u6709\u52b9\u671f\u9650\u304c\u5207\u308c\u3066\u3044\u307e\u3059",
                "account-expired" => "\u30a2\u30ab\u30a6\u30f3\u30c8\u306e\u6709\u52b9\u671f\u9650\u304c\u5207\u308c\u3066\u3044\u307e\u3059",
                "account-restriction" => "\u30a2\u30ab\u30a6\u30f3\u30c8\u306f\u5236\u9650\u3055\u308c\u3066\u3044\u307e\u3059",
                "auth-failed" => "\u8a8d\u8a3c\u306b\u5931\u6557\u3057\u307e\u3057\u305f",
                "not-found" => "\u898b\u3064\u304b\u308a\u307e\u305b\u3093",
                "access-denied" => "\u30a2\u30af\u30bb\u30b9\u304c\u62d2\u5426\u3055\u308c\u307e\u3057\u305f",
                "invalid-path" => "\u30d1\u30b9\u304c\u7121\u52b9\u3067\u3059",
                _ => english,
            };

        return english;
    }
}
