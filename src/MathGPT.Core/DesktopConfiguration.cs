using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Syntax;

namespace MathGPT.Core;

// Changes are scoped to MathGPT-owned client profiles. The journal is DPAPI-only.
public sealed class DesktopConfiguration(StateStore store, string? userHome = null, string? localData = null)
{
    private readonly string home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string local = localData ?? (OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private sealed record Change(string Path, string? Before, string After);
    private sealed record Journal(List<Change> Changes);
    public static bool IsDesktop(ClientKind kind) => kind is ClientKind.CodexDesktop or ClientKind.ClaudeDesktop;
    public static bool Running(ClientKind kind)
    {
        var names = kind == ClientKind.CodexDesktop ? new[] { "Codex", "ChatGPT" } : ["Claude"];
        return names.Any(name => Process.GetProcessesByName(name).Any(p => { using (p) return !p.HasExited; }));
    }
    private static string Id(ClientInstallation install, string suffix) => "desktop-" + install.Kind + "-" + suffix;
    private static string Hash(string? value) => value is null ? "missing" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? Read(string path) { SafeFiles.NoLinks(path); return File.Exists(path) ? File.ReadAllText(path) : null; }
    public void Prepare(ClientInstallation install, ConnectionProfile profile, IReadOnlyList<ModelRoute> routes, string helper)
    {
        if (!IsDesktop(install.Kind) || !File.Exists(helper)) throw new UserException("桌面配置程序不存在，请重新安装 MathGPT。");
        if (install.Kind == ClientKind.CodexDesktop) CodexDesktopIsolation.ValidateRuntimeSupport(install);
        else ClaudeHarnessIsolation.RequireRuntimeSupport(install);
        SafeFiles.NoLinks(helper);
        var gatewayAddress = install.BuiltinRouting ? new BuiltinGateway(store).Prepare(profile, routes) : profile.BaseUrl;
        var fingerprint = Hash(JsonSerializer.Serialize(new { profile.Id, profile.Model, profile.Group, profile.Protocol,
            profile.BaseUrl, profile.Reasoning, helper, store.Root, install.BuiltinRouting, gatewayAddress,
            routes = routes.Select(r => new { r.Id, r.Group, r.Protocols }).OrderBy(r => r.Id).ToArray(), configurationVersion = 10 }));
        if (install.ConfigPending && install.ConfigurationFingerprint == fingerprint && store.Vault.Contains(Id(install, "pending"))) return;
        var changes = new List<Change>();
        void Add(string path, string value) => changes.Add(new(path, Read(path), value));
        var args = new[] { "--credential", UrlPolicy.SafeId(profile.Id), "--data-dir", store.Root };
        if (install.BuiltinRouting) args = [.. args, "--builtin"];
        if (profile.Group == "universal")
        {
            var folder = install.Kind == ClientKind.CodexDesktop
                ? Path.Combine(CodexDesktopIsolation.ConfigurationDirectory(store, profile), "agents")
                : Path.Combine(ClaudeHarnessIsolation.ClaudeProfile(store, profile).CodeConfig, "agents");
            foreach (var (name, text) in MultiModelAgents.Create(install.Kind, routes))
            {
                var path = Path.Combine(folder, name);
                var existing = Read(path);
                if (existing is not null && !existing.Contains(MultiModelAgents.Marker, StringComparison.Ordinal))
                    throw new UserException("已有同名自定义子代理，已保留原文件：" + name);
                Add(path, text);
            }
        }
        if (install.Kind == ClientKind.ClaudeDesktop && args.Any(a => a.Any(c => c is '"' or '%' || char.IsControl(c))))
            throw new UserException("Claude 凭据助手不支持含百分号或引号的数据路径，请选择普通本机目录。");
        if (install.Kind == ClientKind.CodexDesktop)
        {
            var codexHome = CodexDesktopIsolation.ConfigurationDirectory(store, profile);
            var path = Path.Combine(codexHome, "config.toml");
            var provider = "mathgpt_" + UrlPolicy.SafeId(profile.Id);
            var source = Read(path) ?? "";
            var roots = new Dictionary<string, string> { ["model"] = Q(profile.Model), ["model_provider"] = Q(provider),
                ["cli_auth_credentials_store"] = Q("file") };
            var removeRoots = new HashSet<string>();
            var catalog = SafeFiles.Child(store.Root, "desktop-catalogs/codex-" + UrlPolicy.SafeId(profile.Id) + ".json");
            var cache = Read(Path.Combine(codexHome, "models_cache.json"));
            Add(catalog, install.BuiltinRouting ? CompatibilityCatalog.Create(profile, routes) :
                CompatibilityCatalog.CreateNative(profile, routes, cache));
            roots["model_catalog_json"] = Q(catalog);
            if (install.BuiltinRouting) roots["web_search"] = Q("disabled");
            if (profile.Reasoning.Length > 0) roots["model_reasoning_effort"] = Q(profile.Reasoning);
            else removeRoots.Add("model_reasoning_effort");
            var tables = $"\n[model_providers.{provider}]\nname = \"MathGPT\"\nbase_url = {Q(gatewayAddress.TrimEnd('/'))}\nwire_api = \"responses\"\nrequires_openai_auth = false\nsupports_websockets = false\nrequest_max_retries = 0\nstream_max_retries = 0\n\n[model_providers.{provider}.auth]\ncommand = {Q(helper)}\nargs = {JsonSerializer.Serialize(args)}\ntimeout_ms = 10000\nrefresh_interval_ms = 60000\n";
            Add(path, PatchCodex(source, roots, provider, tables, removeRoots));
            install.ConfigDirectory = Path.GetDirectoryName(path)!;
        }
        else
        {
            var id = Guid.ParseExact(profile.Id, "N").ToString();
            var layout = ClaudeHarnessIsolation.ClaudeProfile(store, profile);
            var library = layout.Library;
            var modePath = Path.Combine(layout.UserData, "claude_desktop_config.json");
            var mode = Object(Read(modePath)); mode["deploymentMode"] = "3p"; Add(modePath, mode.ToJsonString(JsonData.Options));
            var metaPath = Path.Combine(library, "_meta.json");
            var meta = Object(Read(metaPath));
            if (meta["entries"] is not null && meta["entries"] is not JsonArray) throw new UserException("Claude 的配置索引格式异常，原文件已保留。");
            var entries = meta["entries"] as JsonArray ?? new JsonArray();
            meta["entries"] = null; meta["entries"] = entries;
            var entry = entries.OfType<JsonObject>().FirstOrDefault(e => e["id"]?.GetValue<string>() == id);
            if (entry is null) entries.Add(new JsonObject { ["id"] = id, ["name"] = "MathGPT · " + profile.Name });
            else entry["name"] = "MathGPT · " + profile.Name;
            meta["appliedId"] = id;
            var selected = ModelCatalogScope.Available(profile, routes)
                .Select(r => new { name = r.Id, labelOverride = r.Id }).ToArray();
            if (!selected.Any(m => m.name == profile.Model)) throw new UserException("该密钥没有可供 Claude Code 桌面版使用的 Messages 模型。");
            var gateway = new
            {
                inferenceProvider = "gateway", inferenceCredentialKind = "helper-script",
                inferenceGatewayBaseUrl = ClientService.AnthropicOrigin(gatewayAddress),
                inferenceGatewayAuthScheme = "bearer", inferenceCredentialHelperWindows = helper,
                inferenceCredentialHelperArgs = args, inferenceCredentialHelperTtlSec = 60,
                inferenceCredentialHelperTimeoutSec = 10, inferenceModels = selected, modelDiscoveryEnabled = false,
                modelCatalogEnabled = false, isClaudeCodeForDesktopEnabled = true,
                desktopHome = "off", coworkTabEnabled = false,
                chatTabEnabled = false, chatAdvancedFileAnalysisEnabled = false,
                deploymentDisplayName = "MathGPT",
                disableEssentialTelemetry = true, disableNonessentialTelemetry = true,
                disableNonessentialServices = true, disableAutoUpdates = false,
                skipWebFetchPreflight = true
            };
            var gatewayPath = Path.Combine(library, id + ".json");
            var gatewayJson = Object(Read(gatewayPath));
            var managed = JsonSerializer.SerializeToNode(gateway, JsonData.Options)!.AsObject();
            if (OperatingSystem.IsMacOS()) { managed.Remove("inferenceCredentialHelperWindows"); managed["inferenceCredentialHelper"] = helper; }
            gatewayJson.Remove(OperatingSystem.IsMacOS() ? "inferenceCredentialHelperWindows" : "inferenceCredentialHelper");
            foreach (var field in managed) gatewayJson[field.Key] = field.Value?.DeepClone();
            Add(gatewayPath, gatewayJson.ToJsonString(JsonData.Options));
            Add(metaPath, meta.ToJsonString(JsonData.Options));
            install.ConfigDirectory = library;
        }
        // Discard no-op writes so repeat configuration retains the original rollback point.
        changes = changes.Where(c => c.Before != c.After).ToList();
        install.ProfileId = profile.Id; install.WorkDirectory = store.State.WorkDirectory; install.ConfigurationFingerprint = fingerprint;
        if (changes.Count == 0) { install.ConfigPending = false; install.ConfigurationMessage = "当前连接已生效"; store.Save(); return; }
        store.Vault.Put(Id(install, "pending"), JsonSerializer.Serialize(new Journal(changes), JsonData.Options));
        install.ConfigPending = true; install.ConfigurationMessage = "配置已准备；正常退出客户端后点打开"; store.Save();
    }
    public void Apply(ClientInstallation install, bool appRunning)
    {
        if (!install.ConfigPending) return;
        if (appRunning) throw new UserException("配置已准备。请正常退出 " + ClientService.DisplayName(install.Kind) + "，再点“打开”。原会话继续保留。");
        var journal = JsonSerializer.Deserialize<Journal>(store.Vault.Get(Id(install, "pending")) ?? "", JsonData.Options)
            ?? throw new UserException("待应用配置不存在，请重新点击配置。");
        foreach (var change in journal.Changes)
        {
            var current = Hash(Read(change.Path));
            if (current != Hash(change.Before) && current != Hash(change.After)) throw new UserException("客户端配置已被其他程序修改，请重新点击配置。不会覆盖这些新改动。");
        }
        store.Vault.Put(Id(install, "rollback"), JsonSerializer.Serialize(journal, JsonData.Options));
        var written = new List<Change>();
        try
        {
            foreach (var change in journal.Changes)
            {
                if (Hash(Read(change.Path)) == Hash(change.Before)) { SafeFiles.AtomicWrite(change.Path, change.After); written.Add(change); }
            }
        }
        catch
        {
            foreach (var change in written.AsEnumerable().Reverse())
                if (Hash(Read(change.Path)) == Hash(change.After)) Restore(change);
            throw;
        }
        install.ConfigPending = false; install.ConfigurationMessage = "当前连接已生效"; store.Save();
        store.Vault.Remove(Id(install, "pending"));
    }
    public void Rollback(ClientInstallation install, bool appRunning)
    {
        if (appRunning) throw new UserException("请先正常退出客户端，再恢复配置。");
        var saved = store.Vault.Get(Id(install, "rollback")) ?? throw new UserException("没有可恢复的配置。");
        var journal = JsonSerializer.Deserialize<Journal>(saved, JsonData.Options)!;
        foreach (var change in journal.Changes)
        {
            var current = Hash(Read(change.Path));
            if (current != Hash(change.After) && current != Hash(change.Before)) throw new UserException("配置有后续改动，已停止恢复以保留它们。");
        }
        foreach (var change in journal.Changes.AsEnumerable().Reverse())
            if (Hash(Read(change.Path)) == Hash(change.After)) Restore(change);
        install.ProfileId = ""; install.ConfigPending = false; install.ConfigurationMessage = "已恢复先前配置"; store.Save();
        store.Vault.Remove(Id(install, "rollback")); store.Vault.Remove(Id(install, "pending"));
    }
    private static void Restore(Change change)
    {
        SafeFiles.NoLinks(change.Path);
        if (change.Before is null) File.Delete(change.Path); else SafeFiles.AtomicWrite(change.Path, change.Before);
    }
    private static JsonObject Object(string? value)
    {
        if (value is null) return new();
        try { return JsonNode.Parse(value) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new UserException("客户端 JSON 配置格式异常，原文件已保留。"); }
    }
    private static string Q(string value) => JsonSerializer.Serialize(value);
    public static string PatchCodex(string source, Dictionary<string, string> roots, string provider, string providerTables, IReadOnlySet<string>? removeRoots = null)
    {
        var parsed = Toml.Parse(source);
        if (parsed.HasErrors) throw new UserException("Codex 原配置存在 TOML 语法错误，请先修复；原文件已保留。");
        // Use the TOML AST spans: regex cannot distinguish tables, multiline strings or quoted keys.
        var edits = new List<(int Start, int Length, string Text)>();
        var missing = new Dictionary<string, string>(roots);
        foreach (var item in parsed.KeyValues)
        {
            if (item.Key is not { } key || key.DotKeys.Any() || item.Value is not { } itemValue) continue;
            var name = key.Key switch { BareKeySyntax k => k.Key?.ToString().Trim() ?? "", StringValueSyntax k => k.Value, _ => "" };
            if (name is not null && removeRoots?.Contains(name) == true)
            {
                edits.Add((key.Span.Offset, itemValue.Span.End.Offset + 1 - key.Span.Offset, "")); continue;
            }
            if (name is null || !missing.Remove(name, out var value)) continue;
            var span = itemValue.Span;
            edits.Add((span.Offset, span.Length, value));
        }
        var owned = new[] { "model_providers." + provider, "model_providers." + provider + ".auth" };
        foreach (var table in parsed.Tables.OfType<TableSyntax>())
        {
            if (table.Name is null || table.OpenBracket is null) continue;
            var name = source.Substring(table.Name.Span.Offset, table.Name.Span.Length);
            if (!owned.Contains(name, StringComparer.Ordinal)) continue;
            var start = table.OpenBracket.Span.Offset;
            var end = table.Span.End.Offset + 1;
            // Only this application's deterministic provider table is replaced.
            edits.Add((start, end - start, ""));
        }
        var result = source;
        foreach (var edit in edits.OrderByDescending(e => e.Start)) result = result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        result = string.Concat(missing.Select(k => k.Key + " = " + k.Value + "\n")) + result.TrimEnd() + "\n" + providerTables;
        if (Toml.Parse(result).HasErrors) throw new UserException("配置合并未通过 TOML 检查，原文件已保留。");
        return result;
    }
}
