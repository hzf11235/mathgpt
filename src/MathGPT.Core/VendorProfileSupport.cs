using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MathGPT.Core;

// Capability inspection is separate from installer/vendor-signature validation. Both
// are required: a marker is never a substitute for trusting an executable's origin.
public static class VendorProfileSupport
{
    public static void Require(ClientInstallation install)
    {
        if (install.Kind is not ClientKind.ClaudeDesktop and not ClientKind.Harness)
            throw Unsupported();
        if (!Path.IsPathFullyQualified(install.Executable) || !File.Exists(install.Executable)) throw Unsupported();
        SafeFiles.NoLinks(install.Executable);
        var baseDirectory = Path.GetDirectoryName(install.Executable)!;
        var asar = OperatingSystem.IsMacOS()
            ? SafeFiles.Child(Directory.GetParent(baseDirectory)?.FullName ?? throw Unsupported(), "Resources/app.asar")
            : SafeFiles.Child(baseDirectory, "resources/app.asar");
        RequireAsar(install.Kind, asar);
    }

    public static void RequireAsar(ClientKind kind, string file)
    {
        SafeFiles.NoLinks(file);
        try
        {
            var text = ReadEntry(file, kind == ClientKind.ClaudeDesktop
                ? ".vite/build/index.pre.js" : "lib/main.js");
            string[] markers = kind switch
            {
                ClientKind.ClaudeDesktop => ["configLibrary", "deploymentMode", "process.env.LOCALAPPDATA",
                    "user-data-dir", "requestSingleInstanceLock", "setPath(\"userData\"", "-3p"],
                ClientKind.Harness => ["DSH_HOME", "userData", "requestSingleInstanceLock", "profiles", "desktop"],
                _ => throw Unsupported()
            };
            if (markers.Any(marker => !text.Contains(marker, StringComparison.Ordinal))) throw Unsupported();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or OverflowException or DecoderFallbackException)
        { throw Unsupported(); }
    }

    private static string ReadEntry(string file, string path)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> prefix = stackalloc byte[16];
        stream.ReadExactly(prefix);
        var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..8]);
        var jsonBytes = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..16]);
        if (headerBytes > 20_000_000 || jsonBytes is 0 or > 16_000_000 || headerBytes < jsonBytes + 8 ||
            8L + headerBytes > stream.Length) throw Unsupported();
        var header = new byte[jsonBytes]; stream.ReadExactly(header);
        using var document = JsonDocument.Parse(header, new() { MaxDepth = 100 });
        var item = document.RootElement;
        foreach (var part in path.Split('/'))
        {
            if (!item.TryGetProperty("files", out var files) || !files.TryGetProperty(part, out item)) throw Unsupported();
        }
        if (item.TryGetProperty("unpacked", out var unpacked) && unpacked.ValueKind == JsonValueKind.True ||
            item.TryGetProperty("link", out _) || !item.TryGetProperty("size", out var sizeValue) ||
            !sizeValue.TryGetInt64(out var size) || size is <= 0 or > 20_000_000 ||
            !item.TryGetProperty("offset", out var offsetValue) || offsetValue.ValueKind != JsonValueKind.String ||
            !long.TryParse(offsetValue.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            throw Unsupported();
        var position = checked(8L + headerBytes + offset);
        if (position > stream.Length || size > stream.Length - position) throw Unsupported();
        stream.Position = position;
        var bytes = new byte[(int)size]; stream.ReadExactly(bytes);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static UserException Unsupported() => new("当前客户端版本尚未通过独立配置检查。原账号未作修改；请更新安装包后重试。");
}
