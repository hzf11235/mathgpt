using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class BuiltinGatewayAudit
{
    public static async Task Run(string root)
    {
        var store = new StateStore(root);
        var profile = new ConnectionProfile { Model = "claude-opus-5-5", Group = "universal", Protocol = WireProtocol.Anthropic, BaseUrl = "https://mathgpt.xyz/v1" };
        if (!BuiltinGateway.Required(ClientKind.CodexDesktop, profile) || !BuiltinGateway.Required(ClientKind.Harness, profile))
            throw new Exception("Universal profiles must keep the adapter active across native model switches.");
        store.SaveProfile(profile, "fixture-upstream-key-must-remain-private");
        var routes = new List<ModelRoute>
        {
            new() { Id = profile.Model, Group = profile.Group, Available = true, Protocols = [WireProtocol.Anthropic] },
            new() { Id = "gpt-6.1-sol", Group = profile.Group, Available = true, Protocols = [WireProtocol.Responses] },
            new() { Id = "gemini-4-argon", Group = profile.Group, Available = true, Protocols = [WireProtocol.ChatCompletions] },
        };
        var gateway = new BuiltinGateway(store);
        var endpoint = gateway.Prepare(profile, routes);
        var binary = Path.GetFullPath("build/bridge-audit/MathGPT.Bridge.exe");
        var local = await gateway.Ensure(profile, binary);
        if (local == store.Key(profile) || !endpoint.StartsWith("http://127.0.0.1:")) throw new Exception("Local credential separation failed");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", local);
        var address = endpoint[..^3];
        try
        {
            using var models = await client.GetAsync(endpoint + "/models");
            var body = await models.Content.ReadAsStringAsync();
            if (!models.IsSuccessStatusCode || !body.Contains("claude-opus-5-5") || !body.Contains("gemini-4-argon") || body.Contains("fixture-upstream")) throw new Exception("Exact model catalog failed");
            if (await gateway.Ensure(profile, "missing.exe") != local) throw new Exception("Existing adapter was not reused");
            using var browser = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models");
            browser.Headers.Add("Origin", "https://attacker.invalid");
            using var denied = await client.SendAsync(browser);
            if (denied.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Browser origin accepted");
            var fakeCodex = Path.Combine(root, "vendor-codex", "ChatGPT.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fakeCodex)!); File.WriteAllText(fakeCodex, "fixture");
            CodexDesktopIsolationAudit.MakeAsar(Path.Combine(Path.GetDirectoryName(fakeCodex)!, "resources/app.asar"), "CODEX_HOME CODEX_ELECTRON_USER_DATA_PATH userData requestSingleInstanceLock");
            var install = new ClientInstallation { Kind = ClientKind.CodexDesktop, BuiltinRouting = true, Executable = fakeCodex };
            var home = Path.Combine(root, "isolated-home"); var localData = Path.Combine(root, "isolated-local");
            var desktop = new DesktopConfiguration(store, home, localData);
            desktop.Prepare(install, profile, routes, Environment.ProcessPath!); desktop.Apply(install, false);
            var toml = File.ReadAllText(Path.Combine(install.ConfigDirectory, "config.toml"));
            if (!toml.Contains("claude-opus-5-5") || !toml.Contains("--builtin") || !toml.Contains(endpoint) || toml.Contains(local) || toml.Contains("fixture-upstream")) throw new Exception("Native configuration exposed secrets or changed model identity");
            var catalog = JsonDocument.Parse(CompatibilityCatalog.Create(profile, routes));
            if (catalog.RootElement.GetProperty("models").GetArrayLength() != 3) throw new Exception("Cross-vendor catalog incomplete");
            foreach (var model in catalog.RootElement.GetProperty("models").EnumerateArray())
                if (!model.GetProperty("slug").GetString()!.StartsWith("gpt-") &&
                    !model.GetProperty("supported_reasoning_levels").EnumerateArray().Any(e => e.GetProperty("effort").GetString() == "low"))
                    throw new Exception("Codex child-agent validation would reject this non-GPT model.");
            var agentFiles = Directory.GetFiles(Path.Combine(install.ConfigDirectory, "agents"), "*.toml");
            if (agentFiles.Length != 3 || !agentFiles.Any(p => File.ReadAllText(p).Contains("gemini-4-argon")))
                throw new Exception("Native cross-vendor subagents were not prepared.");
            foreach (var path in agentFiles)
                if (Tomlyn.Toml.Parse(File.ReadAllText(path)).HasErrors || File.ReadAllText(path).Contains("fixture-upstream"))
                    throw new Exception("Agent definition was invalid or exposed credentials.");
            var harness = new ClientInstallation { Kind = ClientKind.Harness };
            new ClientService(store).Configure(harness, profile, routes[0], routes, Environment.ProcessPath!);
            var yaml = File.ReadAllText(Path.Combine(harness.ConfigDirectory, "profiles", "desktop", "cordis.patch.yml"));
            if (!harness.BuiltinRouting || !yaml.Contains("subagent-model-selection-settings") ||
                !yaml.Contains("gemini-4-argon") || !yaml.Contains("openai-responses") || yaml.Contains("fixture-upstream"))
                throw new Exception("Harness universal picker or allowed child routes were incomplete.");
            var disk = File.ReadAllText(Path.Combine(root, "settings.json"));
            if (disk.Contains(local) || disk.Contains("fixture-upstream")) throw new Exception("Plaintext persistence detected");
        }
        finally { using var stopped = await client.PostAsync(address + "/shutdown", null); }
    }
}
