namespace MathGPT.Core;

public enum AutomaticNetworkOperation { Updates, AccountRefresh }

/// <summary>Explicit, per-user network choices, shared by Windows, macOS and the installer.</summary>
public sealed class NetworkPreferences
{
    public const string FileName = "network-preferences.ini";
    public const string Notice = "自动更新会向 mathgpt.xyz 查询版本并下载更新；自动刷新会使用登录凭据查询账号、余额和模型。服务器可见 IP 等连接信息。更新请求不包含 API Key、聊天或工作文件。两项均可关闭，不影响手动操作。";
    private readonly string path;
    public bool Configured { get; private set; }
    public bool AutomaticUpdates { get; private set; }
    public bool AutomaticAccountRefresh { get; private set; }

    public NetworkPreferences(string root)
    {
        path = SafeFiles.Child(root, FileName);
        Reload();
    }

    public void Reload()
    {
        Configured = AutomaticUpdates = AutomaticAccountRefresh = false;
        if (!File.Exists(path)) return;
        string[] lines;
        try
        {
            SafeFiles.NoLinks(path);
            if (new FileInfo(path).Length > 4096) return;
            lines = File.ReadAllLines(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or UserException) { return; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line == "[Network]") { section = true; continue; }
            var separator = line.IndexOf('=');
            if (!section || separator < 1 || !values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim())) return;
        }
        if (values.Count != 3 || !values.TryGetValue("Version", out var version) || version != "1" ||
            !values.TryGetValue("AutoUpdates", out var updates) || updates is not ("0" or "1") ||
            !values.TryGetValue("AutoAccountRefresh", out var account) || account is not ("0" or "1")) return;
        AutomaticUpdates = updates == "1";
        AutomaticAccountRefresh = account == "1";
        Configured = true;
    }

    public void Save(bool automaticUpdates, bool automaticAccountRefresh)
    {
        SafeFiles.AtomicWrite(path, $"[Network]\nVersion=1\nAutoUpdates={(automaticUpdates ? 1 : 0)}\nAutoAccountRefresh={(automaticAccountRefresh ? 1 : 0)}\n");
        Reload();
    }

    public bool Allows(AutomaticNetworkOperation operation)
    {
        Reload();
        return Configured && (operation switch
        {
            AutomaticNetworkOperation.Updates => AutomaticUpdates,
            AutomaticNetworkOperation.AccountRefresh => AutomaticAccountRefresh,
            _ => false
        });
    }

    public async Task<bool> RunAutomatic(AutomaticNetworkOperation operation, Func<Task> request)
    {
        if (!Allows(operation)) return false;
        await request();
        return true;
    }

    public static bool ApplyCommand(string[] args, string root)
    {
        var index = Array.IndexOf(args, "--set-network-options");
        if (index < 0) return false;
        if ((index != 0 && (index != 2 || args[0] != "--data-dir")) || index + 3 != args.Length ||
            args[index + 1] is not ("0" or "1") || args[index + 2] is not ("0" or "1"))
            throw new UserException("联网选项无效。");
        Directory.CreateDirectory(SafeFiles.DirectoryPath(root));
        new NetworkPreferences(root).Save(args[index + 1] == "1", args[index + 2] == "1");
        return true;
    }
}
