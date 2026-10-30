using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MathGPT.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MathGPT.Tests;

internal static class SelfUpdateAudit
{
    private static readonly Ed25519PrivateKeyParameters FixtureKey = new(Enumerable.Range(1, 32).Select(n => (byte)n).ToArray(), 0);
    private static readonly byte[] FixturePublicKey = FixtureKey.GeneratePublicKey().GetEncoded();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static async Task Run(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Require(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        var zip = Package("windows");
        var release = Release(zip);
        var signed = Feed([release]);
        Require(SelfUpdateService.SelectRelease(signed, "windows", "x64", "1.7.0", FixturePublicKey)?.Version == "1.7.1",
            "Ed25519 signed newer exact-platform release selected");
        Require(SelfUpdateService.SelectRelease(signed, "windows", "arm64", "1.7.0", FixturePublicKey) is null,
            "Another architecture cannot update this client");
        Require(SelfUpdateService.SelectRelease(signed, "mac", "x64", "1.7.0", FixturePublicKey) is null,
            "Another OS cannot update this client");
        Require(SelfUpdateService.SelectRelease(signed, "windows", "x64", "1.7.1", FixturePublicKey) is null &&
            SelfUpdateService.SelectRelease(signed, "windows", "x64", "2.0.0", FixturePublicKey) is null,
            "Same or older version never triggers downgrade");
        var wrapper = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(signed)!;
        var badSignature = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, key_id = SelfUpdateService.KeyId, payload = wrapper["payload"].GetString(),
            signature = Convert.ToBase64String(new byte[64])
        });
        Reject(() => SelfUpdateService.SelectRelease(badSignature, "windows", "x64", "1.7.0", FixturePublicKey));
        Require(true, "Invalid release signature rejected");
        Reject(() => SelfUpdateService.SelectRelease(Feed([release], expired: true), "windows", "x64", "1.7.0", FixturePublicKey));
        Require(true, "Expired signed feed rejected");
        var arbitrary = new Dictionary<string, object>(release) { ["file_name"] = "../other.zip" };
        Reject(() => SelfUpdateService.SelectRelease(Feed([arbitrary]), "windows", "x64", "1.7.0", FixturePublicKey));
        Require(true, "Signed arbitrary filename cannot authorize another URL");
        Reject(() => SelfUpdateService.SelectRelease(Feed([release, release]), "windows", "x64", "1.7.0", FixturePublicKey));
        Require(true, "Duplicate release rejected");

        var happy = Fixture(root, "happy", "windows");
        using var happyHttp = new HttpClient(new Handler(signed, zip));
        var service = new SelfUpdateService(happy, happyHttp, FixturePublicKey);
        var result = await service.CheckAndStage();
        Require(result.Status == SelfUpdateStatus.Staged && service.HasPending && ReadApp(happy) == "old",
            "Real ZIP stages while original portable application remains unchanged");
        var planFile = PlanFile(happy);
        var plan = ReadPlan(planFile);
        var firstApply = SelfUpdateService.ApplyPending(planFile, happy.InstallationRoot, Hash(planFile), FixturePublicKey);
        Require(firstApply.Applied && !firstApply.RolledBack && ReadApp(happy) == "new",
            "Isolated portable directory upgrade performs real filesystem replacement");
        Require(File.ReadAllText(Path.Combine(happy.DataRoot, "account-fixture.txt")) == "keep-account" &&
            File.ReadAllText(Path.Combine(happy.ProtectedDirectories[0], "draft.txt")) == "keep-workspace",
            "Account data and workspace bytes survive actual replacement");
        Require(Directory.Exists(plan.BackupRoot) && !File.Exists(Path.Combine(happy.DataRoot, "self-update", "pending.json")),
            "Successful update retains one rollback point and clears only matching pending task");
        var upgraded = happy with { CurrentVersion = "1.7.1" };
        var cleanup = await new SelfUpdateService(upgraded, happyHttp, FixturePublicKey).CheckAndStage();
        Require(cleanup.Status == SelfUpdateStatus.UpToDate && !Directory.Exists(plan.BackupRoot),
            "Next verified version removes its completed rollback copy");

