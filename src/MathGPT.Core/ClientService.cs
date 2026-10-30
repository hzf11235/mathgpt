using System.Diagnostics;
using System.Text.Json;

namespace MathGPT.Core;

public sealed class ClientService(StateStore store)
{
    public bool Running(ClientInstallation install) => install.Kind == ClientKind.CodexDesktop
        ? CodexDesktopIsolation.Running(store, install)
        : install.Kind is ClientKind.ClaudeDesktop or ClientKind.Harness
            ? ClaudeHarnessIsolation.Running(store, install) : false;
    public static string DisplayName(ClientKind kind) => kind switch
    { ClientKind.Harness => "DeepSeek Harness", ClientKind.Codex => "Codex CLI", ClientKind.CodexDesktop => "Codex 桌面版",
      ClientKind.ClaudeDesktop => "Claude Code 桌面版", _ => "Claude Code CLI" };
    public static bool Supports(ClientKind kind, ModelRoute route) => route.Available && kind switch
    {
        ClientKind.Codex or ClientKind.CodexDesktop or ClientKind.Claude or ClientKind.ClaudeDesktop => route.Protocols.Count > 0,
        _ => route.Protocols.Count > 0
    };
    public void Configure(ClientInstallation install, ConnectionProfile profile, ModelRoute route, IReadOnlyList<ModelRoute>? catalog = null, string? helper = null)
    {
        if (profile.Model != route.Id || profile.Group != route.Group || !Supports(install.Kind, route))
            throw new UserException("此客户端不支持所选模型、分组或协议，请更换选择。");
        if (!route.Protocols.Contains(profile.Protocol)) throw new UserException("所选协议在当前分组中不可用。");
        if (profile.Reasoning.Length > 0 && !ReasoningPolicy.Allowed(profile.Model).Contains(profile.Reasoning))
            throw new UserException("此型号没有声明所选推理强度，请选择“服务默认”。");
        install.LocalRouting = false;
        install.BuiltinRouting = BuiltinGateway.Required(install.Kind, profile, catalog ?? [route]);
        if (DesktopConfiguration.IsDesktop(install.Kind))
        {
            var desktop = new DesktopConfiguration(store);
            desktop.Prepare(install, profile, catalog ?? [route], helper ?? Environment.ProcessPath ?? "");
            var running = install.Kind == ClientKind.CodexDesktop ? CodexDesktopIsolation.Running(store, install) : ClaudeHarnessIsolation.Running(store, install);
            if (!running) desktop.Apply(install, false);
            return;
        }
        if (install.Kind == ClientKind.Harness && profile.Protocol == WireProtocol.ChatCompletions &&
            profile.Model is "gpt-6.1-sol" or "gpt-6-astra")
            throw new UserException("此 GPT 型号的工具调用需要 Responses，请将协议切换为 Responses 后重试。");
        var home = SafeFiles.Child(store.Root, "client-profiles/" + install.Kind.ToString().ToLowerInvariant() + "/" + UrlPolicy.SafeId(profile.Id));
        Directory.CreateDirectory(home);
        install.ConfigDirectory = home;
        install.ProfileId = profile.Id;
        install.WorkDirectory = SafeFiles.DirectoryPath(store.State.WorkDirectory);
        SafeFiles.Writable(install.WorkDirectory);
        if (profile.Group == "universal" && install.Kind is ClientKind.Codex or ClientKind.Claude)
            foreach (var (name, text) in MultiModelAgents.Create(install.Kind, catalog ?? [route]))
                WriteManaged(home, "agents/" + name, text);
        var gatewayAddress = install.BuiltinRouting ? new BuiltinGateway(store).Prepare(profile, catalog ?? [route]) : profile.BaseUrl;
        if (install.Kind == ClientKind.Codex)
        {
            var modelCatalog = SafeFiles.Child(home, "models.json");
            WriteManaged(home, "models.json", install.BuiltinRouting ? CompatibilityCatalog.Create(profile, catalog ?? [route]) :
                CompatibilityCatalog.CreateNative(profile, catalog ?? [route], null));
            var config = "model = " + Q(profile.Model) + "\nmodel_provider = \"mathgpt\"\n" +
                "model_catalog_json = " + Q(modelCatalog) + "\n" +
                "approval_policy = \"on-request\"\nsandbox_mode = \"read-only\"\n" +
                (profile.Reasoning.Length > 0 ? "model_reasoning_effort = " + Q(profile.Reasoning) + "\n" : "") +
                "\n[model_providers.mathgpt]\nname = \"MathGPT\"\nbase_url = " + Q(gatewayAddress.TrimEnd('/')) +
                "\nwire_api = \"responses\"\nenv_key = \"MATHGPT_API_KEY\"\nrequires_openai_auth = false\n" +
                "request_max_retries = 0\nstream_max_retries = 0\n";
            config += "supports_websockets = false\n";
            WriteManaged(home, "config.toml", config);
        }
        else if (install.Kind == ClientKind.Claude)
        {
            var scoped = ModelCatalogScope.Available(profile, catalog ?? [route]);
            var config = JsonSerializer.Serialize(new
            {
                model = profile.Model,
                availableModels = scoped.Select(r => r.Id).ToArray(),
                modelPicker = new { replaceBuiltInOptions = true, options = scoped.Select(r => new { model = r.Id, label = r.Id }).ToArray() },
                permissions = new { defaultMode = "default" },
                cleanupPeriodDays = 365
            }, JsonData.Options);
            WriteManaged(home, "settings.json", config);
        }
        else
        {
            var protocol = install.BuiltinRouting ? "openai-responses" : profile.Protocol switch { WireProtocol.Responses => "openai-responses",
                WireProtocol.Anthropic => "anthropic-messages", _ => "openai-completions" };
            var endpoint = install.BuiltinRouting ? gatewayAddress : profile.Protocol == WireProtocol.Anthropic ? AnthropicOrigin(profile.BaseUrl) : profile.BaseUrl.TrimEnd('/');
            var harnessEffort = profile.Reasoning == "none" ? "off" : profile.Reasoning;
            string Declarations(string id) => ReasoningPolicy.Allowed(id).Length > 0 ? "\n            reasoningEfforts:\n" +
                string.Join("\n", ReasoningPolicy.Allowed(id).Select(e => "              " + Q(e == "none" ? "off" : e) + ": " + Q(e))) : "";
            var declaredModels = ModelCatalogScope.Available(profile, catalog ?? [route]).Select(r => r.Id).ToArray();
            var modelDeclarations = string.Join("\n", declaredModels.Select(id => "          - id: " + Q(id) + "\n            name: " + Q(id) +
                Declarations(id)));
            var patch = "- id: llm-pi-ai\n  config:\n    providers:\n      mathgpt:\n" +
                "        displayName: MathGPT\n        apiKeyEnv: MATHGPT_API_KEY\n        baseURL: " + Q(endpoint) +
                "\n        api: " + protocol + "\n        retryPolicy:\n          mode: normal\n          maxRetries: 0\n" +
                (harnessEffort.Length > 0 ? "        reasoning: " + Q(harnessEffort) + "\n" : "") +
                "        models:\n" + modelDeclarations + "\n- id: agent-default-model\n  config:\n    provider: mathgpt\n    model: " + Q(profile.Model) +
                (harnessEffort.Length > 0 ? "\n    reasoningEffort: " + Q(harnessEffort) : "") + "\n";
            if (profile.Group == "universal")
                patch += "- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels:\n" +
                    string.Join("\n", declaredModels.Select(id => "      - provider: mathgpt\n        model: " + Q(id))) + "\n";
            WriteManaged(home, "profiles/desktop/cordis.patch.yml", patch);
        }
        store.Save();
    }
    private static string Q(string value) => JsonSerializer.Serialize(value);
    public static string AnthropicOrigin(string value)
    {
        // Anthropic SDK appends /v1/messages to a provider base URL.
        var root = value.TrimEnd('/');
        return root.EndsWith("/v1", StringComparison.Ordinal) ? root[..^3] : root;
    }
    private static void WriteManaged(string home, string relative, string content)
    {
        var path = SafeFiles.Child(home, relative);
        if (File.Exists(path))
        {
            SafeFiles.NoLinks(path + ".previous");
            File.Copy(path, path + ".previous", true);
        }
        SafeFiles.AtomicWrite(path, content);
    }
    public ProcessStartInfo LaunchInfo(ClientInstallation install, ConnectionProfile profile)
    {
        if (!File.Exists(install.Executable)) throw new UserException("客户端入口已不存在，请重新检测或安装。");
        if (install.ProfileId != profile.Id || string.IsNullOrEmpty(install.ConfigDirectory))
            throw new UserException("请先为客户端应用连接配置。");
        SafeFiles.NoLinks(install.Executable);
        if (DesktopConfiguration.IsDesktop(install.Kind))
        {
            if (install.ConfigPending) throw new UserException("请先退出客户端，再从 MathGPT 应用待生效配置。");
            return install.Kind == ClientKind.CodexDesktop ? CodexDesktopIsolation.LaunchInfo(store, install, profile) : ClaudeHarnessIsolation.ClaudeLaunch(store, install, profile);
        }
        var key = install.BuiltinRouting ? new BuiltinGateway(store).Ensure(profile).GetAwaiter().GetResult() : store.Key(profile) ?? throw new UserException("API Key 已失效或被删除，请重新保存连接。");
        var start = new ProcessStartInfo(install.Executable)
        {
            UseShellExecute = false, WorkingDirectory = install.WorkDirectory, CreateNoWindow = false
        };
        // Remove inherited provider credentials to keep the selected API profile unambiguous.
        foreach (var name in new[] { "OPENAI_API_KEY", "OPENAI_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
            "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_API_KEY_HELPER", "DEEPSEEK_API_KEY" })
            start.Environment.Remove(name);
        start.Environment["MATHGPT_API_KEY"] = key;
        if (install.Kind == ClientKind.Codex)
        {
            start.Environment["CODEX_HOME"] = install.ConfigDirectory;
            start.ArgumentList.Add("--cd"); start.ArgumentList.Add(install.WorkDirectory);
            start.ArgumentList.Add("--model"); start.ArgumentList.Add(profile.Model);
        }
        else if (install.Kind == ClientKind.Claude)
        {
            start.Environment["CLAUDE_CONFIG_DIR"] = install.ConfigDirectory;
            start.Environment["ANTHROPIC_BASE_URL"] = AnthropicOrigin(install.BuiltinRouting ? new BuiltinGateway(store).Address(profile) : profile.BaseUrl);
            start.Environment["ANTHROPIC_AUTH_TOKEN"] = key;
            start.Environment["ANTHROPIC_MODEL"] = profile.Model;
            foreach (var category in new[] { "HAIKU", "SONNET", "OPUS", "FABLE" })
                start.Environment["ANTHROPIC_DEFAULT_" + category + "_MODEL"] = profile.Model;
            start.Environment["CLAUDE_CODE_SUBAGENT_MODEL"] = profile.Model;
            start.Environment["DISABLE_AUTOUPDATER"] = "1";
            start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            start.Environment["DISABLE_TELEMETRY"] = "1";
            start.Environment["DISABLE_ERROR_REPORTING"] = "1";
            start.Environment["API_MAX_RETRIES"] = "0";
            start.ArgumentList.Add("--model"); start.ArgumentList.Add(profile.Model);
        }
        else
        {
            ClaudeHarnessIsolation.IsolateHarness(store, install, profile, start);
        }
        return start;
    }
    public void Launch(ClientInstallation install, ConnectionProfile profile)
    {
        if (OperatingSystem.IsWindows() && install.Kind == ClientKind.ClaudeDesktop &&
            ClaudeDesktopReadiness.BundledHostPath(install) is null)
            throw new UserException("Claude Code 本机组件尚未就绪。请从 MathGPT 的“客户端”点打开，自动检查完整安装包；不要在报错窗口反复重试。");
        if (install.LocalRouting)
        {
            if (!DesktopConfiguration.IsDesktop(install.Kind) || install.ProfileId != profile.Id)
                throw new UserException("请重新选择要接入的桌面连接。");
            if (!SwitchIntegration.Running) throw new UserException("请先打开 CC Switch，并为目标应用启动路由，再打开客户端。");
            // The external router owns live config. Never apply a stale native journal over it.
            Process.Start(DesktopPackages.LaunchInfo(install))?.Dispose();
            return;
        }
        if (DesktopConfiguration.IsDesktop(install.Kind)) new DesktopConfiguration(store).Apply(install,
            install.Kind == ClientKind.CodexDesktop ? CodexDesktopIsolation.Running(store, install) : ClaudeHarnessIsolation.Running(store, install));
        if (install.Kind == ClientKind.CodexDesktop)
        {
            CodexDesktopIsolation.Launch(store, install, profile);
            return;
        }
        if (install.Kind is ClientKind.ClaudeDesktop or ClientKind.Harness)
        {
            ClaudeHarnessIsolation.Launch(store, install, profile, LaunchInfo(install, profile));
            return;
        }
        Process.Start(LaunchInfo(install, profile))?.Dispose();
    }
    public static bool AutomaticShortcutKind(ClientKind kind) => kind is ClientKind.Harness or ClientKind.CodexDesktop or ClientKind.ClaudeDesktop;
    public static string ShortcutName(ClientKind kind) => kind switch
    { ClientKind.Harness => "DeepSeek Harness", ClientKind.CodexDesktop => "Codex", ClientKind.ClaudeDesktop => "Claude", _ => DisplayName(kind) };
    public static string ShortcutPath(ClientKind kind, string? desktopDirectory = null) =>
        Path.Combine(desktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName(kind) + ".lnk");
    public void AutomaticShortcut(ClientInstallation install, string mathGptExecutable, bool replaceNewVendorShortcut = false)
    {
        if (AutomaticShortcutKind(install.Kind)) Shortcut(install, mathGptExecutable, replaceNewVendorShortcut: replaceNewVendorShortcut);
    }
    public string Shortcut(ClientInstallation install, string mathGptExecutable, string? desktopDirectory = null, bool replaceNewVendorShortcut = false)
    {
        var configured = install.ProfileId.Length > 0 && install.ConfigDirectory.Length > 0;
        var launch = configured ? new ProcessStartInfo(mathGptExecutable) :
            DesktopConfiguration.IsDesktop(install.Kind) ? DesktopPackages.LaunchInfo(install) : new ProcessStartInfo(install.Executable);
        var arguments = configured ? new[] { "--launch", install.Kind.ToString(), "--profile", UrlPolicy.SafeId(install.ProfileId), "--data-dir", store.Root } :
            launch.ArgumentList.ToArray();
        return WriteShortcut(ShortcutName(install.Kind), launch.FileName, arguments,
            install.Executable, "MathGPT managed client " + install.Kind, desktopDirectory,
            [mathGptExecutable, install.Executable, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")], replaceNewVendorShortcut);
    }
    public static string ApplicationShortcut(string mathGptExecutable, string? desktopDirectory = null) =>
        WriteShortcut("MathGPT", mathGptExecutable, [], mathGptExecutable, "MathGPT managed application", desktopDirectory, [mathGptExecutable], false);
    public void CreateAvailableShortcuts(string mathGptExecutable, string? desktopDirectory = null)
    {
        ApplicationShortcut(mathGptExecutable, desktopDirectory);
        foreach (var install in store.State.Clients.Where(c => AutomaticShortcutKind(c.Kind) && File.Exists(c.Executable)))
            Shortcut(install, mathGptExecutable, desktopDirectory);
    }
    private static string WindowsArgument(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static string WriteShortcut(string name, string target, IEnumerable<string> arguments, string icon, string description, string? desktopDirectory, string[] ownedTargets, bool replaceNewVendorShortcut)
    {
        var root = SafeFiles.DirectoryPath(desktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Directory.CreateDirectory(root);
        var path = SafeFiles.Child(root, name + ".lnk");
        SafeFiles.NoLinks(path);
        SafeFiles.NoLinks(target);
        if (!File.Exists(target)) throw new UserException("快捷方式的程序入口不存在。");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new UserException("Windows 快捷方式组件不可用。");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(path);
        try
        {
            if (File.Exists(path) && !replaceNewVendorShortcut &&
                (shortcut.Description != description || !ownedTargets.Any(value => Path.GetFullPath(value).Equals((string)shortcut.TargetPath, StringComparison.OrdinalIgnoreCase))))
                return path;
            shortcut.TargetPath = target;
            shortcut.Arguments = string.Join(" ", arguments.Select(WindowsArgument));
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.IconLocation = icon + ",0";
            shortcut.Description = description;
            shortcut.Save();
            return path;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
    public void Forget(ClientInstallation installation)
    {
        // Forgetting a client never invokes an uninstaller or removes vendor data.
        store.State.Clients.Remove(installation); store.Save();
    }
}
