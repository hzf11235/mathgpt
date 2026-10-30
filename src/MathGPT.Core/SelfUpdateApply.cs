using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MathGPT.Core;

public sealed class SelfUpdateApplyPlan
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public string StageRoot { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public string Version { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ZipFile { get; set; } = "";
    public string ZipSha256 { get; set; } = "";
    public string InventoryFile { get; set; } = "";
    public string InventorySha256 { get; set; } = "";
    public string BeforeInventoryFile { get; set; } = "";
    public string BeforeInventorySha256 { get; set; } = "";
    public string[] ProtectedDirectories { get; set; } = [];
    public string FeedEnvelope { get; set; } = "";
}

public sealed record SelfUpdateApplyResult(bool Applied, bool RolledBack, string Message);

public sealed partial class SelfUpdateService
{
    internal static SelfUpdateApplyPlan ReadPlan(string file)
    {
        SelfUpdateFiles.NoLinks(file);
        if (new FileInfo(file).Length > 524_288) throw new UserException("更新任务内容过大。");
        return JsonSerializer.Deserialize<SelfUpdateApplyPlan>(File.ReadAllText(file, new UTF8Encoding(false, true)), JsonOptions)
            ?? throw new UserException("更新任务格式无效。");
    }
    internal static void ValidatePlan(SelfUpdateApplyPlan plan, string expectedTarget, byte[] publicKey, bool allowInstalledVersion = false,
        DateTimeOffset? verificationTime = null)
    {
        if (plan.Schema != 1 || !Regex.IsMatch(plan.Id, "^[a-f0-9]{32}$") ||
            !SelfUpdateFiles.Same(plan.TargetRoot, expectedTarget) || IsManagedInstallation(expectedTarget))
            throw new UserException("更新任务目标已变化或受本机发布器管理。");
        SelfUpdateFiles.ValidateOptions(new()
        {
            InstallationRoot = plan.TargetRoot, DataRoot = plan.DataRoot, CurrentVersion = plan.CurrentVersion,
            Platform = plan.Platform, Architecture = plan.Architecture, ProtectedDirectories = plan.ProtectedDirectories
        });
        var job = Path.Combine(SelfUpdateFiles.Local(plan.DataRoot), "self-update", plan.Id);
        if (!SelfUpdateFiles.Same(plan.ZipFile, Path.Combine(job, "package.zip")) ||
            !SelfUpdateFiles.Same(plan.InventoryFile, Path.Combine(job, "files.tsv")) ||
            !SelfUpdateFiles.Same(plan.BeforeInventoryFile, Path.Combine(job, "before.tsv")) ||
            !SelfUpdateFiles.Same(plan.StageRoot, SelfUpdateFiles.Sibling(plan.TargetRoot, "stage", plan.Id)) ||
            !SelfUpdateFiles.Same(plan.BackupRoot, SelfUpdateFiles.Sibling(plan.TargetRoot, "rollback", plan.Id)))
            throw new UserException("更新任务路径不属于本次暂存目录。");
        foreach (var path in new[] { job, plan.ZipFile, plan.InventoryFile, plan.BeforeInventoryFile, plan.StageRoot, plan.BackupRoot })
            SelfUpdateFiles.NoLinks(path);
        var release = SelectRelease(Convert.FromBase64String(plan.FeedEnvelope), plan.Platform, plan.Architecture, plan.CurrentVersion, publicKey, verificationTime);
        if (release is null || release.Version != plan.Version || !release.Sha256.Equals(plan.ZipSha256, StringComparison.OrdinalIgnoreCase) ||
            VersionOf(plan.Version) <= VersionOf(plan.CurrentVersion) && !allowInstalledVersion)
            throw new UserException("更新任务不符合已签名清单，已停止替换。");
    }

    // The optional key/fault hook is for isolated fixtures. The real worker always uses
    // the compiled public key and never accepts a replacement key from disk or arguments.
    public static SelfUpdateApplyResult ApplyPending(string planFile, string expectedTarget, string expectedPlanSha256,
        byte[]? verificationKey = null, Action? afterBackupMoved = null)
    {
        SelfUpdateApplyPlan? plan = null;
        Dictionary<string, string>? oldFiles = null, newFiles = null;
        string? atomicWork = null;
        var workFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backupPrepared = false;
        var macSwapped = false;
        var movedOld = false;
        var movedNew = false;
        try
        {
            if (!SelfUpdateFiles.Hash(planFile).Equals(expectedPlanSha256, StringComparison.OrdinalIgnoreCase))
                throw new UserException("更新任务在退出前后发生变化，已停止替换。");
            plan = ReadPlan(planFile);
            ValidatePlan(plan, expectedTarget, verificationKey ?? Convert.FromBase64String(PublicKeyBase64));
            if (!SelfUpdateFiles.Same(planFile, Path.Combine(plan.DataRoot, "self-update", plan.Id, "plan.json")))
                throw new UserException("更新任务文件位置异常。");
            using var updateLock = new FileStream(Path.Combine(plan.DataRoot, "self-update", "update.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            SelfUpdateFiles.ValidateInstalledTree(plan.TargetRoot, plan.Platform);
            SelfUpdateFiles.ValidateInstalledTree(plan.StageRoot, plan.Platform);
            if (plan.Platform != "windows" && Directory.Exists(plan.BackupRoot))
                throw new UserException("回退目录已存在，已保留待处理更新。");
            if (TargetBusy(plan.TargetRoot)) throw new UserException("MathGPT 或其适配器仍在使用程序目录，更新已保留待下次正常退出。");
            if (!SelfUpdateFiles.Hash(plan.ZipFile).Equals(plan.ZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new UserException("更新 ZIP 已变化，已停止替换。");
            // Rebuild the expected inventory from the cryptographically verified ZIP.
            // An editable local inventory can never authorize different program bytes.
            var recheck = Path.Combine(Path.GetDirectoryName(planFile)!, "recheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                var release = SelectRelease(Convert.FromBase64String(plan.FeedEnvelope), plan.Platform, plan.Architecture,
                    plan.CurrentVersion, verificationKey ?? Convert.FromBase64String(PublicKeyBase64))!;
                var extracted = SelfUpdateFiles.Extract(plan.ZipFile, recheck, release, CancellationToken.None);
                var rebuilt = Path.Combine(recheck, "rebuilt.tsv");
                SelfUpdateFiles.WriteInventory(extracted, rebuilt);
                if (!SelfUpdateFiles.Hash(rebuilt).Equals(plan.InventorySha256, StringComparison.OrdinalIgnoreCase))
                    throw new UserException("暂存文件清单与已签名 ZIP 内容不符。");
            }
            finally { SelfUpdateFiles.RemoveOwned(recheck, Path.GetDirectoryName(planFile)!); }
            SelfUpdateFiles.VerifyInventory(plan.StageRoot, plan.InventoryFile, plan.InventorySha256);
            SelfUpdateFiles.VerifyInventory(plan.TargetRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256);
            if (plan.Platform == "windows")
            {
                oldFiles = InventoryEntries(plan.BeforeInventoryFile);
                newFiles = InventoryEntries(plan.InventoryFile);
                if (Directory.Exists(plan.BackupRoot))
                {
                    // A fully verified backup from a caught failure can be reused.
                    // Partial or edited backups are preserved and cannot authorize writes.
                    SelfUpdateFiles.ValidateInstalledTree(plan.BackupRoot, plan.Platform);
                    SelfUpdateFiles.VerifyInventory(plan.BackupRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256);
                }
                else
                {
                    Directory.CreateDirectory(plan.BackupRoot);
                    foreach (var file in oldFiles)
                        CopyVerifiedFile(SafeFiles.Child(plan.TargetRoot, file.Key),
                            SafeFiles.Child(plan.BackupRoot, file.Key), file.Value);
                    SelfUpdateFiles.VerifyInventory(plan.BackupRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256);
                }
                atomicWork = SelfUpdateFiles.Local(plan.StageRoot + ".apply-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(atomicWork) || File.Exists(atomicWork))
                    throw new UserException("更新原子替换目录已存在，已保留文件。");
                Directory.CreateDirectory(atomicWork);
                backupPrepared = true;
                afterBackupMoved?.Invoke();
                // The Windows main program is self-contained. Publish it atomically,
                // without ever removing its directory or executable entry.
                foreach (var file in MainProgramFirst(newFiles))
                {
                    var target = SafeFiles.Child(plan.TargetRoot, file.Key);
                    if (File.Exists(target) && (!oldFiles.TryGetValue(file.Key, out var oldHash) ||
                        !SelfUpdateFiles.Hash(target).Equals(oldHash, StringComparison.OrdinalIgnoreCase)))
                        throw new UserException("程序文件在替换前发生变化，已保留内容。");
                    AtomicCopyFile(SafeFiles.Child(plan.StageRoot, file.Key), target, file.Value, atomicWork, workFiles);
                }
                foreach (var file in oldFiles.Where(file => !newFiles.ContainsKey(file.Key)))
                    RemoveVerifiedFile(SafeFiles.Child(plan.TargetRoot, file.Key), file.Value, atomicWork, workFiles);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // RENAME_SWAP keeps a complete bundle at the user-visible path,
                // including if this worker stops immediately after the system call.
                SwapMacDirectories(plan.TargetRoot, plan.StageRoot);
                macSwapped = true;
                Directory.Move(plan.StageRoot, plan.BackupRoot);
                backupPrepared = true;
                afterBackupMoved?.Invoke();
            }
            else
            {
                // Bundle-layout fixtures on non-Mac hosts do not exercise Darwin.
                Directory.Move(plan.TargetRoot, plan.BackupRoot); movedOld = true;
                afterBackupMoved?.Invoke();
                Directory.Move(plan.StageRoot, plan.TargetRoot); movedNew = true;
            }
            SelfUpdateFiles.VerifyInventory(plan.TargetRoot, plan.InventoryFile, plan.InventorySha256);
            SafeFiles.AtomicWrite(Path.Combine(plan.DataRoot, "self-update", "last-applied.json"), JsonSerializer.Serialize(plan, JsonOptions));
            var pending = Path.Combine(plan.DataRoot, "self-update", "pending.json");
            if (File.Exists(pending) && SelfUpdateFiles.Hash(pending).Equals(expectedPlanSha256, StringComparison.OrdinalIgnoreCase))
                File.Delete(pending);
            if (plan.Platform == "windows")
                try
                {
                    SelfUpdateFiles.ValidateInstalledTree(plan.StageRoot, plan.Platform);
                    SelfUpdateFiles.VerifyInventory(plan.StageRoot, plan.InventoryFile, plan.InventorySha256);
                    SelfUpdateFiles.RemoveOwned(plan.StageRoot, Path.GetDirectoryName(plan.TargetRoot)!);
                }
                catch (Exception cleanupError) when (cleanupError is UserException or IOException or UnauthorizedAccessException) { }
            return new(true, false, "更新已应用；下次由用户正常打开程序。");
        }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or ArgumentException or FormatException or InvalidOperationException)
        {
            var restored = false;
            if (plan is not null && plan.Platform == "windows" && backupPrepared &&
                oldFiles is not null && newFiles is not null && atomicWork is not null)
                restored = RestoreWindowsFiles(plan, oldFiles, newFiles, atomicWork, workFiles);
            else if (plan is not null && macSwapped)
            {
                try
                {
                    var oldRoot = backupPrepared ? plan.BackupRoot : plan.StageRoot;
                    SelfUpdateFiles.VerifyInventory(oldRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256);
                    SwapMacDirectories(plan.TargetRoot, oldRoot);
                    // Keep the rejected new bundle for inspection/retry; never delete it.
                    if (backupPrepared && !Directory.Exists(plan.StageRoot))
                        Directory.Move(plan.BackupRoot, plan.StageRoot);
                    SelfUpdateFiles.VerifyInventory(plan.TargetRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256);
                    restored = true;
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException or UserException) { }
            }
            else if (plan is not null && movedOld)
            {
                try
                {
                    if (movedNew && Directory.Exists(plan.TargetRoot) && !Directory.Exists(plan.StageRoot))
                        Directory.Move(plan.TargetRoot, plan.StageRoot);
                    if (!Directory.Exists(plan.TargetRoot) && Directory.Exists(plan.BackupRoot))
                        Directory.Move(plan.BackupRoot, plan.TargetRoot);
                    restored = Directory.Exists(plan.TargetRoot) && !Directory.Exists(plan.BackupRoot);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { }
            }
            return new(false, restored, restored ? "更新替换失败，已恢复原版；更新保留待重试。" :
                ex is UserException ? Privacy.Clean(ex.Message) : "更新未应用，程序与待处理文件已保留。");
        }
        finally
        {
            if (atomicWork is not null) CleanupAtomicWork(atomicWork, workFiles);
        }
    }

    private static Dictionary<string, string> InventoryEntries(string file) =>
        File.ReadAllLines(file, new UTF8Encoding(false, true)).Select(line => line.Split('\t'))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<KeyValuePair<string, string>> MainProgramFirst(Dictionary<string, string> files) =>
        files.OrderBy(file => file.Key.Equals("MathGPT.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(file => file.Key, StringComparer.Ordinal);

    private static void CopyVerifiedFile(string source, string target, string expectedHash)
    {
        SelfUpdateFiles.NoLinks(source); SelfUpdateFiles.NoLinks(target);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65_536, FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        if (!SelfUpdateFiles.Hash(target).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new UserException("更新复制文件未通过摘要校验，已停止替换。");
    }

    private static void AtomicCopyFile(string source, string target, string expectedHash, string work,
        Dictionary<string, string> workFiles)
    {
        var temporary = SafeFiles.Child(work, Guid.NewGuid().ToString("N") + ".tmp");
        workFiles.Add(temporary, expectedHash);
        CopyVerifiedFile(source, temporary, expectedHash);
        SelfUpdateFiles.NoLinks(target);
        if (File.Exists(target)) File.Replace(temporary, target, null);
        else File.Move(temporary, target);
    }

    private static void RemoveVerifiedFile(string target, string expectedHash, string work,
        Dictionary<string, string> workFiles)
    {
        if (!File.Exists(target)) return;
        if (!SelfUpdateFiles.Hash(target).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new UserException("待移除程序文件已变化，已保留内容。");
        var temporary = SafeFiles.Child(work, Guid.NewGuid().ToString("N") + ".tmp");
        workFiles.Add(temporary, expectedHash);
        // Moving first keeps unexpected racing edits available for recovery.
        File.Move(target, temporary);
        if (!SelfUpdateFiles.Hash(temporary).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(target)) File.Move(temporary, target);
            throw new UserException("待移除程序文件已变化，已保留内容。");
        }
    }

    private static bool RestoreWindowsFiles(SelfUpdateApplyPlan plan, Dictionary<string, string> oldFiles,
        Dictionary<string, string> newFiles, string work, Dictionary<string, string> workFiles)
    {
        var restored = true;
        foreach (var file in MainProgramFirst(oldFiles))
        {
            try
            {
                var target = SafeFiles.Child(plan.TargetRoot, file.Key);
                if (File.Exists(target))
                {
                    var current = SelfUpdateFiles.Hash(target);
                    if (current.Equals(file.Value, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!newFiles.TryGetValue(file.Key, out var signedHash) ||
                        !current.Equals(signedHash, StringComparison.OrdinalIgnoreCase))
                        throw new UserException("回退时发现已变化的文件，已保留内容。");
                }
                AtomicCopyFile(SafeFiles.Child(plan.BackupRoot, file.Key), target, file.Value, work, workFiles);
            }
            catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException) { restored = false; }
        }
        foreach (var file in newFiles.Where(file => !oldFiles.ContainsKey(file.Key)))
            try { RemoveVerifiedFile(SafeFiles.Child(plan.TargetRoot, file.Key), file.Value, work, workFiles); }
            catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException) { restored = false; }
        try { SelfUpdateFiles.VerifyInventory(plan.TargetRoot, plan.BeforeInventoryFile, plan.BeforeInventorySha256); }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException) { restored = false; }
        return restored;
    }

    private static void CleanupAtomicWork(string work, Dictionary<string, string> workFiles)
    {
        // Only delete exact bytes recorded by this invocation. Preserve unknown or
        // changed files, including a partial copy, instead of recursive cleanup.
        foreach (var file in workFiles)
            try
            {
                if (File.Exists(file.Key) && SelfUpdateFiles.Hash(file.Key).Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                    File.Delete(file.Key);
            }
            catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException) { }
        try
        {
            SelfUpdateFiles.NoLinks(work);
            if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
        }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException) { }
    }

    // Apple declares RENAME_SWAP = 0x00000002 in bsd/sys/stdio.h:
    // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stdio.h
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameMac([MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

    private static void SwapMacDirectories(string first, string second)
    {
        SelfUpdateFiles.NoLinks(first); SelfUpdateFiles.NoLinks(second);
        try
        {
            if (RenameMac(first, second, 2) != 0)
                throw new UserException("当前文件系统无法原子交换应用包，已保留原程序与更新。");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { throw new UserException("此系统不支持应用包原子交换，已保留原程序与更新。"); }
    }

    private static bool TargetBusy(string target)
    {
        foreach (var name in new[] { "MathGPT", "MathGPT.Bridge" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        var file = process.MainModule?.FileName;
                        if (file is null || SelfUpdateFiles.Within(file, target)) return true;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    { return true; }
                }
        return false;
    }

    public static async Task<bool> TryRunApplyWorker(string[] args)
    {
        if (!args.Contains("--self-update-apply", StringComparer.Ordinal)) return false;
        string? validatedPlanFile = null;
        try
        {
            if (args.Length != 8 || args[0] != "--self-update-apply" || args[2] != "--target" ||
                args[4] != "--parent" || args[6] != "--plan-sha" || !int.TryParse(args[5], out var parentId) ||
                parentId <= 0 || parentId == Environment.ProcessId || !Regex.IsMatch(args[7], "^[a-fA-F0-9]{64}$"))
                throw new UserException("更新工作程序参数无效。");
            var planFile = SelfUpdateFiles.Local(args[1]);
            var plan = ReadPlan(planFile);
            if (!SelfUpdateFiles.Hash(planFile).Equals(args[7], StringComparison.OrdinalIgnoreCase))
                throw new UserException("更新任务摘要不匹配。");
            ValidatePlan(plan, args[3], Convert.FromBase64String(PublicKeyBase64));
            var workerRoot = Path.Combine(Path.GetDirectoryName(planFile)!, "worker");
            var ownExecutable = Environment.ProcessPath ?? "";
            if (!SelfUpdateFiles.Within(ownExecutable, workerRoot) ||
                VersionOf(plan.CurrentVersion) != VersionOf(typeof(SelfUpdateService).Assembly.GetName().Version!.ToString(3)))
                throw new UserException("更新工作程序与原版本不匹配。");
            if (!SelfUpdateFiles.Same(planFile, Path.Combine(plan.DataRoot, "self-update", plan.Id, "plan.json")))
                throw new UserException("更新任务文件位置异常。");
            var intent = ReadExitIntent(planFile, args[7], parentId, plan.TargetRoot, plan.Platform);
            if (!SelfUpdateFiles.Hash(ownExecutable).Equals(SelfUpdateFiles.Hash(intent.Executable), StringComparison.OrdinalIgnoreCase))
                throw new UserException("更新工作程序与当前安装文件不符。");
            validatedPlanFile = planFile;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                using var parent = Process.GetProcessById(parentId);
                try
                {
                    if (!parent.HasExited && (parent.StartTime.ToUniversalTime().Ticks != intent.StartTimeTicks ||
                        !SelfUpdateFiles.Same(parent.MainModule?.FileName ?? "", intent.Executable)))
                        throw new UserException("更新父进程身份已变化，已停止替换。");
                }
                catch (Exception inspectError) when (
                    (inspectError is InvalidOperationException or System.ComponentModel.Win32Exception) && parent.HasExited) { }
                await parent.WaitForExitAsync(timeout.Token);
                if (parent.ExitCode != 0) throw new UserException("原程序异常退出，更新保留待下次正常退出。");
            }
            catch (ArgumentException) { }
            catch (OperationCanceledException)
            {
                WriteWorkerReceipt(planFile, new(false, false, "原窗口尚未正常退出，更新保留待下次重试。"));
                Environment.ExitCode = 1; return true;
            }
            if (ReadExitIntent(planFile, args[7], parentId, plan.TargetRoot, plan.Platform) != intent)
                throw new UserException("更新退出凭据已变化，已停止替换。");
            VerifyNormalExit(planFile, intent);
            var result = ApplyPending(planFile, args[3], args[7]);
            WriteWorkerReceipt(planFile, result);
            Environment.ExitCode = result.Applied ? 0 : 1;
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            // Never invoke GUI configuration or expose a raw exception/secret in worker mode.
            if (validatedPlanFile is not null)
                try
                {
                    WriteWorkerReceipt(validatedPlanFile, new(false, false, ex is UserException ? Privacy.Clean(ex.Message) : "更新工作程序未完成；待处理更新保留。"));
                }
                catch (Exception) { }
        }
        return true;
    }

    private static void WriteWorkerReceipt(string planFile, SelfUpdateApplyResult result)
    {
        var folder = Path.GetDirectoryName(planFile)!;
        SelfUpdateFiles.NoLinks(folder);
        SafeFiles.AtomicWrite(Path.Combine(folder, "apply-result.json"), JsonSerializer.Serialize(new
        { result.Applied, result.RolledBack, result.Message, timestampUtc = DateTimeOffset.UtcNow }, JsonOptions));
    }
}
