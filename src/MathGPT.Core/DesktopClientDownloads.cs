using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MathGPT.Core;

// Mirror metadata never authorizes an arbitrary proxy URL or changes a desktop
// selection into a CLI. Both the transport and the platform's vendor signature
// remain independent gates before installation.
public static class DesktopClientDownloads
{
    public const string ManifestUrl = "https://mathgpt.xyz/downloads/desktop-clients/manifest.v1.json";
    public const string AssetRoot = "https://mathgpt.xyz/downloads/desktop-clients/assets/";
    public const long MaximumPackageBytes = 8_000_000_000;
    private const int MaximumManifestBytes = 64_000;

    public static bool IsMirror(PackageRelease release) => release.Url.StartsWith(AssetRoot, StringComparison.Ordinal);

    public static PackageRelease Parse(string json, ClientKind kind, string platform, string architecture)
    {
        if (!DesktopConfiguration.IsDesktop(kind) || platform is not ("windows" or "mac") || architecture is not ("x64" or "arm64"))
            throw new UserException("桌面安装目标无效。");
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes) throw InvalidManifest();
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            var root = document.RootElement;
            UniqueProperties(root);
            if (root.GetProperty("schema").GetInt32() != 1) throw InvalidManifest();
            var assets = root.GetProperty("assets");
            if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 16) throw InvalidManifest();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            PackageRelease? selected = null;
            foreach (var asset in assets.EnumerateArray())
            {
                UniqueProperties(asset);
                string Field(string name) => asset.GetProperty(name).GetString() ?? throw InvalidManifest();
                var client = Field("client"); var os = Field("platform"); var arch = Field("architecture");
                var name = Field("file_name"); var url = Field("url"); var official = Field("official_url");
                var version = Field("version"); var digest = Field("sha256"); var packageKind = Field("package_kind");
                var bytes = asset.GetProperty("bytes").GetInt64(); var offline = asset.GetProperty("offline").GetBoolean();
                var assetKind = client switch { "codex" => ClientKind.CodexDesktop, "claude" => ClientKind.ClaudeDesktop, _ => throw InvalidManifest() };
                if (os is not ("windows" or "mac") || arch is not ("x64" or "arm64") ||
                    !seen.Add(client + "/" + os + "/" + arch) ||
                    !Regex.IsMatch(version, @"^[0-9][A-Za-z0-9._+\-]{0,99}$") ||
                    !Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._\-]{0,159}$") ||
                    !Regex.IsMatch(digest, "^[A-Fa-f0-9]{64}$") || digest.All(c => c == '0') ||
                    bytes <= 0 || bytes > MaximumPackageBytes || !offline ||
                    (os == "windows" ? packageKind != "msix" : packageKind is not ("zip" or "dmg")) ||
                    !name.EndsWith("." + packageKind, StringComparison.Ordinal) ||
                    url != AssetRoot + name) throw InvalidManifest();
                ValidateOfficialUrl(assetKind, os, arch, official);
                var release = new PackageRelease(assetKind, version, url, name, "SHA256", digest.ToLowerInvariant(), true, arch)
                { OfficialUrl = official, ExpectedBytes = bytes, PackageKind = packageKind, Offline = offline, Platform = os };
                if (assetKind == kind && os == platform && arch == architecture) selected = release;
            }
            return selected ?? throw new UserException("本站暂未提供此客户端及架构的完整官方包。可稍后重试，或导入官方安装包。");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw InvalidManifest(); }
    }

    public static async Task<PackageRelease> Resolve(ClientKind kind, string platform, string architecture,
        CancellationToken ct = default, HttpClient? http = null)
    {
        using var owned = http is null ? HttpSafety.Create(timeout: TimeSpan.FromSeconds(25)) : null;
        var client = http ?? owned!;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // No redirect is followed, including redirects to the same origin.
            RejectRedirect(response);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new UserException("本站桌面下载清单暂不可用。请稍后重试，或导入官方安装包。");
            if (response.Content.Headers.ContentLength > MaximumManifestBytes) throw InvalidManifest();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var block = new byte[8192]; int count;
            while ((count = await input.ReadAsync(block, ct)) > 0)
            {
                if (buffer.Length + count > MaximumManifestBytes) throw InvalidManifest();
                buffer.Write(block, 0, count);
            }
            string json;
            try { json = new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray()); }
            catch (System.Text.DecoderFallbackException) { throw InvalidManifest(); }
            return Parse(json, kind, platform, architecture);
        }
        catch (HttpRequestException)
        { throw new UserException("无法取得本站桌面下载清单。请稍后重试，或导入官方安装包；不会降低校验要求。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new UserException("取得桌面下载清单超时。请稍后重试，或导入官方安装包。"); }
    }

    public static void ValidateRelease(PackageRelease release)
    {
        if (!DesktopConfiguration.IsDesktop(release.Kind) || release.Platform is not ("windows" or "mac") ||
            release.Architecture is not ("x64" or "arm64") || release.Url != AssetRoot + release.FileName ||
            !Regex.IsMatch(release.FileName, @"^[A-Za-z0-9][A-Za-z0-9._\-]{0,159}$") ||
            release.HashAlgorithm != "SHA256" || !Regex.IsMatch(release.Digest, "^[A-Fa-f0-9]{64}$") ||
            release.Digest.All(c => c == '0') || release.ExpectedBytes <= 0 || release.ExpectedBytes > MaximumPackageBytes ||
            !release.Offline || (release.Platform == "windows" ? release.PackageKind != "msix" : release.PackageKind is not ("zip" or "dmg")) ||
            !release.FileName.EndsWith("." + release.PackageKind, StringComparison.Ordinal))
            throw InvalidManifest();
        ValidateOfficialUrl(release.Kind, release.Platform, release.Architecture, release.OfficialUrl);
    }

    public static void ValidateOfficialUrl(ClientKind kind, string platform, string architecture, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            value != uri.AbsoluteUri || value.Contains('%') || !Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9._/\-]+$") ||
            uri.AbsolutePath.Split('/').Any(x => x is "." or ".."))
            throw InvalidManifest();
        var valid = kind == ClientKind.CodexDesktop
            ? uri.Host == "persistent.oaistatic.com" && uri.AbsolutePath.StartsWith("/codex-app-prod/", StringComparison.Ordinal) &&
              (platform == "windows" ? uri.AbsolutePath.EndsWith(".msix", StringComparison.Ordinal) :
                  uri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith(".dmg", StringComparison.Ordinal))
            : kind == ClientKind.ClaudeDesktop &&
              (uri.Host == "downloads.claude.ai" && uri.AbsolutePath.StartsWith("/releases-offline/", StringComparison.Ordinal) &&
                  (platform == "windows" ? uri.AbsolutePath.Contains("/win32/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith(".msix", StringComparison.Ordinal) :
                      uri.AbsolutePath.Contains("/darwin/", StringComparison.Ordinal) &&
                      (uri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith(".dmg", StringComparison.Ordinal))) ||
               uri.Host == "claude.ai" && uri.AbsolutePath == (platform == "windows"
                   ? "/api/desktop/win32/" + architecture + "/offline/latest/redirect"
                   : "/api/desktop/darwin/" + architecture + "/offline/latest/redirect"));
        if (!valid) throw InvalidManifest();
    }

    public static string MacBundleIdentifier(ClientKind kind) => kind switch
    {
        ClientKind.CodexDesktop => "com.openai.codex",
        ClientKind.ClaudeDesktop => "com.anthropic.claudefordesktop",
        _ => throw new UserException("不是受支持的官方 Mac 桌面应用。")
    };
    public static string MacSigningRequirement(ClientKind kind)
    {
        var team = kind switch
        {
            ClientKind.CodexDesktop => "2DC432GLL2",
            ClientKind.ClaudeDesktop => "Q6L2SF6YDW",
            _ => throw new UserException("不是受支持的官方 Mac 桌面应用。")
        };
        return "anchor apple generic and identifier \"" + MacBundleIdentifier(kind) +
            "\" and certificate leaf[subject.OU] = \"" + team +
            "\" and certificate 1[field.1.2.840.113635.100.6.2.6] exists" +
            " and certificate leaf[field.1.2.840.113635.100.6.1.13] exists";
    }

    public static async Task<bool> VerifyContent(string file, PackageRelease release, CancellationToken ct = default)
    {
        ValidateRelease(release);
        SafeFiles.NoLinks(file);
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != release.ExpectedBytes) return false;
        var actual = await SHA256.HashDataAsync(input, ct);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(release.Digest));
    }

    public static string PartialPath(PackageRelease release, string directory)
    {
        ValidateRelease(release);
        return SafeFiles.Child(directory, release.FileName + ".mathgpt-" + release.Digest.ToLowerInvariant() + ".partial");
    }

    // This performs transport and SHA256 only. Windows additionally validates
    // Authenticode + MSIX identity in BitsDownload and immediately before install;
    // macOS validates its Apple-anchored vendor requirement before copying an app.
    public static async Task<string> FetchContent(PackageRelease release, string directory,
        IProgress<TransferProgress>? progress, CancellationToken ct = default, HttpClient? http = null)
    {
        ValidateRelease(release);
        SafeFiles.Writable(directory);
        var destination = SafeFiles.Child(directory, release.FileName);
        if (File.Exists(destination))
        {
            if (await VerifyContent(destination, release, ct)) return destination;
            throw new UserException("缓存安装包与本站清单的大小或 SHA256 不符，已停止。请删除损坏包后重试，或选择空目录。");
        }
        var partial = PartialPath(release, directory);
        if (File.Exists(partial) && new FileInfo(partial).Length == release.ExpectedBytes)
        {
            if (!await VerifyContent(partial, release, ct))
                throw new UserException("已下载内容 SHA256 不符，已停止。请在下载页删除损坏包后重试，或选择空目录。");
            File.Move(partial, destination, false); return destination;
        }
        // A large offline package has no overall transfer deadline. Each request
        // and read has an independent idle deadline, while cancellation preserves
        // the digest-bound partial file for strict Range continuation.
        using var owned = http is null ? HttpSafety.Create(timeout: Timeout.InfiniteTimeSpan) : null;
        var client = http ?? owned!;
        try
        {
            try { await Download(client, release.Url, partial, release, false, progress, ct); }
            catch (MirrorNetworkException)
            {
                // No integrity, manifest or platform signature rejection can
                // enter this branch. The official response must match this hash.
                progress?.Report(new("本站连接暂不可用，正在尝试同一官方安装包", 0));
                await Download(client, release.OfficialUrl, partial, release, true, progress, ct);
            }
            if (!await VerifyContent(partial, release, ct))
                throw new UserException("安装包 SHA256 或大小不符，已阻止安装；不会切换来源或替换为命令行。");
            File.Move(partial, destination, false);
            return destination;
        }
        catch (MirrorNetworkException)
        { throw new UserException("本站和原厂下载连接暂不可用。请稍后重试，或导入完整的官方安装包。"); }
    }

    private static async Task Download(HttpClient client, string url, string partial, PackageRelease release,
        bool official, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                if (official) ValidateOfficialUrl(release.Kind, release.Platform, release.Architecture, url);
                else if (url != release.Url) throw InvalidManifest();
                SafeFiles.NoLinks(partial);
                var start = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (start < 0 || start >= release.ExpectedBytes)
                    throw new UserException("下载断点长度无效，已保留文件。请删除损坏包后重试，或选择空目录。");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (start > 0) request.Headers.Range = new(start, null);
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
                headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (!official) RejectRedirect(response);
                    if (redirects >= 4 || response.Headers.Location is not { } location)
                        throw new UserException("官方下载重定向异常，已停止。");
                    url = (location.IsAbsoluteUri ? location : new Uri(new Uri(url), location)).AbsoluteUri;
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    throw new MirrorNetworkException();
                if (response.StatusCode != (start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK))
                    throw new UserException("下载源拒绝了此安装包，请稍后重试或导入官方安装包。");
                var range = response.Content.Headers.ContentRange;
                if (start > 0 && (range is null || range.Unit != "bytes" || !range.HasRange || !range.HasLength ||
                    range.From != start || range.To != release.ExpectedBytes - 1 || range.Length != release.ExpectedBytes))
                    throw new UserException("下载源返回的续传范围与清单不符，已保留断点并停止。");
                if (start == 0 && range is not null || response.Content.Headers.ContentEncoding.Any(x => x != "identity"))
                    throw new UserException("下载源返回了不支持的安装包传输格式，已停止。");
                if (response.Content.Headers.ContentLength is { } length && length != release.ExpectedBytes - start)
                    throw new UserException("安装包大小与清单不符，已停止下载；不会切换来源。");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                if (output.Length != start) throw new UserException("下载断点已被其他进程修改，已停止。");
                output.Position = start;
                var buffer = new byte[131072]; long total = start; int count;
                while (true)
                {
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(90));
                    try { count = await input.ReadAsync(buffer, idleTimeout.Token); }
                    catch (IOException) { throw new MirrorNetworkException(); }
                    if (count == 0) break;
                    total += count;
                    if (total > release.ExpectedBytes) throw new UserException("安装包超过清单大小，已停止下载。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress?.Report(new(official ? "下载原厂完整安装包" : "下载本站完整安装包",
                        (double)total / release.ExpectedBytes * 100, total, release.ExpectedBytes));
                }
                if (total != release.ExpectedBytes) throw new MirrorNetworkException();
                return;
            }
        }
        catch (HttpRequestException) { throw new MirrorNetworkException(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MirrorNetworkException(); }
    }

    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw InvalidManifest();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) throw InvalidManifest();
    }
    private static void RejectRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new UserException("本站下载地址发生了未授权重定向，已停止。");
    }
    private static UserException InvalidManifest() => new("桌面下载清单或安装包来源校验失败，已停止自动安装。");
    private sealed class MirrorNetworkException : Exception;
}
