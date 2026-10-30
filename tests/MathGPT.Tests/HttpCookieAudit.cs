using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class HttpCookieAudit
{
    public static async Task Run(string directory)
    {
        var guard = new TcpListener(IPAddress.Loopback, 0); guard.Start();
        var port = ((IPEndPoint)guard.LocalEndpoint).Port; guard.Stop();
        var origin = $"http://127.0.0.1:{port}";
        using var listener = new HttpListener(); listener.Prefixes.Add(origin + "/"); listener.Start();
        using var cancellation = new CancellationTokenSource();
        var refreshed = 0; var originChecked = false;
        var serving = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().WaitAsync(cancellation.Token); }
                catch (OperationCanceledException) { return; }
                var request = context.Request;
                var path = request.Url!.AbsolutePath;
                var cookie = request.Headers["Cookie"] ?? "";
                string body;
                if (path == "/") body = "<html><form><input name=\"csrf\" value=\"fixture-nonce\"></form></html>";
                else if (path == "/__entry")
                {
                    using var reader = new StreamReader(request.InputStream);
                    var text = await reader.ReadToEndAsync();
                    if (!text.Contains("entry_password=fixture-entry-pass") || !text.Contains("csrf=fixture-nonce"))
                    { context.Response.StatusCode = 403; body = "{}"; }
                    else
                    {
                        context.Response.Headers.Add("Set-Cookie", "__Host-mathgpt-entry=granted; Path=/; HttpOnly; SameSite=Lax");
                        context.Response.StatusCode = 303; context.Response.RedirectLocation = "/"; body = "";
                    }
                }
                else if (path == "/api/status")
                {
                    if (!cookie.Contains("__Host-mathgpt-entry=granted")) { context.Response.StatusCode = 403; body = "{}"; }
                    else body = """{"success":true,"data":{"quota_per_unit":500000}}""";
                }
                else if (path.EndsWith("/login/encryption-key")) body = """{"success":true,"data":{"enabled":false}}""";
                else if (path == "/api/user/login")
                {
                    context.Response.Headers.Add("Set-Cookie", "new_api_refresh=fixture-cookie; Path=/api/user/auth; HttpOnly");
                    body = JsonSerializer.Serialize(new { success = true, data = new { access_token = "fixture-expired",
                        access_expires_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds() } });
                }
                else if (path.EndsWith("/auth/refresh"))
                {
                    if (!cookie.Contains("new_api_refresh=fixture-cookie")) throw new InvalidOperationException("refresh cookie path not respected");
                    originChecked = request.Headers["Origin"] == origin; refreshed++;
                    body = JsonSerializer.Serialize(new { success = true, data = new { access_token = "fixture-fresh",
                        access_expires_at = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() } });
                }
                else if (path == "/api/user/self")
                {
                    if (request.Headers["Authorization"] != "Bearer fixture-fresh") throw new InvalidOperationException("expired bearer used");
                    body = """{"success":true,"data":{"id":8,"username":"fixture","quota":1500000}}""";
                }
                else body = """{"success":true}""";
                context.Response.ContentType = path == "/" ? "text/html" : "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        });
        var store = new StateStore(directory);
        try
        {
            using (var site = new WebsiteClient(store, origin))
            {
                await site.UnlockEntry("fixture-entry-pass");
                await site.Login("fixture", "fixture-password");
                if (site.Account?.Balance != 3m || refreshed != 1 || !originChecked) throw new InvalidOperationException("wire refresh/session failed");
            }
            using (var restored = new WebsiteClient(new StateStore(directory), origin))
            {
                var user = await restored.RefreshAccount();
                if (user.Balance != 3m) throw new InvalidOperationException("DPAPI session did not restore");
                await restored.Logout();
            }
        }
        finally
        {
            cancellation.Cancel(); listener.Stop(); await serving;
        }
    }
}
