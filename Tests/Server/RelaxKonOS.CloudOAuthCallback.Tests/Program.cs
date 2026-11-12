using Microsoft.AspNetCore.Http;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Server.Endpoints;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}
DefaultHttpContext Context(string query)
{
    var http = new DefaultHttpContext();
    http.Request.Method = "GET";
    http.Request.QueryString = new QueryString(query);
    return http;
}
var state = new string('a', 43);
var valid = $"?state={state}&code=4%2Fabc%2Bdef%3D";
var context = Context(valid + "&scope=secret&authuser=1&owner=attacker&path=https://attacker.invalid/&prompt=consent");
context.Request.Headers.Authorization = "Bearer attacker";
context.Request.Headers.Cookie = "session=attacker";
context.Request.Headers["X-RelaxKonOS-Module-User"] = "attacker";
context.Request.Headers["X-RelaxKonOS-Module-Role"] = "admin";
Check(CloudOAuthCallbackRequest.TryRead(context.Request, out var filtered), "valid callback accepted");
Check(filtered.Value == valid, "only code and state are relayed and safely escaped");
using (var request = CloudOAuthCallbackRequest.Create(new Uri("http://127.0.0.1:12345/"), "test-service-token", filtered))
{
    Check(request.Method == HttpMethod.Get, "method is fixed");
    Check(request.RequestUri!.AbsolutePath == "/cloud/authorization/google/callback", "internal path is fixed");
    Check(request.RequestUri.Host == "127.0.0.1", "cannot select destination");
    Check(request.Headers.Count() == 1, "no browser identity or authorization headers forwarded");
    Check(request.Headers.GetValues(ModuleHostProtocol.ServiceTokenHeader).Single() == "test-service-token", "internal service identity set");
    Check(request.Content is null, "no body forwarded");
}
foreach (var query in new[]
{
    "", "?code=x", $"?state={state}", $"?state={state}&code=x&error=access_denied",
    $"?state={state}&code=x&code=y", $"?state={state}&state={state}&code=x",
    $"?state={state}&error=x&error=y", "?state=short&code=x", $"?state={state}%2F&code=x",
    $"?state={state}&code=", $"?state={state}&error=", $"?state={state}&code=a%0D%0Ab",
    $"?state={state}&code=a%20b", $"?state={state}&error=https%3A%2F%2Fevil.invalid",
    $"?state={new string('a', 129)}&code=x", $"?state={state}&code={new string('b', 8193)}",
    $"?state={state}&code=x&extra={new string('a', 16384)}"
}) Check(!CloudOAuthCallbackRequest.TryRead(Context(query).Request, out _), "reject malformed callback " + checks);
Check(CloudOAuthCallbackRequest.TryRead(Context($"?state={state}&error=access_denied").Request, out _), "provider denial accepted");
context = Context(valid);
context.Request.Method = "POST";
Check(!CloudOAuthCallbackRequest.TryRead(context.Request, out _), "POST rejected");
context = Context(valid);
context.Request.ContentLength = 1;
Check(!CloudOAuthCallbackRequest.TryRead(context.Request, out _), "body rejected");
context = Context(valid);
context.Request.Headers.TransferEncoding = "chunked";
Check(!CloudOAuthCallbackRequest.TryRead(context.Request, out _), "chunked body rejected");
CloudOAuthCallbackRequest.Harden(context.Response);
Check(context.Response.Headers.CacheControl == "no-store", "callback cannot be cached");
Check(context.Response.Headers["Referrer-Policy"] == "no-referrer", "callback URL cannot leak by referrer");
Check(context.Response.Headers["Content-Security-Policy"] == "default-src 'none'; frame-ancestors 'none'", "callback is inert");
Console.WriteLine($"PASS: {checks} cloud OAuth callback boundary checks.");
