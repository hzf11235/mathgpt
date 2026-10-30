using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class DesktopIntegrationAudit
{
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (UserException) { return; }
        throw new InvalidOperationException("Expected protected desktop configuration to reject the operation.");
    }
    public static Task Run(string root)
    {
        var legacy = new StateStore(Path.Combine(root, "legacy-state"));
        var oldProfile = new ConnectionProfile { Name = "旧连接", Model = "gpt-6.1-sol" };
        legacy.SaveProfile(oldProfile, "fixture-migration-key");
        legacy.State.Clients.Add(new() { Kind = ClientKind.Codex, ProfileId = oldProfile.Id, Executable = "legacy-cli.exe" });
        legacy.State.SchemaVersion = 1; legacy.Save();
        var oldDisk = File.ReadAllText(Path.Combine(legacy.Root, "settings.json"));
        var migrated = new StateStore(legacy.Root);
        Assert(migrated.State.SchemaVersion == 2 && migrated.Key(migrated.State.Profiles[0]) == "fixture-migration-key" &&
            migrated.State.Clients[0].Kind == ClientKind.Codex && File.ReadAllText(Path.Combine(legacy.Root, "settings.json")) == oldDisk,
            "Legacy schema, CLI identity, DPAPI key or on-disk state was not preserved.");
        migrated.Save();
        Assert(new StateStore(legacy.Root).State.SchemaVersion == 2, "Schema migration did not persist.");
        var store = new StateStore(Path.Combine(root, "state"));
        var home = Path.Combine(root, "测试用户 中文 空格"); var local = Path.Combine(root, "local");
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        var profile = new ConnectionProfile { Name = "桌面测试", Model = "gpt-6.1-sol", Group = "gpt-standard", Protocol = WireProtocol.Responses };
        const string secret = "fixture-desktop-private-only"; store.SaveProfile(profile, secret);
        var fakeCodex = Path.Combine(root, "fake-codex", "ChatGPT.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeCodex)!);
        File.WriteAllText(fakeCodex, "fixture: never executed");
        CodexDesktopIsolationAudit.MakeAsar(Path.Combine(Path.GetDirectoryName(fakeCodex)!, "resources", "app.asar"),
            "CODEX_HOME; CODEX_ELECTRON_USER_DATA_PATH; userData; requestSingleInstanceLock;");
        var codex = new ClientInstallation { Kind = ClientKind.CodexDesktop, Executable = fakeCodex };
        var route = new ModelRoute { Id = profile.Model, Group = profile.Group, Available = true, Protocols = [WireProtocol.Responses] };
        var originalConfigPath = Path.Combine(home, ".codex", "config.toml");
        var configPath = Path.Combine(CodexDesktopIsolation.ConfigurationDirectory(store, profile), "config.toml");
        const string original = "# 中文注释\r\n\"model\" = 'old' # keep this comment\r\nmodel_provider='existing'\r\n\r\n[features]\r\nkeep = true\r\nnote = '''\r\n[model_providers.mathgpt_fake]\r\nmodel = 'not a setting'\r\n'''\r\n[model_providers.existing]\r\nname='kept'\r\n[mcp_servers.unchanged]\r\ncommand='keep.exe'\r\n";
        SafeFiles.AtomicWrite(configPath, original);
        SafeFiles.AtomicWrite(originalConfigPath, original);
        var auth = Path.Combine(home, ".codex", "auth.json"); SafeFiles.AtomicWrite(auth, "{\"original\":\"untouched\"}");
        SafeFiles.AtomicWrite(Path.Combine(CodexDesktopIsolation.ConfigurationDirectory(store, profile), "models_cache.json"), """{"models":[{"slug":"gpt-6.1-sol","display_name":"GPT-6.1-Sol","context_window":12345},{"slug":"foreign-model"}]}""");
        var service = new DesktopConfiguration(store, home, local);
        var helper = Environment.ProcessPath!;
        service.Prepare(codex, profile, [route], helper);
        Assert(codex.ConfigPending && File.ReadAllText(configPath) == original, "Preparation changed the running client's config.");
        Reject(() => service.Apply(codex, true));
        Assert(File.ReadAllText(configPath) == original, "Running-client protection failed.");
        service.Apply(codex, false);
        var configured = File.ReadAllText(configPath);
        Assert(!codex.ConfigPending && configured.Contains("# keep this comment") && configured.Contains("[mcp_servers.unchanged]") &&
            configured.Contains("not a setting") && configured.Contains("[model_providers.existing]"), "TOML merge discarded unrelated content.");
        Assert(configured.Contains("wire_api = \"responses\"") && configured.Contains("--credential") && !configured.Contains(secret), "Incorrect desktop auth configuration.");
        Assert(configured.Contains("requires_openai_auth = false"), "Gateway-only Codex setup must not require an OpenAI account.");
        Assert(File.ReadAllText(auth) == "{\"original\":\"untouched\"}", "Original account authentication changed.");
        Assert(File.ReadAllText(originalConfigPath) == original, "API setup changed the original subscription config.");
        var catalogPath = Path.Combine(store.Root, "desktop-catalogs", "codex-" + profile.Id + ".json");
        Assert(File.ReadAllText(catalogPath).Contains("12345") && !File.ReadAllText(catalogPath).Contains("foreign-model"), "Vendor catalog was invented or incorrectly widened.");
        service.Prepare(codex, profile, [route], helper);
        Assert(!codex.ConfigPending, "Repeated apply changed identical configuration.");
        service.Rollback(codex, false);
        Assert(File.ReadAllText(configPath) == original && !File.Exists(catalogPath), "Rollback failed to restore the exact prior TOML.");
        service.Prepare(codex, profile, [route], helper);
        File.AppendAllText(configPath, "\n# external change\n", new UTF8Encoding(false));
        Reject(() => service.Apply(codex, false));
        Assert(File.ReadAllText(configPath).Contains("external change"), "Concurrent external config change was overwritten.");
        SafeFiles.AtomicWrite(configPath, original);
        service.Prepare(codex, profile, [route], helper); service.Apply(codex, false);
        File.AppendAllText(configPath, "\n# after apply\n", new UTF8Encoding(false)); Reject(() => service.Rollback(codex, false));
        Reject(() => DesktopConfiguration.PatchCodex("model = [broken", new() { ["model"] = "\"gpt-6.1-sol\"" }, "mathgpt_test", ""));
        var cleared = DesktopConfiguration.PatchCodex("model_reasoning_effort='ultra' # preserve comment\nmodel_catalog_json='old-provider.json'\n[features]\nkeep=true\n",
            new() { ["model"] = "\"gpt-6-luna\"" }, "mathgpt_fixture", "", new HashSet<string> { "model_reasoning_effort", "model_catalog_json" });
        Assert(!cleared.Contains("ultra") && !cleared.Contains("old-provider.json") && cleared.Contains("# preserve comment") && cleared.Contains("keep=true"),
            "A prior provider's reasoning or catalog leaked into the new desktop configuration.");

        profile.Model = "claude-opus-5-5"; profile.Group = "claude-official"; profile.Protocol = WireProtocol.Anthropic; store.SaveProfile(profile, null);
        var claudeExecutable = Path.Combine(root, "vendor-Claude", "Claude.exe");
        SafeFiles.AtomicWrite(claudeExecutable, "inert vendor capability fixture - never executed");
        var claudeAsar = Path.Combine(Path.GetDirectoryName(claudeExecutable)!, "resources", "app.asar");
        Directory.CreateDirectory(Path.GetDirectoryName(claudeAsar)!);
        ClaudeHarnessIsolationAudit.WriteAsar(claudeAsar, ".vite/build/index.pre.js", ClaudeHarnessIsolationAudit.ClaudeSupportMarkers);
        var claude = new ClientInstallation { Kind = ClientKind.ClaudeDesktop, Executable = claudeExecutable };
        var messages = new ModelRoute { Id = profile.Model, Group = profile.Group, Available = true, Protocols = [WireProtocol.Anthropic] };
        var foreign = new ModelRoute { Id = "claude-sonnet-5", Group = "foreign", Available = true, Protocols = [WireProtocol.Anthropic] };
        var setting = Path.Combine(local, "Claude", "claude_desktop_config.json");
        const string beforeClaude = "{\"mcpServers\":{\"kept\":{\"command\":\"other.exe\"}},\"deploymentMode\":\"1p\"}";
        SafeFiles.AtomicWrite(setting, beforeClaude);
        var originalMeta = Path.Combine(local, "Claude-3p", "configLibrary", "_meta.json");
        const string beforeMeta = "{\"entries\":[{\"id\":\"prior\",\"name\":\"Previous\"}],\"appliedId\":\"prior\",\"unknown\":7}";
        SafeFiles.AtomicWrite(originalMeta, beforeMeta);
        var originalGatewayPath = Path.Combine(local, "Claude-3p", "configLibrary", Guid.ParseExact(profile.Id, "N") + ".json");
        const string beforeGateway = "{\"customPolicy\":{\"enabled\":true},\"inferenceProvider\":\"other\",\"inferenceCredentialHelper\":\"old-platform-helper\"}";
        SafeFiles.AtomicWrite(originalGatewayPath, beforeGateway);
        var originalFiles = new[] { setting, originalMeta, originalGatewayPath }.ToDictionary(path => path, File.ReadAllBytes);
        var layout = ClaudeHarnessIsolation.ClaudeProfile(store, profile);
        var privateSetting = Path.Combine(layout.UserData, "claude_desktop_config.json");
        var meta = Path.Combine(layout.Library, "_meta.json");
        var gatewayPath = Path.Combine(layout.Library, Guid.ParseExact(profile.Id, "N") + ".json");
        SafeFiles.AtomicWrite(privateSetting, beforeClaude);
        SafeFiles.AtomicWrite(meta, beforeMeta);
        SafeFiles.AtomicWrite(gatewayPath, beforeGateway);
        service.Prepare(claude, profile, [messages, foreign], helper); service.Apply(claude, false);
        Assert(originalFiles.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
            "Claude API setup modified the user's original account/profile files.");
        Assert(claude.ConfigDirectory == layout.Library && File.ReadAllText(privateSetting).Contains("other.exe") &&
            File.ReadAllText(meta).Contains("Previous") && File.ReadAllText(meta).Contains("unknown"),
            "The independent Claude config merge discarded its own existing entries.");
        var gateway = File.ReadAllText(Path.Combine(claude.ConfigDirectory, Guid.ParseExact(profile.Id, "N") + ".json"));
        using var gatewayData = System.Text.Json.JsonDocument.Parse(gateway);
        Assert(gatewayData.RootElement.GetProperty("customPolicy").GetProperty("enabled").GetBoolean() &&
            !gatewayData.RootElement.TryGetProperty("inferenceCredentialHelper", out _), "Claude gateway merge lost custom policy or retained another platform's helper.");
        Assert(gatewayData.RootElement.GetProperty("inferenceCredentialKind").GetString() == "helper-script",
            "Claude API connection must use the local credential helper, not interactive account sign-in.");
        foreach (var field in new[] { "disableEssentialTelemetry", "disableNonessentialTelemetry", "disableNonessentialServices", "skipWebFetchPreflight" })
            Assert(gatewayData.RootElement.GetProperty(field).GetBoolean(), "Claude gateway setup retained an unneeded remote connection: " + field);
        Assert(!gatewayData.RootElement.GetProperty("disableAutoUpdates").GetBoolean(), "Independent API setup disabled official security/compatibility updates.");
        Assert(!gatewayData.RootElement.GetProperty("modelCatalogEnabled").GetBoolean(), "Claude gateway setup must use its supplied local model catalog.");
        Assert(gatewayData.RootElement.GetProperty("desktopHome").GetString() == "off" &&
            gatewayData.RootElement.GetProperty("isClaudeCodeForDesktopEnabled").GetBoolean(), "MathGPT Claude Code must open its native Code surface.");
        foreach (var field in new[] { "coworkTabEnabled", "chatTabEnabled", "chatAdvancedFileAnalysisEnabled" })
            Assert(!gatewayData.RootElement.GetProperty(field).GetBoolean(), "Claude Code setup unexpectedly enabled a VM-dependent surface: " + field);
        Assert(gateway.Contains("inferenceCredentialHelperWindows") && gateway.Contains("--credential") && gateway.Contains(profile.Model) &&
            !gateway.Contains(secret) && !gateway.Contains("claude-sonnet-5"), "Claude profile leaked a key or widened its model/group contract.");
        service.Rollback(claude, false);
        Assert(originalFiles.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)),
            "Claude rollback touched the user's original account/profile files.");
        Assert(File.ReadAllText(privateSetting) == beforeClaude && File.ReadAllText(meta) == beforeMeta, "Claude restore did not preserve the prior independent API profile.");
        Assert(File.ReadAllText(gatewayPath) == beforeGateway, "Claude rollback did not restore the exact prior gateway profile.");
        SafeFiles.AtomicWrite(setting, "[1,2]");
        service.Prepare(claude, profile, [messages], helper);
        Assert(File.ReadAllText(setting) == "[1,2]", "Independent API setup read or replaced unrelated original-account JSON.");
        service.Apply(claude, false);
        SafeFiles.AtomicWrite(privateSetting, "[1,2]"); Reject(() => service.Prepare(claude, profile, [messages], helper));
        Assert(File.ReadAllText(privateSetting) == "[1,2]", "Invalid independent vendor JSON was overwritten.");
        return Task.CompletedTask;
    }
}