        var expiredReceipt = Fixture(root, "expired-completed-feed", "windows");
        using var expiredReceiptHttp = new HttpClient(new Handler(signed, zip));
        Require((await new SelfUpdateService(expiredReceipt, expiredReceiptHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Staged,
            "Expired completed-feed fixture stages a real ZIP");
        planFile = PlanFile(expiredReceipt);
        Require(SelfUpdateService.ApplyPending(planFile, expiredReceipt.InstallationRoot, Hash(planFile), FixturePublicKey).Applied,
            "Expired completed-feed fixture applies before its historical feed expires");
        var receiptFile = Path.Combine(expiredReceipt.DataRoot, "self-update", "last-applied.json");
        var completedPlan = ReadPlan(receiptFile);
        completedPlan.FeedEnvelope = Convert.ToBase64String(Feed([release], expired: true));
        File.WriteAllText(receiptFile, JsonSerializer.Serialize(completedPlan, Json), new UTF8Encoding(false));
        var freshAfterExpiry = new Handler(signed, zip);
        using var freshAfterExpiryHttp = new HttpClient(freshAfterExpiry);
        var expiryCheck = await new SelfUpdateService(expiredReceipt with { CurrentVersion = "1.7.1" },
            freshAfterExpiryHttp, FixturePublicKey).CheckAndStage();
        Require(expiryCheck.Status == SelfUpdateStatus.UpToDate && freshAfterExpiry.Requests == 1 && ReadApp(expiredReceipt) == "new",
            "Expired signed completed feed cannot prevent a fresh feed fetch and check");

        foreach (var change in new[] { "changed-program", "unknown-file" })
        {
            var alteredBackup = Fixture(root, "backup-" + change, "windows");
            using var backupHttp = new HttpClient(new Handler(signed, zip));
            Require((await new SelfUpdateService(alteredBackup, backupHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Staged,
                "Backup preservation fixture stages: " + change);
            planFile = PlanFile(alteredBackup);
            var backupPlan = ReadPlan(planFile);
            Require(SelfUpdateService.ApplyPending(planFile, alteredBackup.InstallationRoot, Hash(planFile), FixturePublicKey).Applied,
                "Backup preservation fixture performs a real replacement: " + change);
            var preservedFile = Path.Combine(backupPlan.BackupRoot, change == "changed-program" ? "MathGPT.exe" : "personal-draft.txt");
            File.WriteAllText(preservedFile, "keep-altered-backup", new UTF8Encoding(false));
            var backupRequests = new Handler(signed, zip);
            using var backupCheckHttp = new HttpClient(backupRequests);
            var backupCheck = await new SelfUpdateService(alteredBackup with { CurrentVersion = "1.7.1" },
                backupCheckHttp, FixturePublicKey).CheckAndStage();
            Require(backupCheck.Status == SelfUpdateStatus.UpToDate && backupRequests.Requests == 1 &&
                Directory.Exists(backupPlan.BackupRoot) && File.ReadAllText(preservedFile) == "keep-altered-backup" &&
                Directory.Exists(Path.GetDirectoryName(backupPlan.ZipFile)!) && ReadApp(alteredBackup) == "new",
                "Completed cleanup preserves a rollback tree with " + change + " and still checks the fresh feed");
        }

        var pendingExpiry = Fixture(root, "expired-pending-feed", "windows");
        using var pendingExpiryHttp = new HttpClient(new Handler(signed, zip));
        var pendingExpiryService = new SelfUpdateService(pendingExpiry, pendingExpiryHttp, FixturePublicKey);
        Require((await pendingExpiryService.CheckAndStage()).Status == SelfUpdateStatus.Staged,
            "Expired pending-feed fixture stages a real ZIP");
        planFile = PlanFile(pendingExpiry);
        var expiredPendingPlan = ReadPlan(planFile);
        expiredPendingPlan.FeedEnvelope = Convert.ToBase64String(Feed([release], expired: true));
        var expiredPendingJson = JsonSerializer.Serialize(expiredPendingPlan, Json);
        File.WriteAllText(planFile, expiredPendingJson, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(pendingExpiry.DataRoot, "self-update", "pending.json"), expiredPendingJson, new UTF8Encoding(false));
        var pendingExpiryResult = await pendingExpiryService.CheckAndStage();
        var refreshedPending = ReadPlan(PlanFile(pendingExpiry));
        Require(pendingExpiryResult.Status == SelfUpdateStatus.Staged && refreshedPending.Id != expiredPendingPlan.Id &&
            Directory.Exists(expiredPendingPlan.StageRoot) && File.Exists(expiredPendingPlan.ZipFile) &&
            File.ReadAllText(Path.Combine(expiredPendingPlan.StageRoot, "MathGPT.exe")) == "new" && ReadApp(pendingExpiry) == "old",
            "Expired pending feed is preserved while a fresh signed pending update is staged");

        var applyGuards = Fixture(root, "apply-parent-guard", "windows");
        using var applyGuardsHttp = new HttpClient(new Handler(signed, zip));
        var applyGuardsService = new SelfUpdateService(applyGuards, applyGuardsHttp, FixturePublicKey);
        Require((await applyGuardsService.CheckAndStage()).Status == SelfUpdateStatus.Staged,
            "Apply parent guard fixture stages a real ZIP");
        var guardPlan = ReadPlan(PlanFile(applyGuards));
        var otherPid = Environment.ProcessId == 1 ? 2 : 1;
        Require(PolicyRejected(() => applyGuardsService.BuildApplyStartInfo(otherPid), "更新只能由当前正常退出的进程准备。") &&
            !Directory.Exists(Path.Combine(Path.GetDirectoryName(guardPlan.ZipFile)!, "worker")) && ReadApp(applyGuards) == "old",
            "Apply launcher rejects every parent PID other than this process before copying a worker");

        foreach (var mismatch in new[] { "data-root", "current-version", "platform", "architecture", "protected-paths" })
        {
            var originalOptions = Fixture(root, "pending-options-" + mismatch, "windows");
            using var originalHttp = new HttpClient(new Handler(signed, zip));
            Require((await new SelfUpdateService(originalOptions, originalHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Staged,
                "Pending identity fixture stages: " + mismatch);
            var originalPlan = ReadPlan(PlanFile(originalOptions));
            var activeOptions = mismatch switch
            {
                "data-root" => originalOptions with { DataRoot = Path.Combine(Path.GetDirectoryName(originalOptions.DataRoot)!, "other-data") },
                "current-version" => originalOptions with { CurrentVersion = "1.6.9" },
                "platform" => originalOptions with { Platform = "mac" },
                "architecture" => originalOptions with { Architecture = "arm64" },
                _ => originalOptions with { ProtectedDirectories = [] }
            };
            if (mismatch == "data-root")
            {
                var activeUpdateRoot = Path.Combine(activeOptions.DataRoot, "self-update");
                Directory.CreateDirectory(activeUpdateRoot);
                File.Copy(Path.Combine(originalOptions.DataRoot, "self-update", "pending.json"), Path.Combine(activeUpdateRoot, "pending.json"));
            }
            var activeZip = mismatch == "architecture" ? Package("windows", architecture: "arm64") : zip;
            var activeRelease = Release(activeZip, architecture: activeOptions.Architecture);
            using var activeHttp = new HttpClient(new Handler(Feed([activeRelease]), activeZip));
            var activeService = new SelfUpdateService(activeOptions, activeHttp, FixturePublicKey);
            Require(PolicyRejected(() => activeService.BuildApplyStartInfo(Environment.ProcessId),
                "待处理更新与当前账号目录、版本或平台不匹配。") &&
                !Directory.Exists(Path.Combine(Path.GetDirectoryName(originalPlan.ZipFile)!, "worker")) &&
                File.ReadAllText(Path.Combine(originalPlan.StageRoot, "MathGPT.exe")) == "new",
                "Apply launcher binds pending " + mismatch + " to current options before copying a worker");
            // A Windows-shaped tree cannot run a Mac check; the launcher assertion above still tests that binding.
            if (mismatch == "platform") continue;
            var reboundResult = await activeService.CheckAndStage();
            var rebound = ReadPlan(PlanFile(activeOptions));
            Require(reboundResult.Status == SelfUpdateStatus.Staged && rebound.Id != originalPlan.Id &&
                Path.GetFullPath(rebound.DataRoot) == Path.GetFullPath(activeOptions.DataRoot) &&
                rebound.CurrentVersion == activeOptions.CurrentVersion && rebound.Platform == activeOptions.Platform &&
                rebound.Architecture == activeOptions.Architecture &&
                rebound.ProtectedDirectories.SequenceEqual(activeOptions.ProtectedDirectories) &&
                Directory.Exists(originalPlan.StageRoot) && File.Exists(originalPlan.ZipFile) && ReadApp(originalOptions) == "old",
                "Fresh check replaces mismatched pending " + mismatch + " while preserving the earlier stage");
        }

        var rollback = Fixture(root, "rollback", "windows");
        using var rollbackHttp = new HttpClient(new Handler(signed, zip));
        var rollbackService = new SelfUpdateService(rollback, rollbackHttp, FixturePublicKey);
        Require((await rollbackService.CheckAndStage()).Status == SelfUpdateStatus.Staged, "Rollback fixture stages a real ZIP");
        planFile = PlanFile(rollback);
        var failed = SelfUpdateService.ApplyPending(planFile, rollback.InstallationRoot, Hash(planFile), FixturePublicKey,
            () => throw new IOException("isolated fault after actual old-directory rename"));
        Require(!failed.Applied && failed.RolledBack && ReadApp(rollback) == "old" && rollbackService.HasPending,
            "Failure after real backup move restores original program and retains pending update");
        Require(SelfUpdateService.ApplyPending(planFile, rollback.InstallationRoot, Hash(planFile), FixturePublicKey).Applied &&
            ReadApp(rollback) == "new", "Pending update safely retries after filesystem fault");

        var tamper = Fixture(root, "tamper", "windows");
        using var tamperHttp = new HttpClient(new Handler(signed, zip));
        await new SelfUpdateService(tamper, tamperHttp, FixturePublicKey).CheckAndStage();
        planFile = PlanFile(tamper); plan = ReadPlan(planFile);
        File.WriteAllText(Path.Combine(plan.StageRoot, "MathGPT.exe"), "tampered", new UTF8Encoding(false));
        WriteInventory(plan.StageRoot, plan.InventoryFile);
        plan.InventorySha256 = Hash(plan.InventoryFile);
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan, Json), new UTF8Encoding(false));
        var tamperedApply = SelfUpdateService.ApplyPending(planFile, tamper.InstallationRoot, Hash(planFile), FixturePublicKey);
        Require(!tamperedApply.Applied && ReadApp(tamper) == "old", "Edited stage and edited local hashes cannot bypass signed ZIP recheck");
        var foreign = Path.Combine(root, "foreign");
        Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "keep.txt"), "foreign");
        plan.StageRoot = foreign;
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan, Json), new UTF8Encoding(false));
        Require(!SelfUpdateService.ApplyPending(planFile, tamper.InstallationRoot, Hash(planFile), FixturePublicKey).Applied &&
            File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "foreign", "Edited pending task cannot redirect source or overwrite foreign files");
        Require(!SelfUpdateService.ApplyPending(planFile, foreign, Hash(planFile), FixturePublicKey).Applied,
            "Worker target is bound to the parent-provided target");

