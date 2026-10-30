using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace MathGPT.Core;

public sealed record ClaudeDesktopProfile(string Root, string LocalData, string RoamingData,
    string UserData, string Library, string CodeConfig);

/// <summary>
/// Per-launch vendor profiles. Nothing here changes a machine/user environment variable,
/// copies an existing sign-in, or writes a vendor's ordinary application-data directory.
/// </summary>
public static class ClaudeHarnessIsolation
{
    private sealed record Session(int ProcessId, long StartedUtcTicks, string Executable, string ConfigurationDirectory);
    public static ClaudeDesktopProfile ClaudeProfile(StateStore store, ConnectionProfile profile,
        bool? windows = null)
    {
        var root = ProfileRoot(store, ClientKind.ClaudeDesktop, profile);
        var local = SafeFiles.Child(root, "Local");
        var roaming = SafeFiles.Child(root, "Roaming");
        // Claude's signed Windows build deliberately ignores CLAUDE_USER_DATA_DIR.
        // Its supported 3p loader instead resolves LOCALAPPDATA/Claude-3p, then sets
        // Electron userData to that root before acquiring its single-instance lock.
        var userData = windows ?? OperatingSystem.IsWindows()
            ? SafeFiles.Child(local, "Claude-3p")
            : SafeFiles.Child(root, "Claude-3p");
        return new(root, local, roaming, userData, SafeFiles.Child(userData, "configLibrary"),
            SafeFiles.Child(root, "code"));
    }

    public static string ProfileRoot(StateStore store, ClientKind kind, ConnectionProfile profile)
    {
        if (kind is not ClientKind.ClaudeDesktop and not ClientKind.Harness)
            throw new UserException("这个客户端没有使用此隔离配置。");
        return SafeFiles.Child(store.Root, "client-profiles/" + kind.ToString().ToLowerInvariant() + "/" + UrlPolicy.SafeId(profile.Id));
    }

