using System.IO.Compression;
using System.Text;
using MathGPT.Core;

namespace MathGPT.Mac;

internal static class MacPackageSafety
{
    // Electron framework ZIPs legitimately link ../../Frameworks inside the app.
    // Resolve each link relative to its parent, while keeping the full link graph
    // inside this fresh extraction directory. Never extract an unchecked ZIP.
    internal static void Validate(ZipArchive archive)
    {
        if (archive.Entries.Count > 150_000) throw new UserException("安装包文件数量异常。");
        long expanded = 0;
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > 4_000_000_000) throw new UserException("安装包解压后超过大小限制。");
            var path = Normalize(entry.FullName.TrimEnd('/'));
            if (!paths.Add(path)) throw new UserException("安装包包含重复路径。");
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind is not (0 or 0x8000 or 0x4000 or 0xA000)) throw new UserException("安装包包含不支持的文件类型。");
            if (kind != 0xA000) continue;
            if (entry.Length > 4096) throw new UserException("安装包链接格式异常。");
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
            var target = reader.ReadToEnd();
            var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] + "/" : "";
            links.Add(path, Normalize(parent + ValidateRelative(target)));
        }
        // Prevent chained links from making a lexically internal path escape,
        // including links used as the parent directory of another ZIP entry.
        foreach (var path in paths)
        {
            var resolved = path;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var hops = 0; ; hops++)
            {
                var parts = resolved.Split('/');
                var prefix = "";
                var changed = false;
                for (var i = 0; i < parts.Length; i++)
                {
                    prefix += (i == 0 ? "" : "/") + parts[i];
                    if (!links.TryGetValue(prefix, out var target)) continue;
                    if (hops == 0 && i + 1 < parts.Length) throw new UserException("安装包不能通过链接目录写入文件。");
                    if (hops >= 40 || !seen.Add(prefix)) throw new UserException("安装包包含循环链接。");
                    resolved = Normalize(target + (i + 1 < parts.Length ? "/" + string.Join('/', parts[(i + 1)..]) : ""));
                    changed = true;
                    break;
                }
                if (!changed) break;
            }
        }
    }

    private static string ValidateRelative(string value)
    {
        if (value.Length == 0 || value.StartsWith('/') || value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl))
            throw new UserException("安装包包含不安全的路径或链接。");
        return value;
    }

    private static string Normalize(string value)
    {
        ValidateRelative(value);
        var parts = new List<string>();
        foreach (var part in value.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new UserException("安装包包含外部链接，已停止解压。");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part.Normalize(NormalizationForm.FormC));
        }
        if (parts.Count == 0) throw new UserException("安装包路径不能为空。");
        return string.Join('/', parts);
    }
}
