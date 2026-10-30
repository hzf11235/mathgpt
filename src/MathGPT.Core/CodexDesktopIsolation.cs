using System.Diagnostics;
using System.Text.Json;

namespace MathGPT.Core;

// CODEX_HOME alone does not isolate the Electron account/cookie store or its
// single-instance lock. Both locations must be independent of the user's app.
public static class CodexDesktopIsolation
{
    private sealed record Session(int ProcessId, long StartedUtcTicks, string Executable, string ConfigurationDirectory);
    private const string SessionFile = "mathgpt-launch-session.json";

    public static string ConfigurationDirectory(StateStore store, ConnectionProfile profile) =>
        SafeFiles.Child(store.Root, "client-profiles/codexdesktop/" + UrlPolicy.SafeId(profile.Id));

    public static string ElectronDirectory(string configurationDirectory) =>
        SafeFiles.Child(configurationDirectory, "electron-user-data");

    public static bool IsConfigured(StateStore store, ClientInstallation install, ConnectionProfile profile) =>
        install.Kind == ClientKind.CodexDesktop && install.ProfileId == profile.Id &&
        SamePath(install.ConfigDirectory, ConfigurationDirectory(store, profile));

    public static ProcessStartInfo LaunchInfo(StateStore store, ClientInstallation install, ConnectionProfile profile)
    {
        if (!IsConfigured(store, install, profile))
            throw new UserException("请重新配置 Codex 的独立连接；原账号配置不会改动。");
        SafeFiles.NoLinks(install.Executable);
        SafeFiles.NoLinks(install.ConfigDirectory);
        ValidateRuntimeSupport(install);
        var electron = ElectronDirectory(install.ConfigDirectory);
        SafeFiles.NoLinks(electron);
        Directory.CreateDirectory(electron);
        var start = new ProcessStartInfo(install.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = install.WorkDirectory.Length > 0 ? install.WorkDirectory : install.ConfigDirectory
        };
        // A launcher may itself be started by an agent. Do not inherit its
        // account tokens, app-server overrides or Electron's Node-only mode.
        foreach (var name in start.Environment.Keys.ToArray())
            if (new[] { "CODEX_", "OPENAI_", "ANTHROPIC_", "CLAUDE_", "MATHGPT_" }
                .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                name.Equals("ELECTRON_RUN_AS_NODE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(name);
        start.Environment["CODEX_HOME"] = install.ConfigDirectory;
        start.Environment["CODEX_ELECTRON_USER_DATA_PATH"] = electron;
        start.ArgumentList.Add("--user-data-dir=" + electron);
        return start;
    }

    public static void Launch(StateStore store, ClientInstallation install, ConnectionProfile profile)
    {
        var existing = Running(store, install);
        using var process = Process.Start(LaunchInfo(store, install, profile)) ??
            throw new UserException("无法打开 Codex，请重新检测安装。");
        // A second launch delegates to the instance for this exact user-data
        // directory. Never replace its lease with the short-lived delegate.
        if (existing || process.HasExited) return;
        var session = new Session(process.Id, process.StartTime.ToUniversalTime().Ticks,
            Path.GetFullPath(install.Executable), Path.GetFullPath(install.ConfigDirectory));
        SafeFiles.AtomicWrite(SafeFiles.Child(install.ConfigDirectory, SessionFile), JsonSerializer.Serialize(session, JsonData.Options));
    }

    public static bool Running(StateStore store, ClientInstallation install)
    {
        if (install.Kind != ClientKind.CodexDesktop || install.ProfileId.Length == 0) return false;
        var profile = store.State.Profiles.FirstOrDefault(p => p.Id == install.ProfileId);
        if (profile is null || !IsConfigured(store, install, profile)) return false;
        var path = SafeFiles.Child(install.ConfigDirectory, SessionFile);
        SafeFiles.NoLinks(path);
        if (!File.Exists(path)) return false;
        try
        {
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(path), JsonData.Options);
            if (session is null || !SamePath(session.ConfigurationDirectory, install.ConfigDirectory)) return false;
            using var process = Process.GetProcessById(session.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == session.StartedUtcTicks &&
                SamePath(process.MainModule?.FileName ?? "", session.Executable);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception)
        {
            // A known isolated process that cannot be inspected must not be
            // treated as stopped while its configuration is being changed.
            return true;
        }
        catch (JsonException) { throw new UserException("Codex 独立连接的运行记录损坏，请正常退出该窗口后重新配置。"); }
    }

    public static void ValidateRuntimeSupport(ClientInstallation install)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(install.Executable))!;
        var asar = OperatingSystem.IsMacOS()
            ? Path.Combine(Path.GetDirectoryName(directory)!, "Resources", "app.asar")
            : Path.Combine(directory, "resources", "app.asar");
        if (!SupportsIsolation(asar))
            throw new UserException("此版本 Codex 未确认支持独立连接。请更新官方客户端后重试；不会替换原登录配置。");
    }

    // Read only the vendor's startup source. Unknown layouts fail closed rather
    // than silently reverting to the user's global account/configuration.
    public static bool SupportsIsolation(string asarPath)
    {
        if (!File.Exists(asarPath)) return false;
        SafeFiles.NoLinks(asarPath);
        try
        {
            using var stream = File.OpenRead(asarPath);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 16 || reader.ReadUInt32() != 4) return false;
            var headerSize = reader.ReadUInt32();
            reader.ReadUInt32();
            var jsonSize = reader.ReadUInt32();
            if (headerSize is < 8 or > 8_000_000 || jsonSize == 0 || jsonSize > headerSize - 8 ||
                8L + headerSize > stream.Length) return false;
            using var header = JsonDocument.Parse(reader.ReadBytes((int)jsonSize));
            var baseOffset = 8L + headerSize;
            var home = false; var dataAndLock = false; long total = 0;
            foreach (var (name, entry) in Entries(header.RootElement, ""))
            {
                if (!name.StartsWith(".vite/build/", StringComparison.Ordinal) || !name.EndsWith(".js", StringComparison.Ordinal) ||
                    entry.TryGetProperty("unpacked", out var unpacked) && unpacked.ValueKind == JsonValueKind.True) continue;
                var size = entry.GetProperty("size").GetInt32();
                var offset = long.Parse(entry.GetProperty("offset").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                if (size < 0 || size > 32_000_000 || offset < 0 || offset > stream.Length - baseOffset - size ||
                    (total += size) > 128_000_000) return false;
                stream.Position = baseOffset + offset;
                var text = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(size));
                home |= text.Contains("CODEX_HOME", StringComparison.Ordinal);
                dataAndLock |= text.Contains("CODEX_ELECTRON_USER_DATA_PATH", StringComparison.Ordinal) &&
                    text.Contains("userData", StringComparison.Ordinal) && text.Contains("requestSingleInstanceLock", StringComparison.Ordinal);
                if (home && dataAndLock) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or OverflowException or KeyNotFoundException or InvalidOperationException) { }
        return false;
    }

    private static IEnumerable<(string Name, JsonElement Entry)> Entries(JsonElement node, string prefix)
    {
        if (!node.TryGetProperty("files", out var files)) yield break;
        foreach (var file in files.EnumerateObject())
        {
            var name = prefix + file.Name;
            if (file.Value.TryGetProperty("files", out _))
                foreach (var entry in Entries(file.Value, name + "/")) yield return entry;
            else yield return (name, file.Value);
        }
    }

    private static bool SamePath(string left, string right) => left.Length > 0 && right.Length > 0 &&
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
