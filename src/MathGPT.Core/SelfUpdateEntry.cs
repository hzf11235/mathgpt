using System.Diagnostics;
using System.Text.Json;

namespace MathGPT.Core;

// Headless update check for diagnostics and an isolated real upgrade test. The
// application supplies its own executable directory; no URL or target argument
// can redirect this entry point to another installation.
public static class SelfUpdateEntry
{
    public static async Task<int> CheckAndExit(string installationRoot, string dataRoot, string version,
        string platform, string architecture)
    {
        var protectedDirectories = new List<string>();
        var settings = Path.Combine(dataRoot, "settings.json");
        SafeFiles.NoLinks(settings);
        if (File.Exists(settings))
        {
            if (new FileInfo(settings).Length > 4 * 1024 * 1024) throw new UserException("本机设置文件过大，已停止自动更新检查。");
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(settings), JsonData.Options);
            if (!string.IsNullOrWhiteSpace(state?.WorkDirectory)) protectedDirectories.Add(state.WorkDirectory);
        }
        var updater = new SelfUpdateService(new SelfUpdateOptions
        {
            InstallationRoot = installationRoot, DataRoot = dataRoot, CurrentVersion = version,
            Platform = platform, Architecture = architecture, ProtectedDirectories = protectedDirectories.ToArray()
        });
        var result = await updater.CheckAndStage();
        Console.WriteLine(result.Message);
        SafeFiles.AtomicWrite(Path.Combine(dataRoot, "self-update-check.json"), JsonSerializer.Serialize(new
        {
            status = result.Status.ToString(), version = result.Version, message = result.Message,
            hasPending = updater.HasPending
        }, JsonData.Options));
        var apply = updater.HasPending ? updater.BuildApplyStartInfo(Environment.ProcessId) : null;
        if (apply is not null) Process.Start(apply)?.Dispose();
        return 0;
    }
}
