using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MathGPT.Core;

public sealed record SelfUpdateOptions
{
    public required string InstallationRoot { get; init; }
    public required string DataRoot { get; init; }
    public required string CurrentVersion { get; init; }
    public required string Platform { get; init; }
    public required string Architecture { get; init; }
    public IReadOnlyList<string> ProtectedDirectories { get; init; } = [];
}

public enum SelfUpdateStatus { UpToDate, Staged, Managed, Blocked, Failed }
public sealed record SelfUpdateResult(SelfUpdateStatus Status, string Version, string Message);
public sealed record SelfUpdateRelease(string Version, string Platform, string Architecture, string FileName, long Bytes, string Sha256)
{
    public string Url => SelfUpdateService.DownloadRoot + FileName;
}

public sealed partial class SelfUpdateService
{
    public const string FeedUrl = "https://mathgpt.xyz/downloads/mathgpt-updates.v1.json";
    public const string DownloadRoot = "https://mathgpt.xyz/downloads/";
    public const string KeyId = "mathgpt-update-20261029";
    public const string PublicKeyBase64 = "sGPYRQljyVrnHjI94UdtPra31C/sdsHoMxyWA/Jf3zQ=";
    private const int MaximumFeedBytes = 131_072;
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly SelfUpdateOptions options;
    private readonly HttpClient http;
    private readonly byte[] publicKey;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string UpdateRoot => Path.Combine(Path.GetFullPath(options.DataRoot), "self-update");
    private string PendingFile => Path.Combine(UpdateRoot, "pending.json");
    public bool HasPending => File.Exists(PendingFile);

    public SelfUpdateService(SelfUpdateOptions options, HttpClient? testHttp = null, byte[]? testPublicKey = null)
    {
        this.options = options;
        http = testHttp ?? HttpSafety.Create(timeout: TimeSpan.FromMinutes(15));
        publicKey = testPublicKey?.ToArray() ?? Convert.FromBase64String(PublicKeyBase64);
        if (publicKey.Length != 32) throw new ArgumentException("Ed25519 requires a 32-byte public key.");
    }

    public async Task<SelfUpdateResult> CheckAndStage(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        var callerCancellation = ct;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        ct = deadline.Token;
        string? newJob = null;
        string? newStage = null;
        try
        {
            if (IsManagedInstallation(options.InstallationRoot))
                return new(SelfUpdateStatus.Managed, options.CurrentVersion, "此安装由 FengCodex 管理，更新继续通过本机发布器完成。");
            SelfUpdateFiles.ValidateOptions(options);
            SelfUpdateFiles.ValidateInstalledTree(options.InstallationRoot, options.Platform);
            Directory.CreateDirectory(UpdateRoot);
            SelfUpdateFiles.NoLinks(UpdateRoot);
            using var updateLock = new FileStream(Path.Combine(UpdateRoot, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CleanupCompleted();
            var envelope = await GetBytes(FeedUrl, MaximumFeedBytes, null, ct);
            var release = SelectRelease(envelope, options.Platform, options.Architecture, options.CurrentVersion, publicKey);
            if (release is null) return new(SelfUpdateStatus.UpToDate, options.CurrentVersion, "当前已是可用的最新版本。");
            if (File.Exists(PendingFile))
            {
                try
                {
                    var pending = ReadPlan(PendingFile);
                    ValidatePlan(pending, options.InstallationRoot, publicKey);
                    BindOptions(pending);
                    if (VersionOf(pending.Version) >= VersionOf(release.Version))
                    {
                        SelfUpdateFiles.VerifyInventory(pending.StageRoot, pending.InventoryFile, pending.InventorySha256);
                        return new(SelfUpdateStatus.Staged, pending.Version, "更新已准备，正常退出后应用；不会自动重启。");
                    }
                }
                catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException or
                    JsonException or CryptographicException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException) { }
                // Preserve stale/tampered jobs; they cannot block staging a fresh signed release.
            }
            var id = Guid.NewGuid().ToString("N");
            newJob = Path.Combine(UpdateRoot, id);
            newStage = SelfUpdateFiles.Sibling(options.InstallationRoot, "stage", id);
            var backup = SelfUpdateFiles.Sibling(options.InstallationRoot, "rollback", id);
            if (Directory.Exists(newJob) || Directory.Exists(newStage) || Directory.Exists(backup))
                throw new UserException("更新暂存目录已存在，已保留现有文件。");
            Directory.CreateDirectory(newJob);
            var zip = Path.Combine(newJob, "package.zip");
            await Download(release, zip, ct);
            var unpack = Path.Combine(newJob, "unpack");
            var payload = SelfUpdateFiles.Extract(zip, unpack, release, ct);
            SelfUpdateFiles.NoLinks(Path.GetDirectoryName(newStage)!);
            Directory.Move(payload, newStage);
            var inventory = Path.Combine(newJob, "files.tsv");
            var before = Path.Combine(newJob, "before.tsv");
            SelfUpdateFiles.WriteInventory(newStage, inventory);
            SelfUpdateFiles.WriteInventory(options.InstallationRoot, before);
            var plan = new SelfUpdateApplyPlan
            {
                Id = id, TargetRoot = Path.GetFullPath(options.InstallationRoot), StageRoot = newStage,
                BackupRoot = backup, DataRoot = Path.GetFullPath(options.DataRoot), Version = release.Version,
                CurrentVersion = options.CurrentVersion, Platform = options.Platform, Architecture = options.Architecture,
                ZipFile = zip, ZipSha256 = release.Sha256, InventoryFile = inventory,
                InventorySha256 = SelfUpdateFiles.Hash(inventory), BeforeInventoryFile = before,
                BeforeInventorySha256 = SelfUpdateFiles.Hash(before),
                ProtectedDirectories = options.ProtectedDirectories.ToArray(),
                FeedEnvelope = Convert.ToBase64String(envelope)
            };
            var planJson = JsonSerializer.Serialize(plan, JsonOptions);
            SafeFiles.AtomicWrite(Path.Combine(newJob, "plan.json"), planJson);
            SafeFiles.AtomicWrite(PendingFile, planJson);
            newJob = null; newStage = null;
            return new(SelfUpdateStatus.Staged, release.Version, "新版本已校验并准备，正常退出后应用；账号、密钥和工作区保留，不会自动重启。");
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return new(SelfUpdateStatus.Failed, options.CurrentVersion, "更新下载超时，现有程序继续可用；下次启动可重试。"); }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException or
            HttpRequestException or JsonException or CryptographicException or FormatException or ArgumentException or
            InvalidOperationException or KeyNotFoundException or DecoderFallbackException)
        {
            return new(ex is UserException ? SelfUpdateStatus.Blocked : SelfUpdateStatus.Failed, options.CurrentVersion,
                ex is UserException ? Privacy.Clean(ex.Message) : "暂时无法准备更新，现有程序继续可用；下次启动可重试。");
        }
        finally
        {
            // Only fresh directories owned by this invocation are eligible here.
            if (newStage is not null) SelfUpdateFiles.RemoveOwned(newStage, Path.GetDirectoryName(Path.GetFullPath(options.InstallationRoot))!);
            if (newJob is not null) SelfUpdateFiles.RemoveOwned(newJob, UpdateRoot);
            gate.Release();
        }
    }