        var managedRequests = new Handler(signed, zip);
        using var managedHttp = new HttpClient(managedRequests);
        var managed = happy with { InstallationRoot = @"D:\Codex\Apps\mathgpt-desktop\current" };
        Require((await new SelfUpdateService(managed, managedHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Managed &&
            managedRequests.Requests == 0, "FengCodex managed current is skipped before network access");
        var overlap = Fixture(root, "overlap", "windows");
        var overlapRequests = new Handler(signed, zip);
        using var overlapHttp = new HttpClient(overlapRequests);
        Require((await new SelfUpdateService(overlap with { DataRoot = Path.Combine(overlap.InstallationRoot, "data") },
            overlapHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Blocked && overlapRequests.Requests == 0,
            "Data-root overlap stops before any download or replacement");
        File.WriteAllText(Path.Combine(overlap.InstallationRoot, "personal-draft.txt"), "keep");
        Require((await new SelfUpdateService(overlap, overlapHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Blocked &&
            File.ReadAllText(Path.Combine(overlap.InstallationRoot, "personal-draft.txt")) == "keep",
            "Non-program files are preserved and block whole-folder replacement");

        var redirect = Fixture(root, "redirect", "windows");
        using var redirectHttp = new HttpClient(new Handler(signed, zip, redirect: true));
        Require((await new SelfUpdateService(redirect, redirectHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Blocked &&
            ReadApp(redirect) == "old", "Redirect responses never authorize another update host");
        var timedOut = Fixture(root, "http-timeout", "windows");
        var timeoutRequests = new DelayHandler();
        using var timeoutHttp = new HttpClient(timeoutRequests) { Timeout = TimeSpan.FromMilliseconds(100) };
        var timeoutService = new SelfUpdateService(timedOut, timeoutHttp, FixturePublicKey);
        var timeoutResult = await timeoutService.CheckAndStage(CancellationToken.None);
        Require(timeoutResult.Status == SelfUpdateStatus.Failed && timeoutRequests.Requests == 1 && !timeoutService.HasPending &&
            ReadApp(timedOut) == "old", "HttpClient timeout cancellation returns Failed when the caller was not canceled");
        using var callerCanceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var cancelHttp = new HttpClient(new DelayHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var cancellationPropagated = false;
        try { await new SelfUpdateService(timedOut, cancelHttp, FixturePublicKey).CheckAndStage(callerCanceled.Token); }
        catch (OperationCanceledException) when (callerCanceled.IsCancellationRequested) { cancellationPropagated = true; }
        Require(cancellationPropagated && ReadApp(timedOut) == "old", "Explicit caller cancellation still propagates without changing the program");
        var corrupt = Fixture(root, "hash-mismatch", "windows");
        using var corruptHttp = new HttpClient(new Handler(signed, zip.Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray()));
        Require((await new SelfUpdateService(corrupt, corruptHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Blocked &&
            ReadApp(corrupt) == "old", "Wrong SHA256 leaves the current program intact");
        foreach (var attack in new[] { "traversal", "symlink", "duplicate", "nonprogram" })
        {
            var hostile = Package("windows", attack);
            var fixture = Fixture(root, attack, "windows");
            using var hostileHttp = new HttpClient(new Handler(Feed([Release(hostile)]), hostile));
            Require((await new SelfUpdateService(fixture, hostileHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Blocked &&
                ReadApp(fixture) == "old", "Signed hostile ZIP rejected: " + attack);
        }
        var macZip = Package("mac");
        var mac = Fixture(root, "mac-layout", "mac");
        using var macHttp = new HttpClient(new Handler(Feed([Release(macZip, "mac")]), macZip));
        var macService = new SelfUpdateService(mac, macHttp, FixturePublicKey);
        Require((await macService.CheckAndStage()).Status == SelfUpdateStatus.Staged, "Mac app-bundle ZIP stages within its own application boundary");
        planFile = PlanFile(mac);
        Require(SelfUpdateService.ApplyPending(planFile, mac.InstallationRoot, Hash(planFile), FixturePublicKey).Applied &&
            ReadApp(mac) == "new", "Mac bundle-shaped fixture replaces only the .app directory");
        var macPersonal = Fixture(root, "mac-original-personal", "mac");
        var personalRelative = Path.Combine("Contents", "Resources", "personal-draft.txt");
        var originalPersonal = Path.Combine(macPersonal.InstallationRoot, personalRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(originalPersonal)!);
        File.WriteAllText(originalPersonal, "keep-original-mac-personal", new UTF8Encoding(false));
        using var macPersonalHttp = new HttpClient(new Handler(Feed([Release(macZip, "mac")]), macZip));
        Require((await new SelfUpdateService(macPersonal, macPersonalHttp, FixturePublicKey).CheckAndStage()).Status == SelfUpdateStatus.Staged,
            "Mac original-personal-file fixture stages a real bundle ZIP");
        planFile = PlanFile(macPersonal);
        var macPersonalPlan = ReadPlan(planFile);
        Require(SelfUpdateService.ApplyPending(planFile, macPersonal.InstallationRoot, Hash(planFile), FixturePublicKey).Applied,
            "Mac original-personal-file fixture performs a real bundle replacement");
        var macPersonalCheck = await new SelfUpdateService(macPersonal with { CurrentVersion = "1.7.1" },
            macPersonalHttp, FixturePublicKey).CheckAndStage();
        Require(macPersonalCheck.Status == SelfUpdateStatus.UpToDate && Directory.Exists(macPersonalPlan.BackupRoot) &&
            File.ReadAllText(Path.Combine(macPersonalPlan.BackupRoot, personalRelative)) == "keep-original-mac-personal" &&
            File.ReadAllText(Path.Combine(macPersonalPlan.BackupRoot, "Contents", "MacOS", "MathGPT")) == "old" &&
            ReadApp(macPersonal) == "new", "Mac cleanup preserves original unknown personal files in its rollback bundle");
        Require(!await SelfUpdateService.TryRunApplyWorker(["--other"]), "Normal startup is not treated as an apply worker");
        Require(await SelfUpdateService.TryRunApplyWorker(["--self-update-apply"]) && Environment.ExitCode != 0,
            "Malformed worker is handled without GUI or application startup");
        Environment.ExitCode = 0;
        File.WriteAllText(Path.Combine(root, "self-update-results.json"), JsonSerializer.Serialize(new
        {
            passed = checks.Count, checks, realFilesystemUpgrade = true, realFilesystemRollback = true,
            realMacHardwareTested = false, productionInstallChanged = false, paidCalls = false,
            productionSigningPrivateKeyRead = false
        }, Json), new UTF8Encoding(false));
        Console.WriteLine($"Passed {checks.Count} signed-feed, ZIP staging, real replacement and rollback checks.");
    }

    private static SelfUpdateOptions Fixture(string root, string name, string platform)
    {
        var folder = Path.Combine(root, name);
        var installation = Path.Combine(folder, platform == "windows" ? "MathGPT" : "MathGPT.app");
        var data = Path.Combine(folder, "data");
        var workspace = Path.Combine(folder, "workspaces");
        Directory.CreateDirectory(data); Directory.CreateDirectory(workspace);
        var app = Path.Combine(installation, platform == "windows" ? "MathGPT.exe" : "Contents/MacOS/MathGPT");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.WriteAllText(app, "old", new UTF8Encoding(false));
        if (platform == "mac") File.WriteAllText(Path.Combine(installation, "Contents/Info.plist"), "fixture");
        File.WriteAllText(Path.Combine(data, "account-fixture.txt"), "keep-account");
        File.WriteAllText(Path.Combine(workspace, "draft.txt"), "keep-workspace");
        return new() { InstallationRoot = installation, DataRoot = data, CurrentVersion = "1.7.0", Platform = platform,
            Architecture = "x64", ProtectedDirectories = [workspace] };
    }
    private static string ReadApp(SelfUpdateOptions options) => File.ReadAllText(Path.Combine(options.InstallationRoot,
        options.Platform == "windows" ? "MathGPT.exe" : "Contents/MacOS/MathGPT"));
    private static string PlanFile(SelfUpdateOptions options)
    {
        var plan = ReadPlan(Path.Combine(options.DataRoot, "self-update", "pending.json"));
        return Path.Combine(options.DataRoot, "self-update", plan.Id, "plan.json");
    }
    private static SelfUpdateApplyPlan ReadPlan(string path) =>
        JsonSerializer.Deserialize<SelfUpdateApplyPlan>(File.ReadAllText(path), Json)!;
    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static void WriteInventory(string root, string inventory) => File.WriteAllText(inventory,
        string.Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (File: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/'))).OrderBy(f => f.Relative, StringComparer.Ordinal)
            .Select(f => Hash(f.File) + "\t" + f.Relative + "\n")), new UTF8Encoding(false));
    private static Dictionary<string, object> Release(byte[] zip, string platform = "windows", string architecture = "x64") => new()
    {
        ["version"] = "1.7.1", ["platform"] = platform, ["architecture"] = architecture,
        ["file_name"] = platform == "windows" ? $"MathGPT-1.7.1-Portable-win-{architecture}.zip" : $"MathGPT-1.7.1-mac-{architecture}-preview.zip",
        ["bytes"] = zip.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()
    };
    private static byte[] Feed(Dictionary<string, object>[] releases, bool expired = false)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, app = "mathgpt-desktop", generated_at_utc = now.AddDays(expired ? -100 : -1).ToString("O"),
            expires_at_utc = now.AddDays(expired ? -20 : 89).ToString("O"), releases
        });
        var signer = new Ed25519Signer(); signer.Init(true, FixtureKey); signer.BlockUpdate(payload, 0, payload.Length);
        return JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, key_id = SelfUpdateService.KeyId,
            payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signer.GenerateSignature()) });
    }
    private static byte[] Package(string platform, string? attack = null, string architecture = "x64")
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var prefix = platform == "windows" ? $"MathGPT-1.7.1-Portable-win-{architecture}/" : "MathGPT.app/Contents/";
            void Add(string path, string text, bool link = false)
            {
                var entry = archive.CreateEntry(path);
                entry.ExternalAttributes = (link ? 0xa1ff : 0x81a4) << 16;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text);
            }
            if (platform == "windows")
            {
                Add(prefix + "MathGPT.exe", "new"); Add(prefix + "MathGPT.Bridge.exe", "new-bridge");
                Add(prefix + "README.md", "fixture release"); Add(prefix + "LICENSE", "fixture license");
            }
            else { Add(prefix + "MacOS/MathGPT", "new"); Add(prefix + "Info.plist", "fixture"); }
            if (attack == "traversal") Add(prefix + "../outside.txt", "blocked");
            if (attack == "symlink") Add(prefix + "THIRD-PARTY-NOTICES.md", "target", true);
            if (attack == "duplicate") Add(prefix + "mathgpt.exe", "collision");
            if (attack == "nonprogram") Add(prefix + "settings.json", "must never replace account settings");
        }
        return output.ToArray();
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is UserException or JsonException or FormatException) { return; }
        throw new InvalidOperationException("Expected signed update policy rejection.");
    }
    private static bool PolicyRejected(Action action, string expectedMessage)
    {
        try { action(); }
        catch (UserException ex) { return ex.Message == expectedMessage; }
        return false;
    }
    private sealed class DelayHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
    private sealed class Handler(byte[] feed, byte[] package, bool redirect = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsoluteUri != SelfUpdateService.FeedUrl &&
                !request.RequestUri.AbsoluteUri.StartsWith(SelfUpdateService.DownloadRoot + "MathGPT-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture URL.");
            if (redirect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.invalid/payload.zip") } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(request.RequestUri.AbsoluteUri == SelfUpdateService.FeedUrl ? feed : package) });
        }
    }
}