    public static ProcessStartInfo ClaudeLaunch(StateStore store, ClientInstallation install,
        ConnectionProfile profile)
    {
        if (install.Kind != ClientKind.ClaudeDesktop) throw new UserException("请选择 Claude Code 桌面版。");
        var layout = ClaudeProfile(store, profile);
        if (install.ProfileId != profile.Id || !SamePath(install.ConfigDirectory, layout.Library))
            throw new UserException("请先重新配置 Claude；原账号与 MathGPT 连接将分开保存。");
        if (install.ConfigPending) throw new UserException("请先应用待生效的 Claude 配置。");
        var start = DirectLaunch(install);
        ClearProviderEnvironment(start);
        Directory.CreateDirectory(layout.UserData);
        Directory.CreateDirectory(layout.CodeConfig);
        // --user-data-dir is consumed by Electron before the application's JS and
        // scopes its cookies and single-instance lock. Windows 3p resolves the same
        // path through the private LOCALAPPDATA root below.
        start.ArgumentList.Add("--user-data-dir=" + layout.UserData);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(layout.RoamingData);
            start.Environment["LOCALAPPDATA"] = layout.LocalData;
            start.Environment["APPDATA"] = layout.RoamingData;
        }
        start.Environment["CLAUDE_CONFIG_DIR"] = layout.CodeConfig;
        return start;
    }

    public static void RequireRuntimeSupport(ClientInstallation install) => VendorProfileSupport.Require(install);

    public static bool Running(StateStore store, ClientInstallation install)
        => Running(store, install, false);

    private static bool Running(StateStore store, ClientInstallation install, bool ownsLaunchGate)
    {
        if (!TryScopedDirectory(store, install, out var directory)) return false;
        var path = SafeFiles.Child(directory, "mathgpt-launch-session.json");
        if (!ownsLaunchGate)
        {
            var gate = SafeFiles.Child(directory, "mathgpt-launch.lock");
            if (File.Exists(gate))
                try { using var probe = new FileStream(gate, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
        }
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 16_384) return true;
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(path), JsonData.Options);
            if (session is null || session.ProcessId <= 0 || !SamePath(session.ConfigurationDirectory, directory)) return true;
            using var process = Process.GetProcessById(session.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != session.StartedUtcTicks) return false;
            var executable = process.MainModule?.FileName;
            return executable is null || SamePath(executable, session.Executable);
        }
        catch (ArgumentException) { return false; } // The PID has exited, or has been reused.
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // An uninspectable known PID must not permit config overwrite.
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        catch (JsonException) { return true; }
    }

    /// <returns>False when this exact API profile is already open; its lease is preserved.</returns>
    public static bool Launch(StateStore store, ClientInstallation install, ConnectionProfile profile,
        ProcessStartInfo? prepared = null)
    {
        if (!TryScopedDirectory(store, install, out var directory) || install.ProfileId != profile.Id)
            throw new UserException("请先为此客户端配置独立连接。");
        var start = prepared ?? (install.Kind == ClientKind.ClaudeDesktop
            ? ClaudeLaunch(store, install, profile)
            : throw new UserException("DeepSeek Harness 尚未准备连接参数。"));
        ValidatePreparedLaunch(store, install, profile, start);
        Directory.CreateDirectory(directory);
        var lease = SafeFiles.Child(directory, "mathgpt-launch-session.json");
        var gate = SafeFiles.Child(directory, "mathgpt-launch.lock");
        using var ownership = new FileStream(gate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Running(store, install, true)) return false;
        RequireRuntimeSupport(install);
        using var process = Process.Start(start) ?? throw new UserException("客户端未能启动。");
        try
        {
            SafeFiles.AtomicWrite(lease, JsonSerializer.Serialize(new Session(process.Id,
                process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(install.Executable), directory), JsonData.Options));
        }
        catch (InvalidOperationException)
        {
            throw new UserException("客户端启动后已退出，请检查安装或重新检测客户端。");
        }
        return true;
    }

    public static void IsolateHarness(StateStore store, ClientInstallation install,
        ConnectionProfile profile, ProcessStartInfo start)
    {
        var root = ProfileRoot(store, ClientKind.Harness, profile);
        if (install.Kind != ClientKind.Harness || install.ProfileId != profile.Id ||
            !SamePath(install.ConfigDirectory, root) || start.UseShellExecute ||
            !SamePath(start.FileName, install.Executable))
            throw new UserException("请先重新配置 DeepSeek Harness 的独立连接。");
        if (start.ArgumentList.Any(argument => argument.StartsWith("--user-data-dir", StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(start.Arguments))
            throw new UserException("客户端已有额外启动参数，请重新检测安装。");
        var shell = SafeFiles.Child(root, "electron");
        Directory.CreateDirectory(shell);
        start.Environment["DSH_HOME"] = root;
        // DSH_HOME owns the backend credentials/sessions/plugins; Electron owns
        // additional account cookies and its single-instance lock independently.
        start.ArgumentList.Add("--user-data-dir=" + shell);
    }

    public static void ClearProviderEnvironment(ProcessStartInfo start)
    {
        if (start.UseShellExecute) throw new UserException("隔离连接需要直接启动客户端。");
        var exact = new HashSet<string>(["MATHGPT_API_KEY", "DEEPSEEK_API_KEY", "DSH_HOME", "CODEX_HOME",
            "DISABLE_AUTOUPDATER", "ELECTRON_RUN_AS_NODE", "NODE_OPTIONS"], StringComparer.OrdinalIgnoreCase);
        foreach (var name in start.Environment.Keys.Where(name =>
            name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || exact.Contains(name)).ToArray())
            start.Environment.Remove(name);
    }

    private static bool TryScopedDirectory(StateStore store, ClientInstallation install, out string directory)
    {
        directory = "";
        if (install.Kind is not ClientKind.ClaudeDesktop and not ClientKind.Harness) return false;
        var profile = store.State.Profiles.FirstOrDefault(item => item.Id == install.ProfileId);
        if (profile is null) return false;
        var expected = install.Kind == ClientKind.ClaudeDesktop ? ClaudeProfile(store, profile).Library :
            ProfileRoot(store, ClientKind.Harness, profile);
        if (!SamePath(install.ConfigDirectory, expected)) return false;
        directory = expected;
        return true;
    }

    private static void ValidatePreparedLaunch(StateStore store, ClientInstallation install,
        ConnectionProfile profile, ProcessStartInfo start)
    {
        var userData = install.Kind == ClientKind.ClaudeDesktop ? ClaudeProfile(store, profile).UserData :
            SafeFiles.Child(ProfileRoot(store, ClientKind.Harness, profile), "electron");
        if (start.UseShellExecute || !SamePath(start.FileName, install.Executable) || start.Arguments.Length > 0 ||
            start.ArgumentList.Count is < 1 or > 2 || start.ArgumentList[0] != "--user-data-dir=" + userData ||
            start.ArgumentList.Count == 2 && (install.Kind != ClientKind.ClaudeDesktop || start.ArgumentList[1] != "--startup"))
            throw new UserException("客户端隔离参数已变化，请重新配置。");
        SafeFiles.NoLinks(start.FileName);
        SafeFiles.NoLinks(userData);
        if (install.Kind == ClientKind.Harness)
        {
            if (!SamePath(EnvironmentValue(start, "DSH_HOME"), install.ConfigDirectory))
                throw new UserException("DeepSeek Harness 数据目录不匹配，请重新配置。");
        }
        else
        {
            var layout = ClaudeProfile(store, profile);
            if (start.Environment.ContainsKey("CLAUDE_USER_DATA_DIR") ||
                !SamePath(EnvironmentValue(start, "CLAUDE_CONFIG_DIR"), layout.CodeConfig) ||
                OperatingSystem.IsWindows() && (!SamePath(EnvironmentValue(start, "LOCALAPPDATA"), layout.LocalData) ||
                    !SamePath(EnvironmentValue(start, "APPDATA"), layout.RoamingData)))
                throw new UserException("Claude 数据目录不匹配，请重新配置。");
        }
    }

    private static string EnvironmentValue(ProcessStartInfo start, string key) =>
        start.Environment.TryGetValue(key, out var value) ? value ?? "" : "";

    private static ProcessStartInfo DirectLaunch(ClientInstallation install)
    {
        if (!Path.IsPathFullyQualified(install.Executable) || !File.Exists(install.Executable))
            throw new UserException("客户端入口不存在，请重新检测安装。");
        SafeFiles.NoLinks(install.Executable);
        var work = SafeFiles.DirectoryPath(install.WorkDirectory);
        SafeFiles.Writable(work);
        // explorer.exe AppsFolder activation does not preserve the selected child's
        // environment. Direct full-trust EXE activation is required for this profile.
        return new(install.Executable) { UseShellExecute = false, WorkingDirectory = work };
    }

    private static bool SamePath(string first, string second) => first.Length > 0 &&
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
