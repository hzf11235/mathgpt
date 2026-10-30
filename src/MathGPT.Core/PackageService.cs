using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MathGPT.Core;

public sealed class PackageService(StateStore store, HttpClient? http = null)
{
    private readonly HttpClient client = http ?? HttpSafety.Create(timeout: TimeSpan.FromSeconds(45));
    public static string Architecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
    public async Task<PackageRelease> Resolve(ClientKind kind, CancellationToken ct = default)
    {
        if (DesktopConfiguration.IsDesktop(kind))
            return await DesktopClientDownloads.Resolve(kind, "windows", Architecture, ct, client);
        if (kind == ClientKind.Codex)
        {
            using var response = await client.GetAsync("https://api.github.com/repos/openai/codex/releases/latest", ct);
            var json = await HttpSafety.Json(response, ct);
            var arch = Architecture == "arm64" ? "aarch64" : "x86_64";
            var file = $"codex-{arch}-pc-windows-msvc.exe.zip";
            var asset = json.Obj("assets").Items().FirstOrDefault(a => a.Str("name") == file);
            var digest = asset.Str("digest");
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal)) throw new UserException("Codex 官方发布未提供 SHA256，已停止自动安装。");
            var version = json.Str("tag_name").Replace("rust-v", "");
            return new(kind, version, asset.Str("browser_download_url"),
                $"codex-{version}-{Architecture}.zip", "SHA256", digest[7..], false, Architecture);
        }
        if (kind == ClientKind.Claude)
        {
            const string root = "https://downloads.claude.ai/claude-code-releases";
            var version = (await client.GetStringAsync(root + "/stable", ct)).Trim();
            if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$"))
                throw new UserException("Claude Code 官方安装源没有返回有效版本。请检查网络地区限制。");
            using var response = await client.GetAsync($"{root}/{version}/manifest.json", ct);
            var manifest = await HttpSafety.Json(response, ct);
            var platform = "win32-" + Architecture;
            var hash = manifest.Obj("platforms").Obj(platform).Str("checksum");
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) throw new UserException("官方未提供此架构的有效安装包校验值。");
            return new(kind, version, $"{root}/{version}/{platform}/claude.exe",
                $"claude-{version}-{platform}.exe", "SHA256", hash, false, Architecture);
        }
        if (Architecture != "x64") throw new UserException("DeepSeek 官方桌面版目前仅发布 Windows x64 包。此处不会把 x64 包当作 ARM64 原生版安装。");
        var feed = await client.GetStringAsync("https://download.deepseek.com/dsh-desk/feeds/win-x64/nightly.yml", ct);
        static string Field(string yaml, string key)
        {
            var match = Regex.Match(yaml, @"(?m)^" + key + @":\s*(?:>-\s*\r?\n\s+)?([^\r\n]+)");
            return match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : "";
        }
        var url = Field(feed, "path");
        var hash512 = Field(feed, "sha512");
        var ver = Field(feed, "version");
        if (!Regex.IsMatch(ver, @"^\d+\.\d+\.\d+[a-zA-Z0-9.-]*$") || Convert.FromBase64String(hash512).Length != 64)
            throw new UserException("Harness 官方更新清单不完整，已停止安装。");
        UrlPolicy.OfficialDownload(url);
        return new(kind, ver, url, Path.GetFileName(new Uri(url).AbsolutePath), "SHA512", hash512, true, "x64");
    }
    public async Task<ClientInstallation> Install(PackageRelease release, string packageFile, string parent,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (DesktopClientDownloads.IsMirror(release)) DesktopClientDownloads.ValidateRelease(release);
        else UrlPolicy.OfficialDownload(release.Url);
        if (DesktopConfiguration.IsDesktop(release.Kind))
        {
            using var packageLock = new FileStream(packageFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (DesktopClientDownloads.IsMirror(release) && !await BitsDownload.Verify(packageFile, release, ct))
                throw new UserException("安装包 SHA256、原厂签名或应用身份校验失败，已阻止安装。");
            progress?.Report(new("Windows 正在验证并安装官方桌面应用", 100));
            var app = await DesktopPackages.Install(packageFile, release.Kind, ct);
            store.State.Clients.RemoveAll(c => c.Kind == app.Kind); store.State.Clients.Add(app); store.Save(); return app;
        }
        if (!await BitsDownload.Verify(packageFile, release, ct)) throw new UserException("安装包校验失败，禁止执行。");
        SafeFiles.Writable(parent);
        var target = SafeFiles.Child(parent, release.Kind.ToString().ToLowerInvariant() + "-" + release.Version);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            var known = store.State.Clients.FirstOrDefault(c => c.Kind == release.Kind &&
                c.Managed && c.InstallationRoot.Equals(target, StringComparison.OrdinalIgnoreCase) && File.Exists(c.Executable));
            if (known is not null) return known;
            throw new UserException("目标版本目录已有内容。请先检测已有安装，或选择新的安装目录。");
        }
        Directory.CreateDirectory(target);
        progress?.Report(new("安装中；保留现有客户端与配置", 0));
        string executable;
        if (release.Installer)
        {
            // NSIS's /D parameter must be final and unquoted, including paths with spaces.
            if (target.Contains('"') || target.Contains('\r') || target.Contains('\n'))
                throw new UserException("安装路径含无效字符。");
            var start = new ProcessStartInfo(packageFile) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.Arguments = "/S /D=" + target;
            using var process = Process.Start(start) ?? throw new UserException("无法启动官方安装器。");
            // Once NSIS starts, let it finish atomically. Download cancellation remains available before this point.
            await process.WaitForExitAsync(CancellationToken.None);
            if (process.ExitCode != 0) throw new UserException($"官方安装器未完成（代码 {process.ExitCode}）。关闭旧版 Harness 后重试。");
            executable = Directory.EnumerateFiles(target, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => Path.GetFileName(p).Contains("Harness", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(p).Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
                ?? throw new UserException("官方安装器退出后未找到客户端。请保留目录并联系客服。");
        }
        else if (release.Kind == ClientKind.Codex)
        {
            await Task.Run(() => ExtractSafe(packageFile, target, ct), ct);
            executable = Directory.EnumerateFiles(target, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p => Path.GetFileName(p).StartsWith("codex-", StringComparison.Ordinal) &&
                    Path.GetFileName(p).EndsWith("-windows-msvc.exe", StringComparison.Ordinal))
                ?? Directory.EnumerateFiles(target, "codex.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new UserException("官方包内缺少 Codex 可执行文件。");
        }
        else
        {
            executable = SafeFiles.Child(target, "claude.exe");
            File.Copy(packageFile, executable, false);
        }
        var version = release.Kind == ClientKind.Harness ? release.Version : await ReadVersion(executable, ct);
        var install = new ClientInstallation { Kind = release.Kind, Version = version, Managed = true,
            Executable = executable, InstallationRoot = target, WorkDirectory = store.State.WorkDirectory };
        SafeFiles.AtomicWrite(SafeFiles.Child(target, "mathgpt-client.json"), JsonSerializer.Serialize(
            new { product = "MathGPT", kind = release.Kind.ToString(), version, source = release.Url, digest = release.Digest }, JsonData.Options));
        store.State.Clients.RemoveAll(c => c.Kind == release.Kind);
        store.State.Clients.Add(install); store.Save();
        progress?.Report(new("安装完成，尚未配置模型", 100));
        return install;
    }
    public static void ExtractSafe(string zip, string root, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        long total = 0;
        if (archive.Entries.Count > 10000) throw new UserException("压缩包文件数量异常。");
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            total += entry.Length;
            if (total > 1_500_000_000 || entry.Length > 800_000_000 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new UserException("压缩包包含过大文件或链接，已阻止解包。");
            var output = SafeFiles.Child(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(output);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                entry.ExtractToFile(output, false);
            }
        }
    }
    public static async Task<string> ReadVersion(string executable, CancellationToken ct = default)
    {
        SafeFiles.NoLinks(executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new UserException("客户端无法启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var value = (await output).Trim();
            await error;
            if (process.ExitCode != 0 || value.Length == 0) throw new UserException("客户端版本检查失败。请确认选择了官方客户端。");
            return value.Length > 100 ? value[..100] : value;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
    }
    public static string? FindInstalled(ClientKind kind)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] names = kind switch
        {
            ClientKind.Codex => ["codex.exe"], ClientKind.Claude => ["claude.exe"], _ => ["DeepSeek Harness.exe"]
        };
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(new[]
        {
            Path.Combine(home, ".local", "bin"), Path.Combine(local, "Programs", "DeepSeek Harness"),
            Path.Combine(local, "@deepseek-ai", "dsh-desktop"), @"D:\Program Files\dsh"
        });
        foreach (var directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)))
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        if (kind == ClientKind.Harness)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                using var uninstall = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var id in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(id);
                    if (entry?.GetValue("DisplayName") is not string display ||
                        !display.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.GetValue("InstallLocation") is string location)
                    {
                        var candidate = Path.Combine(location, "DeepSeek Harness.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
        }
        return null;
    }
}
