using System.Net;
using RelaxKonOS.ServerModule.CloudStorage;

var count = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
Check(GoogleDriveAuthorization.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "RFC 7636 challenge vector");
var state = GoogleDriveAuthorization.RandomValue();
Check(state.Length == 43 && state.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "state URL-safe 256-bit value");
Check(state != GoogleDriveAuthorization.RandomValue(), "state freshness");
var url = GoogleDriveAuthorization.Start("client", "https://example.test/api/cloud-storage/oauth/google/callback", state, "verifier", false);
Check(url.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?"), "official Google consent endpoint");
Check(url.Contains("code_challenge_method=S256") && url.Contains("access_type=offline") && url.Contains("prompt=consent"), "PKCE and offline consent");
Check(Uri.UnescapeDataString(url).Contains("/auth/drive.readonly"), "read-only scopes");
var handler = new Handler();
var provider = new GoogleDriveAuthorization((uri, priv) => { Check(uri.AbsoluteUri == "https://oauth2.googleapis.com/token" && !priv, "fixed token destination"); return new HttpClient(handler, false); });
handler.Body = "{\"refresh_token\":\"fixture-refresh\"}";
Check(await provider.ExchangeAsync("client", "fixture-secret", "https://example.test/callback", "fixture-code", "verifier", default) == "fixture-refresh", "refresh exchanged");
Check(handler.Form!.Contains("code_verifier=verifier") && handler.Form.Contains("client_secret=fixture-secret"), "server exchange contains PKCE and secret");
await provider.ExchangeAsync("client", "fixture-secret", "", "android-code", null, default);
Check(handler.Form!.Contains("redirect_uri=") && !handler.Form.Contains("code_verifier"), "Android server auth code exchange");
foreach (var body in new[] { "{}", "{\"refresh_token\":\"\"}", new string('x', 70000) }) {
    handler.Body = body; var rejected = false;
    try { await provider.ExchangeAsync("client", "secret", "", "code", null, default); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "reject incomplete or oversized response");
}
handler.Status = HttpStatusCode.BadRequest; handler.Body = "secret fixture provider error";
try { await provider.ExchangeAsync("client", "secret", "", "code", null, default); throw new Exception("accepted error"); }
catch (InvalidDataException e) { Check(!e.Message.Contains("secret"), "provider error redaction"); }
Console.WriteLine($"{count} checks passed");
sealed class Handler : HttpMessageHandler {
    internal string Body = "{}"; internal string? Form; internal HttpStatusCode Status = HttpStatusCode.OK;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        Form = await request.Content!.ReadAsStringAsync(ct);
        return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
    }
}
