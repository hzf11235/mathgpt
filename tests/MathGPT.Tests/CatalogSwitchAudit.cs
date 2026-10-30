using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class CatalogSwitchAudit
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ModelRoute Route(string id, string group, params WireProtocol[] protocols) =>
        new() { Id = id, Group = group, Available = true, Protocols = protocols.ToList() };

    private static string[] ModelIds(string json) => JsonDocument.Parse(json).RootElement.GetProperty("models")
        .EnumerateArray().Select(model => model.GetProperty("slug").GetString()!).ToArray();

    public static Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var store = new StateStore(Path.Combine(root, "state"));
        store.State.WorkDirectory = Path.Combine(root, "work");
        Directory.CreateDirectory(store.State.WorkDirectory);
        var gpt = new ConnectionProfile { Name = "GPT", Model = "gpt-6.1-sol", Group = "gpt-standard", Protocol = WireProtocol.Responses, Reasoning = "low" };
        store.SaveProfile(gpt, "fixture-scope-gpt-key");
        var sol = Route(gpt.Model, gpt.Group, WireProtocol.Responses);
        var astra = Route("gpt-6-astra", gpt.Group, WireProtocol.Responses);
        var old = Route("gpt-5.6-sol", gpt.Group, WireProtocol.Responses);
        var unauthorized = Route("gpt-foreign", "another-group", WireProtocol.Responses);
        var paused = new ModelRoute { Id = "gpt-paused", Group = gpt.Group, Available = false, Protocols = [WireProtocol.Responses] };
        var scope = new[] { astra, sol, old, sol, unauthorized, paused, Route("protocol-absent", gpt.Group) };
        var cache = """{"models":[{"slug":"gpt-6.1-sol","display_name":"Vendor model","context_window":12345},{"slug":"gpt-foreign","context_window":99999}]}""";
        var native = CompatibilityCatalog.CreateNative(gpt, scope, cache);
        Require(ModelIds(native).SequenceEqual(new[] { "gpt-6.1-sol", "gpt-5.6-sol", "gpt-6-astra" }), "A selected default or partial cache restricted the authorized catalog.");
        using (var doc = JsonDocument.Parse(native))
        {
            var first = doc.RootElement.GetProperty("models")[0];
            Require(first.GetProperty("context_window").GetInt32() == 12345 && first.GetProperty("display_name").GetString() == "Vendor model", "Exact native metadata was replaced by invented capabilities.");
            var fallback = doc.RootElement.GetProperty("models")[1];
            Require(fallback.GetProperty("context_window").GetInt32() == 32768 && fallback.GetProperty("description").GetString()!.Contains("compatibility"), "An uncached model lacks its conservative compatibility disclosure.");
        }
        Require(ModelIds(CompatibilityCatalog.CreateNative(gpt, scope, "broken")).Length == 3, "An unreadable old cache hid valid models.");
        using (var unknown = JsonDocument.Parse(CompatibilityCatalog.Create(gpt, [Route("unknown-model", gpt.Group, WireProtocol.Responses)])))
            Require(unknown.RootElement.GetProperty("models")[0].GetProperty("supported_reasoning_levels").GetArrayLength() == 0, "Unknown models were assigned an invented reasoning level.");
        foreach (var id in new[] { "gpt-6.1-terra", "gpt-6-luna", "gpt-5.6-luna" })
            Require(ReasoningPolicy.Allowed(id).SequenceEqual(new[] { "none", "low", "medium", "high", "xhigh", "max" }), "A published model lost its documented effort options.");

        var client = new ClientInstallation { Kind = ClientKind.Harness, Version = "fixture-1", Executable = Path.Combine(root, "harness.exe"), InstallationRoot = root };
        File.WriteAllText(client.Executable, "existing application", new UTF8Encoding(false));
        var initialExecutable = File.ReadAllBytes(client.Executable);
        new ClientService(store).Configure(client, gpt, sol, scope, Environment.ProcessPath!);
        var patch = File.ReadAllText(Path.Combine(client.ConfigDirectory, "profiles/desktop/cordis.patch.yml"));
        Require(!client.BuiltinRouting && patch.Contains(old.Id) && patch.Contains(astra.Id) && patch.Contains("reasoningEfforts:") && !patch.Contains("gpt-foreign") && !patch.Contains("gpt-paused"), "Native Harness did not retain the full same-provider picker or effort declarations.");
        var cli = new ClientInstallation { Kind = ClientKind.Codex };
        new ClientService(store).Configure(cli, gpt, sol, scope, Environment.ProcessPath!);
        Require(ModelIds(File.ReadAllText(Path.Combine(cli.ConfigDirectory, "models.json"))).Length == 3 &&
            File.ReadAllText(Path.Combine(cli.ConfigDirectory, "config.toml")).Contains("model_catalog_json"), "Codex CLI did not receive its full scoped picker.");

        var home = Path.Combine(root, "home");
        var local = Path.Combine(root, "local");
        SafeFiles.AtomicWrite(Path.Combine(home, ".codex/models_cache.json"), cache);
        var desktop = new DesktopConfiguration(store, home, local);
        var fakeCodex = Path.Combine(root, "vendor-codex", "ChatGPT.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeCodex)!); File.WriteAllText(fakeCodex, "fixture");
        CodexDesktopIsolationAudit.MakeAsar(Path.Combine(Path.GetDirectoryName(fakeCodex)!, "resources/app.asar"), "CODEX_HOME CODEX_ELECTRON_USER_DATA_PATH userData requestSingleInstanceLock");
        var codexDesktop = new ClientInstallation { Kind = ClientKind.CodexDesktop, Executable = fakeCodex, Version = "existing" };
        desktop.Prepare(codexDesktop, gpt, scope, Environment.ProcessPath!);
        desktop.Apply(codexDesktop, false);
        var nativePath = Path.Combine(store.Root, "desktop-catalogs/codex-" + gpt.Id + ".json");
        Require(ModelIds(File.ReadAllText(nativePath)).Length == 3, "Codex Desktop still used only the old cache's selected model.");

        var all = new ConnectionProfile { Name = "All", Model = sol.Id, Group = "universal", Protocol = WireProtocol.Responses };
        store.SaveProfile(all, "fixture-all-model-key");
        var allRoutes = new[]
        {
            Route(sol.Id, all.Group, WireProtocol.ChatCompletions, WireProtocol.Responses),
            Route("claude-opus-5-5", all.Group, WireProtocol.Anthropic),
            Route("gemini-4-argon", all.Group, WireProtocol.ChatCompletions),
            Route("foreign", "not-authorized", WireProtocol.Responses)
        };
        foreach (var kind in Enum.GetValues<ClientKind>())
            Require(BuiltinGateway.Required(kind, all, allRoutes), "An initially native universal model skipped the adapter for later cross-provider selections.");
        new ClientService(store).Configure(client, all, allRoutes[0], allRoutes, Environment.ProcessPath!);
        patch = File.ReadAllText(Path.Combine(client.ConfigDirectory, "profiles/desktop/cordis.patch.yml"));
        Require(client.BuiltinRouting && patch.Contains("claude-opus-5-5") && patch.Contains("gemini-4-argon") && !patch.Contains("foreign"), "Universal Harness picker widened or lost its authorized model scope.");
        using (var gateway = JsonDocument.Parse(store.Vault.Get("builtin-gateway-" + all.Id)!))
        {
            var mappings = gateway.RootElement.GetProperty("models");
            Require(mappings.GetProperty(sol.Id).GetString() == "responses" && mappings.GetProperty("claude-opus-5-5").GetString() == "messages" &&
                mappings.GetProperty("gemini-4-argon").GetString() == "chat", "Native model switches used the default model's protocol instead of the selected model's protocol.");
        }
        var fakeClaude = Path.Combine(root, "vendor-claude", "Claude.exe");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fakeClaude)!, "resources")); File.WriteAllText(fakeClaude, "fixture");
        ClaudeHarnessIsolationAudit.WriteAsar(Path.Combine(Path.GetDirectoryName(fakeClaude)!, "resources/app.asar"), ".vite/build/index.pre.js", ClaudeHarnessIsolationAudit.ClaudeSupportMarkers);
        var claudeDesktop = new ClientInstallation { Kind = ClientKind.ClaudeDesktop, BuiltinRouting = true, Executable = fakeClaude };
        desktop.Prepare(claudeDesktop, all, allRoutes, Environment.ProcessPath!);
        desktop.Apply(claudeDesktop, false);
        var settingsPath = Path.Combine(claudeDesktop.ConfigDirectory, Guid.ParseExact(all.Id, "N") + ".json");
        using (var gateway = JsonDocument.Parse(File.ReadAllText(settingsPath)))
        {
            var models = gateway.RootElement.GetProperty("inferenceModels").EnumerateArray().Select(model => model.GetProperty("name").GetString()).ToArray();
            Require(models.Length == 3 && models.Contains("gemini-4-argon") && models.Contains("claude-opus-5-5"), "Claude Desktop gateway did not declare the full universal picker.");
        }
        var claudeCli = new ClientInstallation { Kind = ClientKind.Claude };
        new ClientService(store).Configure(claudeCli, all, allRoutes[0], allRoutes, Environment.ProcessPath!);
        using (var picker = JsonDocument.Parse(File.ReadAllText(Path.Combine(claudeCli.ConfigDirectory, "settings.json"))))
        {
            var values = picker.RootElement.GetProperty("modelPicker").GetProperty("options").EnumerateArray().Select(row => row.GetProperty("model").GetString()).ToArray();
            Require(values.Length == 3 && values.Contains("gpt-6.1-sol") && values.Contains("gemini-4-argon"), "Claude Code custom picker was restricted to one model.");
        }

        var mixed = new[] { sol, Route("message-only-fixture", gpt.Group, WireProtocol.Anthropic) };
        Require(BuiltinGateway.Required(ClientKind.Harness, gpt, mixed) && BuiltinGateway.Required(ClientKind.CodexDesktop, gpt, mixed), "A mixed-protocol authorized catalog needed another manual setup after switching models.");
        var gemini = new ConnectionProfile { Name = "Gemini", Model = "gemini-4-argon", Group = "gemini-standard", Protocol = WireProtocol.ChatCompletions };
        store.SaveProfile(gemini, "fixture-gemini-key");
        var geminiRoute = Route(gemini.Model, gemini.Group, WireProtocol.ChatCompletions);
        new ClientService(store).Configure(client, gemini, geminiRoute, [geminiRoute], Environment.ProcessPath!);
        patch = File.ReadAllText(Path.Combine(client.ConfigDirectory, "profiles/desktop/cordis.patch.yml"));
        Require(client.ProfileId == gemini.Id && client.Version == "fixture-1" && client.Executable == Path.Combine(root, "harness.exe") &&
            File.ReadAllBytes(client.Executable).SequenceEqual(initialExecutable) && patch.Contains(gemini.Model) && !patch.Contains(sol.Id), "Changing keys reinstalled the application or retained the previous connection's model list.");
        var state = File.ReadAllText(Path.Combine(store.Root, "settings.json"));
        Require(!state.Contains("fixture-all-model-key") && !state.Contains("fixture-scope-gpt-key") && !state.Contains("fixture-gemini-key"), "A model switch persisted a plaintext API key.");
        return Task.CompletedTask;
    }
}
