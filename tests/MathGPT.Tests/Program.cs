using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class Program
{
    private static readonly List<object> results = [];
    private static string root = "";
    private static int failed;
    public static async Task<int> Main(string[] args)
    {
        root = Path.GetFullPath(args.Length > 0 ? args[0] : "work/tests");
        Directory.CreateDirectory(root);
        if (args.Contains("--authenticode"))
        {
            await AuthenticodeAudit.Run(root, args.Skip(Array.IndexOf(args, "--authenticode") + 1).ToArray());
            return 0;
        }
        if (args.Contains("--seed-upgrade"))
        {
            var state = new StateStore(root);
            state.State.WorkDirectory = Path.Combine(root, "工作区 中文 空格");
            Directory.CreateDirectory(state.State.WorkDirectory);
            var p = new ConnectionProfile { Name = "安装验收夹具", Model = "gpt-6.1-sol", BaseUrl = "http://127.0.0.1:41890/v1" };
            state.SaveProfile(p, "fixture-upgrade-private-key");
            state.State.SchemaVersion = 1; state.Save(); // Real legacy on-disk schema; the new reader must preserve it.
            File.WriteAllText(Path.Combine(state.State.WorkDirectory, "保留产物.md"), "安装升级验收夹具。", new UTF8Encoding(false));
            Console.WriteLine("Seeded isolated upgrade fixtures."); return 0;
        }
        if (args.Contains("--desktop-mirror")) return await DesktopMirrorAudit.Run(root);
        if (args.Contains("--accounts")) { await WebsiteAccountsAudit.Run(root); return 0; }
        if (args.Contains("--codex-isolation")) { await CodexDesktopIsolationAudit.Run(root); return 0; }
        if (args.Contains("--claude-harness-isolation")) { await ClaudeHarnessIsolationAudit.Run(root); return 0; }
        if (args.Contains("--self-update")) { await SelfUpdateAudit.Run(Path.Combine(root, "self-update-" + Guid.NewGuid().ToString("N"))); return 0; }
        if (args.Contains("--claude-readiness")) { await ClaudeReadinessAudit.Run(root); return 0; }
        if (args.Contains("--packages")) return await PackageAudit.Run(root);
        if (args.Contains("--native-clients")) return await NativeClientAudit.Run(root);
        if (args.Contains("--desktop-packages")) return await DesktopPackageAudit.Run(root);
        if (args.Contains("--local-deploy-state"))
        {
            var local = new StateStore(@"D:\Codex\Data\mathgpt-desktop");
            if (!File.Exists(Path.Combine(local.Root, "settings.json")))
            {
                local.State.DownloadDirectory = Path.Combine(local.Root, "downloads");
                local.State.InstallDirectory = @"D:\Codex\Apps\mathgpt-clients\官方 客户端";
                local.State.WorkDirectory = @"D:\Codex\Documents\MathGPT\Workspaces";
            }
            Directory.CreateDirectory(local.State.WorkDirectory);
            var audited = new StateStore(Path.Combine(root, "state"));
            foreach (var client in audited.State.Clients)
            {
                if (local.State.Clients.Any(c => c.Kind == client.Kind)) continue;
                client.ProfileId = ""; client.ConfigDirectory = ""; client.WorkDirectory = local.State.WorkDirectory;
                local.State.Clients.Add(client);
            }
            local.Save();
            Console.WriteLine($"Local data initialized: {local.State.Clients.Count} clients, {local.State.Profiles.Count} profiles.");
            return 0;
        }
        await Check("DPAPI round trip and plaintext exclusion", Vault);
        await Check("HTTPS, origin, path traversal and reparse guards", Guard);
        await Check("final CNY rates and group-specific protocol fail closed", Pricing);
        await Check("configuration isolation, secrets in child environment only, repeat apply", Config);
        await Check("native desktop TOML/JSON merge, staging, encrypted journal, rollback and concurrent edit protection",
            () => DesktopIntegrationAudit.Run(Path.Combine(root, "desktop-" + Guid.NewGuid().ToString("N"))));
        await Check("API-key-only model/group/price sync without account login or inference",
            () => ScopedCatalogAudit.Run(Path.Combine(root, "scoped-" + Guid.NewGuid().ToString("N"))));
        await Check("web approval continuation, memory-only device proof, encrypted key and reusable full-model profile",
            () => DesktopSetupAudit.Run(Path.Combine(root, "web-setup-" + Guid.NewGuid().ToString("N"))));
        await Check("full scoped model pickers, per-model protocols and existing-install key switching",
            () => CatalogSwitchAudit.Run(Path.Combine(root, "catalog-switch-" + Guid.NewGuid().ToString("N"))));
        await Check("account RSA login, 2FA, refresh, key contract and invalid auth", Account);
        await Check("server-compatible long Unicode password CNG envelope", EnvelopeAudit.Run);
        await Check("real HTTP entry cookies, refresh path, origin and encrypted session restore",
            () => HttpCookieAudit.Run(Path.Combine(root, "wire-" + Guid.NewGuid().ToString("N"))));
        await Check("credential rotation retains the old disk state on failed settings commit", FailedCredentialCommit);
        await Check("cross-provider CC Switch imports preserve native protocol and model identity",
            () => SwitchIntegrationAudit.Run(Path.Combine(root, "switch-import")));
        await Check("bundled local adapter startup, credential separation, model catalog and isolated native configuration",
            () => BuiltinGatewayAudit.Run(Path.Combine(root, "builtin-" + Guid.NewGuid().ToString("N"))));
        await Check("all three inference protocols and model attribution", Inference);
        await Check("shared-artifact sequential and parallel task queue", Queue);
        await Check("budget, call limit, uncertain request, stop and restart recovery", Recovery);
        await Check("archive traversal and damaged package rejection", Archive);
        await Check("real Windows shortcuts, native MSIX activation, encrypted-profile references and repeat creation",
            () => ShortcutAudit.Run(Path.Combine(root, "shortcuts-" + Guid.NewGuid().ToString("N"))));
        File.WriteAllText(Path.Combine(root, "test-results.json"), JsonSerializer.Serialize(
            new { timestamp = DateTimeOffset.UtcNow, failed, checks = results }, JsonData.Options), new UTF8Encoding(false));
        Console.WriteLine($"RESULT {results.Count - failed}/{results.Count} passed; {failed} failed");
        return failed == 0 ? 0 : 1;
    }
    private static async Task Check(string name, Func<Task> action)
    {
        try { await action(); results.Add(new { name, result = "passed" }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; results.Add(new { name, result = "failed", error = ex.GetType().Name, message = Privacy.Clean(ex.Message) }); Console.WriteLine("FAIL " + name + ": " + Privacy.Clean(ex.Message)); }
    }
    private static void True(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (UserException) { return; }
        throw new InvalidOperationException("Expected a rejected operation");
    }
    private static StateStore Store(string name)
    {
        var store = new StateStore(Path.Combine(root, name));
        store.State.WorkDirectory = Path.Combine(root, name, "中文 空格 工作区");
        Directory.CreateDirectory(store.State.WorkDirectory);
        return store;
    }
    private static Task Vault()
    {
        var store = Store("vault-" + Guid.NewGuid().ToString("N"));
        const string secret = "fixture-private-key-92716";
        var profile = new ConnectionProfile { Name = "中文连接" };
        store.SaveProfile(profile, secret);
        True(store.Key(profile) == secret, "DPAPI did not decrypt");
        True(!Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories)
            .Any(p => Encoding.UTF8.GetString(File.ReadAllBytes(p)).Contains(secret)), "secret leaked to disk");
        store.RemoveProfile(profile); True(store.Key(profile) is null, "delete failed");
        return Task.CompletedTask;
    }
    private static Task Guard()
    {
        foreach (var url in new[] { "http://example.com/v1", "https://user:pass@example.com", "https://a.com/v1?key=test", "file:///C:/x" })
            Throws(() => UrlPolicy.Base(url));
        True(UrlPolicy.Base("http://127.0.0.1:9999/v1").IsLoopback, "loopback rejected");
        Throws(() => SafeFiles.Child(root, "../escape"));
        Throws(() => UrlPolicy.OfficialDownload("https://evil.example.com/setup.exe"));
        Throws(() => UrlPolicy.SafeId("../../credentials"));
        return Task.CompletedTask;
    }
    private static Task FailedCredentialCommit()
    {
        var store = Store("credential-commit-" + Guid.NewGuid().ToString("N"));
        var original = new ConnectionProfile { BaseUrl = "https://original.example.com/v1" };
        store.SaveProfile(original, "fixture-original-secret");
        var update = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(original, JsonData.Options), JsonData.Options)!;
        update.BaseUrl = "https://new.example.com/v1";
        Directory.CreateDirectory(Path.Combine(store.Root, "settings.json.previous"));
        try { store.SaveProfile(update, "fixture-new-secret"); throw new InvalidOperationException("expected commit failure"); }
        catch (IOException) { }
        var reload = new StateStore(store.Root);
        True(reload.Active.BaseUrl == original.BaseUrl && reload.Key(reload.Active) == "fixture-original-secret",
            "new key became associated with an old provider URL");
        return Task.CompletedTask;
    }
    private static Task Pricing()
    {
        using var doc = JsonDocument.Parse("""
        {"usable_group":{"g":"GPT","c":"Claude"},"data":[
          {"model_name":"gpt-fixture","enable_groups":["g"],"supported_endpoint_types":["openai","anthropic"],
           "group_endpoint_types":{"g":["openai-response"]},"group_pricing":{"g":{"available":true,"currency":"CNY","billing_mode":"token",
           "markup_included":true,"additional_group_ratio":false,"token_unit":1000000,"rates":{"input_price":"2","output_price":"8"}}}},
          {"model_name":"claude-fixture","enable_groups":["c"],"group_endpoint_types":{"c":["anthropic"]},
           "group_pricing":{"c":{"available":true,"currency":"CNY","billing_mode":"credit","markup_included":true,"per_credit_price":"0.1425"}}},
          {"model_name":"paused","runtime_paused":true,"enable_groups":["g"],"group_endpoint_types":{"g":["openai"]},
           "group_pricing":{"g":{"available":true,"currency":"CNY"}}},
          {"model_name":"missing-contract","enable_groups":["g"],"supported_endpoint_types":["openai"],
           "group_pricing":{"g":{"available":true,"currency":"CNY"}}}]}
        """);
        var routes = CatalogService.ParsePricing(doc.RootElement);
        True(routes.Count == 4 && routes[0].Budgetable, "catalog shape");
        True(routes[0].Protocols.SequenceEqual(new[] { WireProtocol.Responses }), "model-wide protocol leaked");
        True(routes[0].Estimate(1_000_000, 1_000_000) == 10m, "retail rate multiplied twice");
        True(!routes[1].Budgetable && routes[1].PriceText.Contains("0.1425"), "credit billed as tokens");
        True(!routes[2].Available && !routes[3].Available, "paused/missing protocol accepted");
        True(ClientService.Supports(ClientKind.Claude, routes[0]) && BuiltinGateway.Required(ClientKind.Claude, new ConnectionProfile { Model = routes[0].Id, Protocol = WireProtocol.Responses }), "Cross-protocol route did not require the bundled adapter");
        using var legacy = JsonDocument.Parse("""
        {"usable_group":{"default":"默认"},"data":[{"model_name":"gpt-6.1-sol","enable_groups":["default"],
         "group_endpoint_types":{"default":["openai"]}}]}
        """);
        var defaultRoute = CatalogService.ParsePricing(legacy.RootElement).Single();
        True(defaultRoute.Available && !defaultRoute.Budgetable && defaultRoute.PriceText.Contains("未提供最终价"),
            "available unquoted default model was hidden or assigned an invented price");
        return Task.CompletedTask;
    }
    private static async Task Config()
    {
        var store = Store("config-" + Guid.NewGuid().ToString("N"));
        var profile = new ConnectionProfile { Model = "gpt-6.1-sol", Group = "g", Reasoning = "high" };
        store.SaveProfile(profile, "fixture-key-92716");
        var service = new ClientService(store);
        var exe = Environment.ProcessPath!;
        foreach (var kind in new[] { ClientKind.Codex, ClientKind.Harness, ClientKind.Claude })
        {
            profile.Model = kind == ClientKind.Claude ? "claude-sonnet-4-5" : "gpt-6.1-sol";
            profile.Protocol = kind == ClientKind.Claude ? WireProtocol.Anthropic : WireProtocol.Responses;
            profile.Reasoning = kind == ClientKind.Claude ? "" : "high";
            var route = Route(profile.Model, profile.Protocol, profile.Group);
            var installation = new ClientInstallation { Kind = kind, Executable = exe };
            service.Configure(installation, profile, route);
            service.Configure(installation, profile, route);
            var start = service.LaunchInfo(installation, profile);
            True(start.Environment["MATHGPT_API_KEY"] == "fixture-key-92716", "missing process key");
            True(!start.Arguments.Contains("fixture-key") && !start.ArgumentList.Any(x => x.Contains("fixture-key")), "command line secret");
            True(Directory.EnumerateFiles(installation.ConfigDirectory, "*.previous", SearchOption.AllDirectories).Any(), "no own-config recovery file");
            True(!Directory.EnumerateFiles(installation.ConfigDirectory, "*", SearchOption.AllDirectories)
                .Any(p => File.ReadAllText(p).Contains("fixture-key")), "plaintext configuration");
            True(installation.ConfigDirectory.StartsWith(store.Root), "escaped profile root");
        }
        await Task.CompletedTask;
    }
    private static async Task Account()
    {
        var store = Store("account-" + Guid.NewGuid().ToString("N"));
        var handler = new FixtureHandler();
        using var site = new WebsiteClient(store, "http://127.0.0.1:41890", handler);
        var challenge = await site.Login("fixture", "fixture-password");
        True(challenge is not null && challenge.Methods.Contains("2fa"), "2FA challenge not surfaced");
        await site.VerifyLogin(challenge!.FlowToken, "123456");
        True(site.Account?.Balance == 10m, "account quota conversion");
        var keys = await site.Keys();
        True(keys.Count == 1 && keys[0].Group == "g" && keys[0].Usable, "key list contract");
        True(await site.KeySecret(1) == "fixture-key", "key secret contract");
        True(await site.CreateKey("桌面", "g", 1m) == 2, "create key contract");
        await site.RefreshSession();
        handler.RejectAuth = true;
        try { await site.RefreshAccount(); throw new InvalidOperationException("invalid credential accepted"); }
        catch (UserException) { True(!site.SignedIn, "invalid auth retained"); }
        await site.Logout();
        True(!store.Vault.Contains("site-session"), "logout retained session");
    }
    private static async Task Inference()
    {
        var store = Store("inference-" + Guid.NewGuid().ToString("N"));
        var profile = Profile(store);
        var handler = new FixtureHandler();
        var inference = new InferenceService(store, new HttpClient(handler));
        foreach (var wire in Enum.GetValues<WireProtocol>())
        {
            var output = await inference.Run(profile, new() { Model = "fixture-model", Protocol = wire, Role = "review" }, "test", 1000, default);
            True(output.Complete && output.UsageKnown && output.Model == "fixture-model" && output.Input == 20 && output.Output == 10, "inference shape");
        }
        True(handler.Calls == 3 && handler.MaxSeenOutput == 1000, "unbounded/repeated request");
        foreach (var (model, wire, efforts) in new[] {
            ("gpt-6.1-sol", WireProtocol.Responses, new[] { "low", "medium", "high", "xhigh", "max" }),
            ("claude-opus-5-5", WireProtocol.Anthropic, new[] { "low", "medium", "high", "xhigh", "max" }),
            ("gemini-4-argon", WireProtocol.ChatCompletions, new[] { "low", "medium", "high" }) })
        foreach (var effort in efforts)
        {
            await inference.Run(profile, new() { Model = model, Protocol = wire, Role = "review", Reasoning = effort }, "test", 1000, default);
            using var request = JsonDocument.Parse(handler.Inputs.Last());
            var actual = wire == WireProtocol.Responses ? request.RootElement.Obj("reasoning").Str("effort") :
                wire == WireProtocol.Anthropic ? request.RootElement.Obj("output_config").Str("effort") : request.RootElement.Str("reasoning_effort");
            True(actual == effort && request.RootElement.Str("model") == model, "model or native reasoning parameter changed");
            if (wire == WireProtocol.Anthropic) True(request.RootElement.Obj("thinking").Str("type") == "adaptive", "Claude adaptive thinking missing");
        }
    }
    private static async Task Queue()
    {
        foreach (var mode in Enum.GetValues<TaskMode>())
        {
            var store = Store("queue-" + Guid.NewGuid().ToString("N"));
            var profile = Profile(store);
            var handler = new FixtureHandler();
            using var site = new WebsiteClient(store, "http://127.0.0.1:41890", handler);
            var queue = new TaskQueue(store, new InferenceService(store, new HttpClient(handler)),
                new CatalogService(store, site, new HttpClient(handler)));
            var task = queue.Enqueue("完成中文 测试任务", mode, Roles(profile), 1m, 2, 6, 1024);
            await Finished(task);
            True(task.Status == WorkStatus.Completed && task.Calls == 3, task.Error);
            True(task.Steps.All(s => File.Exists(Path.Combine(task.Directory, s.OutputFile))), "no persisted artifacts");
            True(handler.Inputs.Last().Contains("fixture-result"), "role outputs were not shared");
            True(task.Committed > 0 && task.Committed < task.Budget, "cost not recorded");
            True(!File.ReadAllText(Path.Combine(task.Directory, "记录.json")).Contains("fixture-secret-key"), "artifact key leak");
        }
    }
    private static async Task Recovery()
    {
        var store = Store("recovery-" + Guid.NewGuid().ToString("N"));
        var profile = Profile(store);
        var handler = new FixtureHandler { FailInference = true };
        using var site = new WebsiteClient(store, "http://127.0.0.1:41890", handler);
        var queue = new TaskQueue(store, new InferenceService(store, new HttpClient(handler)),
            new CatalogService(store, site, new HttpClient(handler)));
        var task = queue.Enqueue("测试失败后的恢复", TaskMode.Sequential, Roles(profile), 1m, 2, 6, 1024);
        await Finished(task);
        True(task.Status == WorkStatus.Failed && task.Calls == 1 && task.Committed > 0, "failure retry or reservation lost");
        var before = task.Committed;
        handler.FailInference = false; queue.Resume(task.Id); await Finished(task);
        True(task.Status == WorkStatus.Completed && task.Calls == 4 && task.Committed > before, "retry did not retain old uncertain cost");
        var tiny = queue.Enqueue("超过预算的任务", TaskMode.Sequential, Roles(profile), 0.000001m, 1, 3, 1024);
        await Finished(tiny); True(tiny.Calls == 0 && tiny.Status == WorkStatus.Failed, "over-budget request sent");
        handler.DelayMs = 2000;
        var stop = queue.Enqueue("取消任务", TaskMode.Sequential, Roles(profile), 1m, 1, 3, 1024);
        for (int i = 0; i < 100 && stop.Calls == 0; i++) await Task.Delay(10);
        queue.Stop(stop.Id); await Finished(stop);
        True(stop.Status == WorkStatus.Paused && stop.Calls == 1 && stop.Committed > 0, "cancel lost unknown charge");
        var persisted = JsonSerializer.Deserialize<List<WorkTask>>(File.ReadAllText(Path.Combine(store.Root, "tasks.json")), JsonData.Options)!;
        persisted[0].Status = WorkStatus.Running; persisted[0].Steps[0].Status = WorkStatus.Running;
        File.WriteAllText(Path.Combine(store.Root, "tasks.json"), JsonSerializer.Serialize(persisted, JsonData.Options));
        var restart = new TaskQueue(store, new InferenceService(store), new CatalogService(store, site));
        True(restart.Tasks[0].Status == WorkStatus.Interrupted && restart.Tasks[0].Steps[0].Status == WorkStatus.Interrupted, "restart resubmitted unknown requests");
    }
    private static async Task Archive()
    {
        var folder = Path.Combine(root, "archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "bad.zip");
        using (var a = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        { using var writer = new StreamWriter(a.CreateEntry("../outside.exe").Open()); writer.Write("not executable"); }
        Throws(() => PackageService.ExtractSafe(zip, Path.Combine(folder, "extract"), default));
        var release = new PackageRelease(ClientKind.Codex, "test", "https://github.com/openai/codex/test.zip", "bad.zip", "SHA256", new string('0', 64), false, "x64");
        True(!await BitsDownload.Verify(zip, release), "damaged package accepted");
    }
    private static ConnectionProfile Profile(StateStore store)
    {
        var p = new ConnectionProfile { Name = "夹具", BaseUrl = "http://127.0.0.1:41890/v1", Group = "g",
            Model = "fixture-model", Protocol = WireProtocol.Responses, CustomInputPrice = 1, CustomOutputPrice = 2 };
        store.SaveProfile(p, "fixture-secret-key"); return p;
    }
    private static ModelRoute Route(string model, WireProtocol wire, string group) =>
        new() { Id = model, Group = group, Protocols = [wire], Available = true, BillingMode = "token",
            FinalCny = true, InputPrice = 1, OutputPrice = 2, PriceVersion = "fixture" };
    private static List<RoleAssignment> Roles(ConnectionProfile p) => new[] { "规划", "实现", "审阅" }.Select(role =>
        new RoleAssignment { Role = role, ProfileId = p.Id, Model = p.Model, Protocol = WireProtocol.Responses, Quote = Route(p.Model, WireProtocol.Responses, p.Group) }).ToList();
    private static async Task Finished(WorkTask task)
    {
        for (int i = 0; i < 1000 && task.Status is WorkStatus.Queued or WorkStatus.Running; i++) await Task.Delay(10);
        True(task.Status is not WorkStatus.Queued and not WorkStatus.Running, "queue timed out");
        await Task.Delay(20);
    }
}

internal sealed class FixtureHandler : HttpMessageHandler
{
    public bool RejectAuth { get; set; }
    public bool FailInference { get; set; }
    public int DelayMs { get; set; }
    public int Calls;
    public int MaxSeenOutput;
    public List<string> Inputs { get; } = [];
    private readonly RSA rsa = RSA.Create(2048);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
        using var body = JsonDocument.Parse(text);
        var b = body.RootElement;
        object result;
        if (path == "/api/status") result = new { success = true, data = new { quota_per_unit = 500000, password_login_enabled = true } };
        else if (path.EndsWith("/login/encryption-key")) result = new { success = true, data = new { enabled = true, kid = "fixture", public_key = rsa.ExportSubjectPublicKeyInfoPem() } };
        else if (path == "/api/user/login")
        {
            var value = Encoding.UTF8.GetString(rsa.Decrypt(Convert.FromBase64String(b.Str("password_encrypted")), RSAEncryptionPadding.OaepSHA256));
            if (value != "fixture-password") throw new InvalidOperationException("RSA password incorrect");
            result = new { success = true, data = new { require_verification = true, flow_token = "fixture-flow", methods = new[] { new { method = "2fa" } } } };
        }
        else if (path == "/api/user/login/verify" || path.EndsWith("/auth/refresh"))
        {
            if (path.Contains("verify") && b.Str("method") != "2fa") throw new InvalidOperationException("wrong verification method");
            result = new { success = true, data = new { access_token = "fixture-session-access", access_expires_at = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds() } };
        }
        else if (path == "/api/user/self")
        {
            if (RejectAuth) return new(HttpStatusCode.Unauthorized);
            if (request.Headers.Authorization?.Parameter != "fixture-session-access") throw new InvalidOperationException("missing session bearer");
            result = new { success = true, data = new { id = 1, display_name = "夹具账号", quota = 5000000, used_quota = 100000 } };
        }
        else if (path == "/api/token/" && request.Method == HttpMethod.Get)
            result = new { success = true, data = new { items = new[] { new { id = 1, name = "夹具密钥", group = "g", status = 1, remain_quota = 500000, expired_time = -1 } } } };
        else if (path.EndsWith("/key")) result = new { success = true, data = new { key = "fixture-key" } };
        else if (path == "/api/token/" && request.Method == HttpMethod.Post)
        {
            if (b.Number("remain_quota") != 500000 || b.Flag("unlimited_quota")) throw new InvalidOperationException("unbounded key creation");
            result = new { success = true, data = new { id = 2 } };
        }
        else if (path.EndsWith("/auth/logout")) result = new { success = true };
        else if (path.EndsWith("/models")) result = new { data = new[] { new { id = "fixture-model" } } };
        else if (path.EndsWith("/responses") || path.EndsWith("/messages") || path.EndsWith("/completions"))
        {
            Interlocked.Increment(ref Calls);
            if (FailInference) return new(HttpStatusCode.ServiceUnavailable);
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            lock (Inputs) Inputs.Add(text);
            MaxSeenOutput = (int)Math.Max(b.Number("max_output_tokens"), Math.Max(b.Number("max_tokens"), b.Number("max_completion_tokens")));
            if (path.EndsWith("/responses")) result = new { model = "fixture-model", status = "completed",
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "fixture-result" } } } },
                usage = new { input_tokens = 20, output_tokens = 10 } };
            else if (path.EndsWith("/messages"))
            {
                if (!request.Headers.Contains("x-api-key")) throw new InvalidOperationException("Messages missing native key header");
                result = new { model = "fixture-model", stop_reason = "end_turn", content = new[] { new { type = "text", text = "fixture-result" } },
                    usage = new { input_tokens = 20, output_tokens = 10 } };
            }
            else result = new { model = "fixture-model", choices = new[] { new { finish_reason = "stop", message = new { content = "fixture-result" } } },
                usage = new { prompt_tokens = 20, completion_tokens = 10 } };
        }
        else throw new InvalidOperationException("unexpected fixture route: " + path);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
    }
}
