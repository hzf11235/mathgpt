using System.Net;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class DesktopSetupAudit
{
    private const string DeviceSecret = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    private const string FakeKey = "sk-fixture-desktop-setup-key";
    private const string PlanId = "0123456789abcdef0123456789abcdef";
    private sealed class Handler(string verification = "https://mathgpt.xyz/desktop/connect?code=ABCD-EFGH", bool reject = false) : HttpMessageHandler
    {
        public int Polls;
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.Host != "mathgpt.xyz" || request.RequestUri.Query.Length != 0)
                throw new InvalidOperationException("Setup sent a secret in a URL, used an unexpected host or HTTP method.");
            Paths.Add(request.RequestUri.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/start", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { success = true, data = new { device_code = DeviceSecret, user_code = "ABCD-EFGH", verification_uri = verification, expires_in = 600, interval = 3 } });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.GetProperty("device_code").GetString() != DeviceSecret)
                throw new InvalidOperationException("The device proof was not sent in the protected POST body.");
            Polls++;
            if (reject) return Reply(HttpStatusCode.Gone, new { success = false, error = "expired_token" });
            if (Polls == 1) return Reply(HttpStatusCode.Accepted, new { success = false, error = "authorization_pending" });
            return Reply(HttpStatusCode.OK, new { success = true, data = new { plan_id = PlanId, token_id = 123, token_name = "网页万能密钥", api_key = FakeKey,
                base_url = "https://mathgpt.xyz/v1", client = "codex", platform = "windows", model = "", reasoning = "", group = "universal" } });
        }
    }
    private sealed class CatalogHandler(Action? beforeReply = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri?.AbsoluteUri != "https://mathgpt.xyz/v1/models?mathgpt_catalog=1" || request.Headers.Authorization?.Parameter != FakeKey)
                throw new InvalidOperationException("Import attempted login, inference or an unscoped catalog.");
            var names = new[] { "gpt-6.1-sol", "claude-opus-5-5", "gemini-4-argon" };
            var pricing = names.Select(name => new { model_name = name, enable_groups = new[] { "universal" },
                group_endpoint_types = new { universal = new[] { name.StartsWith("claude", StringComparison.Ordinal) ? "anthropic" : "openai-response" } },
                group_pricing = new { universal = new { available = true, currency = "CNY", billing_mode = "token", markup_included = true, token_unit = 1000000,
                    rates = new { input_price = 1, output_price = 2 } } } });
            beforeReply?.Invoke();
            return Task.FromResult(Reply(HttpStatusCode.OK, new { data = names.Select(id => new { id }),
                mathgpt = new { schema_version = 1, group_verified = true, group = "universal", pricing = new { data = pricing } } }));
        }
    }
    private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    public static async Task Run(string root)
    {
        var handler = new Handler();
        using var client = new DesktopSetupClient(handler);
        var session = await client.Start();
        if (JsonSerializer.Serialize(session).Contains(DeviceSecret, StringComparison.Ordinal) || session.ToString().Contains(DeviceSecret, StringComparison.Ordinal))
            throw new InvalidOperationException("The device credential escaped its memory-only holder.");
        if (await client.Poll(session) is not null) throw new InvalidOperationException("A pending approval was treated as authorized.");
        var grant = await client.Poll(session) ?? throw new InvalidOperationException("The approved setup did not arrive.");
        if (grant.TokenId != 123 || grant.Model.Length != 0 || JsonSerializer.Serialize(grant).Contains(FakeKey, StringComparison.Ordinal) || grant.ToString().Contains(FakeKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The setup scope or secret serialization contract changed.");
        var store = new StateStore(root);
        var catalogHandler = new CatalogHandler();
        using var http = new HttpClient(catalogHandler);
        using var site = new WebsiteClient(store, fixtureHandler: catalogHandler);
        var catalog = new CatalogService(store, site, http);
        var imported = await DesktopSetupImporter.Import(store, catalog, grant);
        if (imported.Client != ClientKind.CodexDesktop || imported.ModelCount != 3 || store.Active.Group != "universal" || store.Active.Model != "gpt-6.1-sol" ||
            store.Key(store.Active) != FakeKey || store.Active.SetupPlanId != PlanId || store.State.PreferredClient != ClientKind.CodexDesktop)
            throw new InvalidOperationException("The web plan was not continued with its complete authorized model scope.");
        var id = store.Active.Id;
        await DesktopSetupImporter.Import(store, catalog, grant);
        if (store.State.Profiles.Count != 1 || store.Active.Id != id || File.ReadAllText(Path.Combine(root, "settings.json")).Contains(FakeKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Reconnecting duplicated the profile or stored a plaintext credential.");
        foreach (var uri in new[] { "https://example.com/desktop/connect?code=ABCD-EFGH", "http://mathgpt.xyz/desktop/connect?code=ABCD-EFGH", "https://mathgpt.xyz/other?code=ABCD-EFGH" })
        {
            using var bad = new DesktopSetupClient(new Handler(uri));
            try { await bad.Start(); throw new InvalidOperationException("A foreign verification page was accepted."); }
            catch (UserException) { }
        }
        using var revoked = new DesktopSetupClient(new Handler(reject: true));
        var expired = await revoked.Start();
        try { await revoked.Poll(expired); throw new InvalidOperationException("Expired approval was accepted."); }
        catch (UserException) { }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await client.Wait(session, cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        if (catalogHandler.Calls != 2) throw new InvalidOperationException("Import performed unexpected network work.");
        await CancellationBoundaries(Path.Combine(root, "cancel-import"), grant);
    }
    private static async Task CancellationBoundaries(string root, DesktopSetupGrant grant)
    {
        var store = new StateStore(root);
        var existing = new ConnectionProfile { Name = "保留的连接", WebsiteKeyId = grant.TokenId, Model = "gpt-6-astra",
            BaseUrl = "https://mathgpt.xyz/v1", Group = "original", Reasoning = "high" };
        store.SaveProfile(existing, "sk-fixture-original-import-key");
        var before = JsonSerializer.Serialize(store.State, JsonData.Options);
        var diskBefore = File.ReadAllBytes(Path.Combine(root, "settings.json"));
        var handler = new CatalogHandler();
        using var http = new HttpClient(handler);
        using var site = new WebsiteClient(store, fixtureHandler: new CatalogHandler());
        var catalog = new CatalogService(store, site, http);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await DesktopSetupImporter.Import(store, catalog, grant, canceled.Token); throw new InvalidOperationException("A canceled import completed."); }
        catch (OperationCanceledException) { }
        if (handler.Calls != 0 || JsonSerializer.Serialize(store.State, JsonData.Options) != before ||
            !File.ReadAllBytes(Path.Combine(root, "settings.json")).SequenceEqual(diskBefore) || store.Key(existing) != "sk-fixture-original-import-key")
            throw new InvalidOperationException("A pre-canceled import changed the active profile, saved key or settings.");

        var during = new StateStore(Path.Combine(root, "during-load"));
        during.State.PreferredClient = ClientKind.Harness;
        using var cancellation = new CancellationTokenSource();
        var pending = new CatalogHandler(cancellation.Cancel);
        using var pendingHttp = new HttpClient(pending);
        using var pendingSite = new WebsiteClient(during, fixtureHandler: new CatalogHandler());
        var pendingCatalog = new CatalogService(during, pendingSite, pendingHttp);
        try { await DesktopSetupImporter.Import(during, pendingCatalog, grant, cancellation.Token); throw new InvalidOperationException("A canceled catalog response completed the plan."); }
        catch (OperationCanceledException) { }
        var restored = new StateStore(during.Root);
        if (pending.Calls != 1 || restored.State.PreferredClient != ClientKind.Harness || restored.Active.Model.Length != 0 || restored.Active.LastSync.Length != 0 ||
            restored.Active.SetupPlanId != PlanId || restored.Key(restored.Active) != FakeKey)
            throw new InvalidOperationException("Cancellation applied a late model/client result or lost the resumable approved key.");
    }
}