    public ProcessStartInfo? BuildApplyStartInfo(int parentPid)
    {
        if (parentPid != Environment.ProcessId) throw new UserException("更新只能由当前正常退出的进程准备。");
        if (!HasPending || parentPid <= 0 || IsManagedInstallation(options.InstallationRoot)) return null;
        SelfUpdateFiles.ValidateOptions(options);
        var plan = ReadPlan(PendingFile);
        ValidatePlan(plan, options.InstallationRoot, publicKey);
        BindOptions(plan);
        var job = Path.GetDirectoryName(plan.ZipFile)!;
        var planFile = Path.Combine(job, "plan.json");
        var digest = SelfUpdateFiles.Hash(PendingFile);
        if (!SelfUpdateFiles.Hash(planFile).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new UserException("待处理更新与任务文件不一致。");
        SelfUpdateFiles.ValidateInstalledTree(plan.TargetRoot, plan.Platform);
        SelfUpdateFiles.VerifyInventory(plan.TargetRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256);
        SelfUpdateFiles.VerifyInventory(plan.StageRoot, plan.InventoryFile, plan.InventorySha256);
        if (!SelfUpdateFiles.Hash(plan.ZipFile).Equals(plan.ZipSha256, StringComparison.OrdinalIgnoreCase))
            throw new UserException("更新包在准备后发生变化，已停止替换。");
        var worker = Path.Combine(job, "worker");
        var executable = Environment.ProcessPath ?? throw new UserException("无法确定更新程序入口。");
        string workerExecutable;
        if (plan.Platform == "windows")
        {
            if (!Path.GetFileName(executable).Equals("MathGPT.exe", StringComparison.OrdinalIgnoreCase))
                throw new UserException("自动更新只适用于正式便携版入口。");
            if (!SelfUpdateFiles.Same(executable, Path.Combine(plan.TargetRoot, "MathGPT.exe")))
                throw new UserException("更新程序不属于当前安装目录。");
            Directory.CreateDirectory(worker);
            workerExecutable = Path.Combine(worker, "MathGPT.exe");
            SelfUpdateFiles.NoLinks(workerExecutable);
            File.Copy(executable, workerExecutable, true);
        }
        else
        {
            var ownExecutable = Path.Combine(plan.TargetRoot, "Contents", "MacOS", "MathGPT");
            if (!SelfUpdateFiles.Same(executable, ownExecutable))
                throw new UserException("请先将 MathGPT.app 放入应用程序目录，并从该位置打开，再准备自动更新。");
            workerExecutable = Path.Combine(worker, "MathGPT.app", "Contents", "MacOS", "MathGPT");
            if (Directory.Exists(worker)) SelfUpdateFiles.RemoveOwned(worker, job);
            SelfUpdateFiles.CopyTree(plan.TargetRoot, Path.Combine(worker, "MathGPT.app"));
        }
        PrepareExitIntent(planFile, digest, executable);
        var start = new ProcessStartInfo(workerExecutable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = job };
        foreach (var argument in new[] { "--self-update-apply", planFile, "--target", plan.TargetRoot,
            "--parent", parentPid.ToString(CultureInfo.InvariantCulture), "--plan-sha", digest })
            start.ArgumentList.Add(argument);
        return start;
    }

    private void BindOptions(SelfUpdateApplyPlan plan)
    {
        if (!SelfUpdateFiles.Same(plan.DataRoot, options.DataRoot) || plan.CurrentVersion != options.CurrentVersion ||
            plan.Platform != options.Platform || plan.Architecture != options.Architecture ||
            plan.ProtectedDirectories.Length != options.ProtectedDirectories.Count ||
            !plan.ProtectedDirectories.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                options.ProtectedDirectories.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new UserException("待处理更新与当前账号目录、版本或平台不匹配。");
    }

    public static bool IsManagedInstallation(string root)
    {
        try { return Path.GetFullPath(root).TrimEnd('\\', '/').Equals(@"D:\Codex\Apps\mathgpt-desktop\current", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    public static SelfUpdateRelease? SelectRelease(byte[] envelope, string platform, string architecture, string currentVersion,
        byte[]? verificationKey = null, DateTimeOffset? now = null)
    {
        if (envelope.Length > MaximumFeedBytes) throw new UserException("更新清单过大，已停止检查。");
        using var wrapper = JsonDocument.Parse(envelope);
        Fields(wrapper.RootElement, "schema", "key_id", "payload", "signature");
        var root = wrapper.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("key_id").GetString() != KeyId)
            throw new UserException("更新清单密钥或格式不匹配。");
        var payload = Convert.FromBase64String(root.GetProperty("payload").GetString()!);
        var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()!);
        if (payload.Length > 65_536 || signature.Length != 64) throw new UserException("更新清单签名无效。");
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(verificationKey ?? Convert.FromBase64String(PublicKeyBase64), 0));
        signer.BlockUpdate(payload, 0, payload.Length);
        if (!signer.VerifySignature(signature)) throw new UserException("更新清单未通过 MathGPT 发布签名校验，已停止下载。");
        _ = new UTF8Encoding(false, true).GetString(payload);
        using var document = JsonDocument.Parse(payload);
        var feed = document.RootElement;
        Fields(feed, "schema", "app", "generated_at_utc", "expires_at_utc", "releases");
        var clock = now ?? DateTimeOffset.UtcNow;
        if (feed.GetProperty("schema").GetInt32() != 1 || feed.GetProperty("app").GetString() != "mathgpt-desktop" ||
            !DateTimeOffset.TryParse(feed.GetProperty("generated_at_utc").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var generated) ||
            !DateTimeOffset.TryParse(feed.GetProperty("expires_at_utc").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) ||
            generated.Offset != TimeSpan.Zero || expires.Offset != TimeSpan.Zero || generated > clock.AddMinutes(10) ||
            expires <= clock || expires <= generated || expires - generated > TimeSpan.FromDays(91))
            throw new UserException("更新清单已过期或时间无效，请稍后重新检查。");
        if (platform is not ("windows" or "mac") || architecture is not ("x64" or "arm64"))
            throw new UserException("不支持的更新平台。");
        var current = VersionOf(currentVersion);
        var entries = feed.GetProperty("releases");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 32)
            throw new UserException("更新清单数量异常。");
        var releases = new List<SelfUpdateRelease>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries.EnumerateArray())
        {
            Fields(item, "version", "platform", "architecture", "file_name", "bytes", "sha256");
            var release = new SelfUpdateRelease(item.GetProperty("version").GetString()!, item.GetProperty("platform").GetString()!,
                item.GetProperty("architecture").GetString()!, item.GetProperty("file_name").GetString()!,
                item.GetProperty("bytes").GetInt64(), item.GetProperty("sha256").GetString()!);
            ValidateRelease(release);
            if (!unique.Add(release.Platform + "/" + release.Architecture + "/" + release.Version))
                throw new UserException("更新清单包含重复版本。");
            if (release.Platform == platform && release.Architecture == architecture && VersionOf(release.Version) > current)
                releases.Add(release);
        }
        return releases.OrderByDescending(r => VersionOf(r.Version)).FirstOrDefault();
    }

