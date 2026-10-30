namespace MathGPT.Core;

public enum ClaudeDesktopReadinessStatus
{
    Ready, MissingHostEngine, UnsafePath, InvalidHostEngine, Unreadable, SignatureRejected, Unsupported
}

public sealed record ClaudeDesktopReadinessReport(ClaudeDesktopReadinessStatus Status, string HostEnginePath, string Message)
{
    public bool Ready => Status == ClaudeDesktopReadinessStatus.Ready;
}

public enum ClaudeDesktopRepairAction { Reuse, UpgradeOffline, SameVersionConflict, InstalledVersionNewer, Blocked }

// Checks only the engine bundled with this installation. No old cache, user PATH,
// downloads, writable vendor directories or process launches participate.
public static class ClaudeDesktopReadiness
{
    public const long MinimumHostEngineBytes = 1_000_000;
    public const long MaximumHostEngineBytes = 1_000_000_000;

    public static Task<ClaudeDesktopReadinessReport> Check(ClientInstallation installation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Report(ClaudeDesktopReadinessStatus.Unsupported, "", "此依赖检查仅适用于 Windows Claude Code 桌面版。"));
        return Check(installation, Architecture(installation),
            (file, token) => DesktopPackages.Verify(file, ClientKind.ClaudeDesktop, token), ct);
    }

    // The explicit architecture and verifier also make path/policy fixtures independent
    // of installed applications and real vendor signatures.
    public static async Task<ClaudeDesktopReadinessReport> Check(ClientInstallation installation, string architecture,
        Func<string, CancellationToken, Task> verifySignature, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(verifySignature);
        if (installation.Kind != ClientKind.ClaudeDesktop)
            return Report(ClaudeDesktopReadinessStatus.Unsupported, "", "不是 Claude Code 桌面版安装。");
        string file;
        try { file = HostPath(installation, architecture); }
        catch (Exception ex) when (ex is UserException or ArgumentException or NotSupportedException)
        { return Report(ClaudeDesktopReadinessStatus.UnsafePath, "", "Claude 安装路径异常或包含链接，已停止依赖检查。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Report(ClaudeDesktopReadinessStatus.Unreadable, "", "无法安全读取 Claude 安装目录，请重新检测安装。"); }
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(file); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { return Report(ClaudeDesktopReadinessStatus.MissingHostEngine, file, "当前 Claude 安装缺少预置的 Claude Code 引擎，需要完整离线包。"); }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                return Report(ClaudeDesktopReadinessStatus.UnsafePath, file, "Claude Code 引擎路径不是普通文件，已停止接入。");
            // Hold a read-only lock throughout signature verification; never repair WindowsApps here.
            using var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (handle.Length is < MinimumHostEngineBytes or > MaximumHostEngineBytes)
                return Report(ClaudeDesktopReadinessStatus.InvalidHostEngine, file, "预置 Claude Code 引擎大小异常，不能作为可用安装。");
            if (handle.ReadByte() < 0)
                return Report(ClaudeDesktopReadinessStatus.InvalidHostEngine, file, "预置 Claude Code 引擎不可读。");
            ct.ThrowIfCancellationRequested();
            try { await verifySignature(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (UserException)
            { return Report(ClaudeDesktopReadinessStatus.SignatureRejected, file, "预置 Claude Code 引擎未通过 Anthropic 原厂签名校验，已停止接入。"); }
            ct.ThrowIfCancellationRequested();
            NoLinks(file);
            return Report(ClaudeDesktopReadinessStatus.Ready, file, "当前安装的 Claude Code 引擎已通过文件与原厂签名检查。");
        }
        catch (OperationCanceledException) { throw; }
        catch (UserException)
        { return Report(ClaudeDesktopReadinessStatus.UnsafePath, file, "Claude Code 引擎路径发生变化或包含链接，已停止接入。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Report(ClaudeDesktopReadinessStatus.Unreadable, file, "无法安全读取或校验预置 Claude Code 引擎，请重新检测安装。"); }
    }

    // A fast final launch guard. A non-null path proves only normal-file presence;
    // callers must await Check before presenting the installation as ready.
    public static string? BundledHostPath(ClientInstallation installation, string? architecture = null)
    {
        if (!OperatingSystem.IsWindows() || installation.Kind != ClientKind.ClaudeDesktop) return null;
        try
        {
            var file = HostPath(installation, architecture ?? Architecture(installation));
            var attributes = File.GetAttributes(file);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 ? file : null;
        }
        catch (Exception ex) when (ex is UserException or ArgumentException or NotSupportedException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    // Pure decision only. UpgradeOffline still requires the existing download/hash/
    // signature/install pipeline and a successful post-install Check.
    public static ClaudeDesktopRepairAction Plan(ClaudeDesktopReadinessReport readiness, string installedVersion, PackageRelease release)
    {
        if (readiness.Ready) return ClaudeDesktopRepairAction.Reuse;
        if (readiness.Status != ClaudeDesktopReadinessStatus.MissingHostEngine)
            return ClaudeDesktopRepairAction.Blocked;
        try { DesktopClientDownloads.ValidateRelease(release); }
        catch (UserException) { return ClaudeDesktopRepairAction.Blocked; }
        if (release.Kind != ClientKind.ClaudeDesktop || release.Platform != "windows" ||
            !TryVersion(installedVersion, out var installed) || !TryVersion(release.Version, out var available))
            return ClaudeDesktopRepairAction.Blocked;
        var comparison = installed.CompareTo(available);
        return comparison > 0 ? ClaudeDesktopRepairAction.InstalledVersionNewer :
            comparison == 0 ? ClaudeDesktopRepairAction.SameVersionConflict : ClaudeDesktopRepairAction.UpgradeOffline;
    }

    public static string RepairMessage(ClaudeDesktopRepairAction action) => action switch
    {
        ClaudeDesktopRepairAction.Reuse => "Claude Code 引擎已就绪，复用现有安装。",
        ClaudeDesktopRepairAction.UpgradeOffline => "当前 Claude 缺少引擎，可通过已校验的更高版本完整离线包升级；安装后须重新检查。",
        ClaudeDesktopRepairAction.SameVersionConflict =>
            "当前 Claude 缺少引擎，但与离线包版本相同；Windows 可能拒绝同标识不同内容的包。不会自动卸载或清理数据，请使用更高版本完整离线包。",
        ClaudeDesktopRepairAction.InstalledVersionNewer => "当前 Claude 比离线包更新，已阻止降级；请更新下载清单。",
        _ => "当前安装或离线包尚未通过安全检查，已停止自动修复。请重新检测安装并更新下载清单。"
    };

    private static bool TryVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (!Version.TryParse(value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    private static string Architecture(ClientInstallation installation) => installation.Architecture is "x64" or "arm64"
        ? installation.Architecture : PackageService.Architecture;

    private static string HostPath(ClientInstallation installation, string architecture)
    {
        if (architecture is not ("x64" or "arm64")) throw new UserException("不支持的处理器架构。");
        var root = installation.InstallationRoot;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or "..") ||
            root.IndexOf(':', 2) >= 0)
            throw new UserException("不是普通本机安装目录。");
        root = SafeFiles.DirectoryPath(root);
        NoLinks(root);
        var file = Path.Combine(root, "app", "resources", "preseed", "claude-code", "win32-" + architecture, "claude.exe");
        NoLinks(file);
        return file;
    }

    // File.Exists ignores some dangling links; GetAttributes detects their reparse
    // flag. Walk missing components too, so an ancestor link never becomes "missing".
    private static void NoLinks(string path)
    {
        for (var at = Path.GetFullPath(path); !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            try
            {
                if ((File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                    throw new UserException("路径包含链接。");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    private static ClaudeDesktopReadinessReport Report(ClaudeDesktopReadinessStatus status, string path, string message) =>
        new(status, path, message);
}
