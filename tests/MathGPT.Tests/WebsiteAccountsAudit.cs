using System.Net;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class WebsiteAccountsAudit
{
    public static async Task Run(string root)
    {
        var store = new StateStore(root);
        using var handler = new AccountsHandler();
        using var site = new WebsiteClient(store, "https://fixture.example", handler);
        void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); Console.WriteLine("PASS " + reason); }
        await site.Login("alice", "fixture-password");
        Require(site.Account?.Id == 1 && site.SavedAccounts.Count == 1, "first account is remembered after authoritative self lookup");
        Require(await site.CreateQuickKey("quick-key", "universal") == 42 && handler.QuickKeyScopeCorrect,
            "quick creation keeps every group model and follows the account balance without an artificial expiry");
        await site.Login("bob", "fixture-password");
        Require(site.Account?.Id == 2 && site.SavedAccounts.Count == 2, "adding a second account preserves the first session without logout");
        await site.SwitchAccount(1);
        Require(site.Account?.Id == 1 && handler.LastAccount == 1, "switch uses the selected account bearer");
        try { await site.Login("bad", "incorrect"); throw new InvalidOperationException("bad login accepted"); } catch (UserException) { }
        Require(site.Account?.Id == 1 && site.SavedAccounts.Count == 2, "failed added login restores the existing account");
        var challenge = await site.Login("2fa", "fixture-password");
        Require(challenge is not null && !site.SignedIn, "new login challenge does not use previous account credentials");
        site.CancelAccountLogin();
        Require(site.Account?.Id == 1, "cancelled login verification preserves previous login");
        handler.BlockBob = true;
        try { await site.SwitchAccount(2); throw new InvalidOperationException("revoked account accepted"); } catch (UserException) { }
        Require(site.Account?.Id == 1, "invalid saved account cannot replace the current session");
        handler.BlockBob = false;
        using var restored = new WebsiteClient(new StateStore(root), "https://fixture.example", new AccountsHandler());
        await restored.SwitchAccount(2);
        Require(restored.Account?.Id == 2 && restored.SavedAccounts.Count == 2, "encrypted saved accounts survive process recreation");
        using var foreign = new WebsiteClient(new StateStore(root), "https://other.example", new AccountsHandler());
        Require(foreign.SavedAccounts.Count == 0, "saved sessions cannot cross origins");
        await restored.Logout();
        Require(!restored.SignedIn && restored.SavedAccounts.Count == 1 && restored.SavedAccounts[0].Id == 1, "logout removes only the selected account");
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Require(files.All(p => !File.ReadAllText(p).Contains("fixture-account-access-", StringComparison.Ordinal)), "bearers are absent from plaintext files");
    }

    private sealed class AccountsHandler : HttpMessageHandler
    {
        public int LastAccount;
        public bool BlockBob;
        public bool QuickKeyScopeCorrect;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object data = new { };
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/status") data = new { quota_per_unit = 500000, turnstile_check = false };
            else if (path.EndsWith("encryption-key")) data = new { enabled = false };
            else if (path == "/api/user/login")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var name = body.RootElement.GetProperty("username").GetString();
                if (name == "bad") return Reply(HttpStatusCode.BadRequest, new { success = false });
                if (name == "2fa") data = new { require_verification = true, flow_token = "fixture-flow", methods = new[] { new { method = "2fa" } } };
                else data = new { access_token = "fixture-account-access-" + (name == "alice" ? 1 : 2), access_expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
            }
            else if (path == "/api/token/" && request.Method == HttpMethod.Post)
            {
                using var key = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var fields = key.RootElement;
                QuickKeyScopeCorrect = fields.GetProperty("group").GetString() == "universal" &&
                    !fields.GetProperty("model_limits_enabled").GetBoolean() && fields.GetProperty("model_limits").GetString() == "" &&
                    fields.GetProperty("unlimited_quota").GetBoolean() && fields.GetProperty("expired_time").GetInt32() == -1;
                data = new { id = 42 };
            }
            else if (path == "/api/user/self")
            {
                LastAccount = request.Headers.Authorization?.Parameter?.EndsWith("1") == true ? 1 : 2;
                if (LastAccount == 2 && BlockBob) return Reply(HttpStatusCode.Unauthorized, new { success = false });
                data = new { id = LastAccount, username = LastAccount == 1 ? "alice" : "bob", quota = 5000000, used_quota = 0 };
            }
            return Reply(HttpStatusCode.OK, new { success = true, data });
        }
        private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
