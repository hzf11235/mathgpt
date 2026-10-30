using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MathGPT.Core;

// CC Switch is an independent MIT tool. We import a provider only on a user
// gesture; it shows its own confirmation and owns the protocol conversion.
public static class SwitchIntegration
{
    public const string Version = "4.0.21";
    public static string InstallerUrl => "https://github.com/farion1231/cc-switch/releases/download/v" + Version +
        "/CC-Switch-v" + Version + "-Windows" + (PackageService.Architecture == "arm64" ? "-arm64" : "") + ".msi";
    public static bool CanRoute(ClientKind kind, ModelRoute route) => route.Available && route.Protocols.Count > 0 &&
        kind is ClientKind.CodexDesktop or ClientKind.ClaudeDesktop;
    public static string Format(WireProtocol protocol) => protocol switch
    {
        WireProtocol.Anthropic => "anthropic", WireProtocol.Responses => "openai_responses", _ => "openai_chat"
    };
    public static string BuildImport(ClientKind kind, ConnectionProfile profile, ModelRoute route, string key)
    {
        if (kind != ClientKind.CodexDesktop || !CanRoute(kind, route) || route.Id != profile.Model ||
            route.Group != profile.Group || !route.Protocols.Contains(profile.Protocol))
            throw new UserException("该组合需要手动添加 Claude Desktop 模型映射，请按接入窗口指引操作。");
        if (!Regex.IsMatch(profile.Model, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,191}$") ||
            key.Length is < 1 or > 8192 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            throw new UserException("模型或密钥格式无效，请重新保存连接。");
        var baseUrl = UrlPolicy.Base(profile.BaseUrl).AbsoluteUri.TrimEnd('/');
        if (profile.Protocol == WireProtocol.Anthropic) baseUrl = ClientService.AnthropicOrigin(baseUrl);
        var fields = new Dictionary<string, string>
        {
            ["resource"] = "provider", ["app"] = "codex", ["name"] = "MathGPT · " + profile.Model,
            ["endpoint"] = baseUrl, ["apiKey"] = key, ["apiFormat"] = Format(profile.Protocol),
            ["model"] = profile.Model, ["homepage"] = "https://mathgpt.xyz",
            // Import never silently rewrites the currently running Codex host.
            ["enabled"] = "false"
        };
        return "ccswitch://v1/import?" + string.Join("&", fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));
    }
    public static bool Installed
    {
        get { using var key = Registry.ClassesRoot.OpenSubKey(@"ccswitch\shell\open\command"); return key?.GetValue(null) is string; }
    }
    public static bool Running => new[] { "cc-switch", "CC Switch", "CCSwitch" }.Any(name =>
        Process.GetProcessesByName(name).Any(p => { using (p) return !p.HasExited; }));
    public static void Import(ClientKind kind, ConnectionProfile profile, ModelRoute route, string key)
    {
        if (!Installed) throw new UserException("请先下载并安装 CC Switch，打开一次后再导入。");
        var link = BuildImport(kind, profile, route, key);
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { throw new UserException("无法打开 CC Switch，请先完成安装并打开一次，再重试导入。"); }
    }
}