    internal static void ValidateRelease(SelfUpdateRelease release)
    {
        _ = VersionOf(release.Version);
        if (release.Platform is not ("windows" or "mac") || release.Architecture is not ("x64" or "arm64") ||
            release.Bytes is <= 0 or > 536_870_912 || !Regex.IsMatch(release.Sha256, "^[A-Fa-f0-9]{64}$") || release.Sha256.All(c => c == '0'))
            throw new UserException("更新包信息无效。");
        var expected = release.Platform == "windows" ? $"MathGPT-{release.Version}-Portable-win-{release.Architecture}.zip" :
            $"MathGPT-{release.Version}-mac-{release.Architecture}";
        if (release.Platform == "windows" ? release.FileName != expected :
            release.FileName != expected + ".zip" && release.FileName != expected + "-preview.zip")
            throw new UserException("更新包地址或文件名不属于本平台发行包。");
    }

    internal static Version VersionOf(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$") ||
            !Version.TryParse(value, out var version)) throw new UserException("无法安全比较更新版本。");
        return version;
    }

    private static void Fields(JsonElement item, params string[] expected)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new UserException("更新清单格式不正确。");
        var fields = item.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != expected.Length || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Except(expected, StringComparer.Ordinal).Any()) throw new UserException("更新清单包含未知或重复字段。");
    }

    private async Task<byte[]> GetBytes(string url, int maximum, long? exact, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
            response.RequestMessage?.RequestUri is { } actual && actual.AbsoluteUri != url)
            throw new UserException("更新源返回了重定向或异常响应，已停止下载。");
        if (response.Content.Headers.ContentLength is { } size && (size > maximum || exact is { } length && size != length))
            throw new UserException("更新下载大小不匹配。");
        using var memory = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[65_536];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (memory.Length + read > maximum) throw new UserException("更新下载超过允许大小。");
            memory.Write(buffer, 0, read);
        }
        if (exact is { } expected && memory.Length != expected) throw new UserException("更新下载不完整。");
        return memory.ToArray();
    }

    private async Task Download(SelfUpdateRelease release, string file, CancellationToken ct)
    {
        using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
            response.RequestMessage?.RequestUri is { } actual && actual.AbsoluteUri != release.Url ||
            response.Content.Headers.ContentLength is { } size && size != release.Bytes)
            throw new UserException("更新包返回了重定向或大小异常，已停止下载。");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0; var buffer = new byte[65_536]; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > release.Bytes) throw new UserException("更新包超过清单大小。");
            hash.AppendData(buffer, 0, count);
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != release.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UserException("更新包 SHA256 或大小不匹配，已停止替换。");
    }

    private void CleanupCompleted()
    {
        try { CleanupCompletedVerified(); }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException or
            JsonException or CryptographicException or FormatException or ArgumentException or KeyNotFoundException or InvalidOperationException) { }
        // An old receipt is never allowed to prevent fetching the next signed feed.
    }

    private void CleanupCompletedVerified()
    {
        var receipt = Path.Combine(UpdateRoot, "last-applied.json");
        if (!File.Exists(receipt)) return;
        var plan = ReadPlan(receipt);
        if (plan.Version != options.CurrentVersion || !SelfUpdateFiles.Same(plan.TargetRoot, options.InstallationRoot)) return;
        // Expiry limits a new install, not authentication of a past successful install.
        using var wrapper = JsonDocument.Parse(Convert.FromBase64String(plan.FeedEnvelope));
        using var payload = JsonDocument.Parse(Convert.FromBase64String(wrapper.RootElement.GetProperty("payload").GetString()!));
        var signedAt = DateTimeOffset.Parse(payload.RootElement.GetProperty("generated_at_utc").GetString()!, CultureInfo.InvariantCulture);
        ValidatePlan(plan, options.InstallationRoot, publicKey, allowInstalledVersion: true, verificationTime: signedAt.AddSeconds(1));
        SelfUpdateFiles.VerifyInventory(plan.TargetRoot, plan.InventoryFile, plan.InventorySha256);
        // The first Mac install has no trusted old inventory. Keep its rollback,
        // including any user-added bundle contents, instead of deleting unique files.
        if (plan.Platform == "mac") return;
        if (Directory.Exists(plan.BackupRoot))
        {
            SelfUpdateFiles.ValidateInstalledTree(plan.BackupRoot, plan.Platform);
            SelfUpdateFiles.VerifyInventory(plan.BackupRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256);
        }
        if (!SelfUpdateFiles.RemoveOwned(plan.BackupRoot, Path.GetDirectoryName(plan.TargetRoot)!)) return;
        if (!SelfUpdateFiles.RemoveOwned(Path.GetDirectoryName(plan.ZipFile)!, UpdateRoot)) return;
        File.Delete(receipt);
    }
}
