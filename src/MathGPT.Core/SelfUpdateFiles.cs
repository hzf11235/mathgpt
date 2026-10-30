using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MathGPT.Core;

internal static class SelfUpdateFiles
{
    private static readonly HashSet<string> WindowsFiles = new(StringComparer.OrdinalIgnoreCase)
    { "MathGPT.exe", "MathGPT.Bridge.exe", "MathGPT.Bridge-source.zip", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "使用说明.txt" };
    internal static bool Same(string left, string right) => Path.GetFullPath(left).TrimEnd('\\', '/')
        .Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    internal static bool Within(string child, string parent) => Same(child, parent) ||
        Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string Local(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Any(char.IsControl) || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "." or "..") ||
            OperatingSystem.IsWindows() && path.IndexOf(':', 2) >= 0)
            throw new UserException("自动更新只支持普通本机程序目录。");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length < 4 || Same(full, Path.GetPathRoot(full)!)) throw new UserException("不能替换磁盘根目录。");
        NoLinks(full);
        return full;
    }
    internal static void NoLinks(string path)
    {
        for (var at = Path.GetFullPath(path); !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            try
            {
                if ((File.GetAttributes(at) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new UserException("更新目录包含链接或特殊文件，已停止替换。");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }
    internal static void ValidateOptions(SelfUpdateOptions options)
    {
        if (options.Platform is not ("windows" or "mac") || options.Architecture is not ("x64" or "arm64"))
            throw new UserException("当前平台暂不支持自动更新。");
        _ = SelfUpdateService.VersionOf(options.CurrentVersion);
        var target = Local(options.InstallationRoot);
        var data = Local(options.DataRoot);
        foreach (var protectedPath in options.ProtectedDirectories.Append(data))
        {
            var folder = Local(protectedPath);
            if (Within(folder, target) || Within(target, folder))
                throw new UserException("程序目录与账号数据或工作区重叠，已保留文件并停止自动替换。");
        }
        if (SelfUpdateService.IsManagedInstallation(target)) throw new UserException("FengCodex 管理的安装不能自行替换。");
    }
    internal static string Sibling(string target, string kind, string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$") || kind is not ("stage" or "rollback")) throw new UserException("更新任务标识无效。");
        target = Local(target);
        var result = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + ".mathgpt-" + kind + "-" + id);
        NoLinks(result); return result;
    }
    internal static void ValidateInstalledTree(string root, string platform)
    {
        root = Local(root);
        if (!Directory.Exists(root)) throw new UserException("未找到待更新的程序目录。");
        var files = Tree(root);
        if (platform == "windows")
        {
            if (files.Any(f => !WindowsFiles.Contains(Path.GetRelativePath(root, f))) ||
                Directory.EnumerateDirectories(root).Any() || !File.Exists(Path.Combine(root, "MathGPT.exe")))
                throw new UserException("便携目录含有非程序文件或目录，已保留内容；请将程序与数据分开放置。");
        }
        else if (files.Any(f => !Path.GetRelativePath(root, f).Replace('\\', '/').StartsWith("Contents/", StringComparison.Ordinal)) ||
            !File.Exists(Path.Combine(root, "Contents", "MacOS", "MathGPT")) || !File.Exists(Path.Combine(root, "Contents", "Info.plist")))
            throw new UserException("无法确认 MathGPT 应用包布局，已停止自动替换。");
    }
    internal static List<string> Tree(string root)
    {
        NoLinks(root);
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                NoLinks(item);
                if ((File.GetAttributes(item) & FileAttributes.Directory) != 0) pending.Push(item);
                else files.Add(item);
                if (files.Count > 10_000) throw new UserException("更新文件数量异常。");
            }
        return files;
    }
    internal static string Extract(string zip, string unpack, SelfUpdateRelease release, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        var wrapper = Path.GetFileNameWithoutExtension(release.FileName) + "/";
        var wrapped = release.Platform == "windows" && archive.Entries.Any(e => e.FullName.StartsWith(wrapper, StringComparison.Ordinal));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = entry.FullName;
            var directory = path.EndsWith('/');
            var normal = directory ? path[..^1] : path;
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (path.Length > 400 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) || path.StartsWith('/') ||
                normal.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                    Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) ||
                !paths.Add(normal.Normalize(NormalizationForm.FormC)) || mode is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || archive.Entries.Count > 10_000)
                throw new UserException("更新 ZIP 包含不安全路径、重复文件或链接，已停止解包。");
            total += entry.Length;
            if (total > 1_500_000_000 || entry.Length > 800_000_000) throw new UserException("更新 ZIP 解包大小异常。");
            if (release.Platform == "windows")
            {
                if (wrapped && path == wrapper && directory) continue;
                var relative = wrapped && path.StartsWith(wrapper, StringComparison.Ordinal) ? path[wrapper.Length..] : path;
                if (directory || !WindowsFiles.Contains(relative)) throw new UserException("更新 ZIP 包含非 MathGPT 程序文件。");
            }
            else if (!path.StartsWith("MathGPT.app/Contents/", StringComparison.Ordinal) &&
                path is not ("使用说明.txt" or "运行本机检查.command") &&
                !(directory && path is "MathGPT.app/" or "MathGPT.app/Contents/"))
                throw new UserException("更新 ZIP 包含应用包之外的内容。");
        }
        Directory.CreateDirectory(unpack);
        PackageService.ExtractSafe(zip, unpack, ct);
        var payload = Path.Combine(unpack, release.Platform == "windows" ? wrapped ? wrapper[..^1] : "" : "MathGPT.app");
        ValidateInstalledTree(payload, release.Platform);
        if (release.Platform == "windows" && !File.Exists(Path.Combine(payload, "MathGPT.Bridge.exe")))
            throw new UserException("更新 ZIP 缺少程序组件。");
        if (OperatingSystem.IsMacOS() && release.Platform == "mac")
        {
            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("MathGPT.app/Contents/", StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
            {
                var mode = (entry.ExternalAttributes >> 16) & 0x1ff;
                if (mode is not (0 or 0x180 or 0x1a4 or 0x1ed)) throw new UserException("应用包文件权限异常。");
                File.SetUnixFileMode(Path.Combine(unpack, entry.FullName.Replace('/', Path.DirectorySeparatorChar)),
                    (UnixFileMode)(mode == 0 ? 0x1a4 : mode));
            }
        }
        return payload;
    }
    internal static string Hash(string path)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    internal static void WriteInventory(string root, string output)
    {
        var lines = Tree(root).Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => Hash(item.File) + "\t" + item.Relative + "\n");
        File.WriteAllText(output, string.Concat(lines), new UTF8Encoding(false, true));
    }
    internal static void VerifyInventory(string root, string inventory, string expectedHash)
    {
        if (!Hash(inventory).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new UserException("更新文件清单已变化。");
        var lines = File.ReadAllLines(inventory, new UTF8Encoding(false, true));
        if (lines.Length is 0 or > 10_000) throw new UserException("更新文件清单数量异常。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[a-f0-9]{64}$") || parts[1].Any(char.IsControl) ||
                parts[1].Contains('\\') || parts[1].Contains(':') || parts[1].StartsWith('/') ||
                parts[1].Split('/').Any(p => p.Length == 0 || p is "." or "..") || !seen.Add(parts[1]))
                throw new UserException("更新文件清单路径异常。");
            var file = SafeFiles.Child(root, parts[1].Replace('/', Path.DirectorySeparatorChar));
            if (!Hash(file).Equals(parts[0], StringComparison.OrdinalIgnoreCase)) throw new UserException("更新文件在准备后发生变化，已停止替换。");
        }
        if (Tree(root).Count != lines.Length) throw new UserException("更新目录包含清单之外的文件。");
    }
    internal static bool RemoveOwned(string path, string parent)
    {
        try
        {
            path = Local(path); parent = Local(parent);
            if (Same(path, parent) || !Same(Path.GetDirectoryName(path)!, parent))
                throw new UserException("更新清理路径越界，已保留文件。");
            if (!Directory.Exists(path)) return true;
            _ = Tree(path);
            Directory.Delete(path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserException) { return false; }
    }
    internal static void CopyTree(string source, string target)
    {
        NoLinks(source); NoLinks(target);
        if (Directory.Exists(target)) throw new UserException("更新工作程序目录已存在。");
        Directory.CreateDirectory(target);
        foreach (var file in Tree(source))
        {
            var destination = SafeFiles.Child(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
            if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
        }
    }
}
