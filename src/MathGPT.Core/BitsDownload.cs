using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MathGPT.Core;

// BITS is the Windows download manager: it owns HTTP retries, partial files and restart recovery.
public sealed class BitsDownload(StateStore store)
{
    public async Task<string> Fetch(PackageRelease release, string directory,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (DesktopClientDownloads.IsMirror(release))
        {
            DesktopClientDownloads.ValidateRelease(release);
            var mirrorDestination = SafeFiles.Child(directory, release.FileName);
            var mirrorRecord = store.State.Downloads.LastOrDefault(r => r.Url == release.Url && r.Destination == mirrorDestination);
            if (mirrorRecord is null)
            {
                mirrorRecord = new() { Url = release.Url, Destination = mirrorDestination };
                store.State.Downloads.Add(mirrorRecord);
            }
            mirrorRecord.PartialFile = DesktopClientDownloads.PartialPath(release, directory);
            mirrorRecord.Status = "下载完整官方安装包，可暂停后重试"; store.Save();
            try
            {
                var downloaded = await DesktopClientDownloads.FetchContent(release, directory, progress, ct);
                progress?.Report(new("正在核验 SHA256 与原厂应用签名", 100));
                if (!await Verify(downloaded, release, ct))
                {
                    mirrorRecord.Status = "校验失败，禁止安装"; store.Save();
                    throw new UserException("安装包 SHA256、原厂签名或应用身份校验失败，已阻止安装；不会更换来源。");
                }
                mirrorRecord.Status = "SHA256 与原厂签名已通过"; return downloaded;
            }
            catch (OperationCanceledException) { mirrorRecord.Status = "已暂停，可从断点重试"; throw; }
            catch { if (mirrorRecord.Status != "校验失败，禁止安装") mirrorRecord.Status = "下载未完成，已保留断点"; throw; }
            finally { store.Save(); }
        }
        UrlPolicy.OfficialDownload(release.Url);
        SafeFiles.Writable(directory);
        var destination = SafeFiles.Child(directory, release.FileName);
        if (File.Exists(destination))
        {
            if (await Verify(destination, release, ct)) return destination;
            throw new UserException("缓存安装包校验失败。请在下载页删除损坏包后重试，或选择空的下载目录。");
        }
        var record = store.State.Downloads.LastOrDefault(r => r.Url == release.Url && r.Destination == destination);
        if (record is null)
        {
            record = new() { Url = release.Url, Destination = destination };
            store.State.Downloads.Add(record);
            store.Save();
        }
        await Task.Run(async () =>
        {
            IBackgroundCopyManager? manager = null;
            IBackgroundCopyJob? job = null;
            try
            {
                manager = (IBackgroundCopyManager)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("4991D34B-80A1-4291-83B6-3328366B9097"), true)!)!;
                if (Guid.TryParse(record.BitsJobId, out var existing))
                {
                    try
                    {
                        manager.GetJob(ref existing, out job);
                        job.GetDisplayName(out var displayName);
                        if (displayName != "MathGPT-" + record.Id)
                            throw new UserException("下载断点的归属不一致，已保留系统任务。请选择新的下载目录。");
                    }
                    catch (COMException) { record.BitsJobId = ""; }
                }
                if (job is null)
                {
                    manager.CreateJob("MathGPT-" + record.Id, 0, out var id, out job);
                    record.BitsJobId = id.ToString();
                    job.AddFile(release.Url, destination);
                    job.SetPriority(0); // Foreground, still managed by BITS.
                    job.SetMinimumRetryDelay(60);
                    job.SetNoProgressTimeout(120);
                    store.Save();
                }
                job.Resume();
                var lastProgressAt = DateTimeOffset.UtcNow;
                ulong previous = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    job.GetState(out var state);
                    job.GetProgress(out var status);
                    if (status.Transferred != previous) { previous = status.Transferred; lastProgressAt = DateTimeOffset.UtcNow; }
                    if (state == 6) { job.Complete(); record.Status = "下载完成，待校验"; break; }
                    if (state is 4 or 8 || DateTimeOffset.UtcNow - lastProgressAt > TimeSpan.FromMinutes(2))
                    {
                        job.Suspend();
                        throw new UserException("下载暂时失败，已保留断点。检查网络后点击重试；也可导入从官方取得的安装包。");
                    }
                    var total = status.Total == ulong.MaxValue ? 0 : (long)status.Total;
                    var fraction = total > 0 ? (double)status.Transferred / total * 100 : 0;
                    record.Status = state == 5 ? "网络等待，可暂停或重试" : "下载中";
                    progress?.Report(new(record.Status, fraction, (long)status.Transferred, total));
                    await Task.Delay(400, ct);
                }
            }
            catch (OperationCanceledException)
            {
                job?.Suspend(); record.Status = "已暂停，可从断点重试"; throw;
            }
            catch (COMException)
            {
                record.Status = "BITS 不可用";
                throw new UserException("Windows BITS 下载服务不可用。请检查系统下载服务，或导入已下载的官方安装包。");
            }
            finally
            {
                store.Save();
                if (job is not null) Marshal.ReleaseComObject(job);
                if (manager is not null) Marshal.ReleaseComObject(manager);
            }
        }, ct);
        progress?.Report(new("正在核验官方哈希", 100));
        if (!await Verify(destination, release, ct))
        {
            record.Status = "校验失败，禁止安装"; store.Save();
            throw new UserException("安装包与官方哈希不一致，已阻止安装。请删除损坏包后重试。");
        }
        record.Status = "哈希已通过"; store.Save();
        return destination;
    }
    public static async Task<bool> Verify(string file, PackageRelease release, CancellationToken ct = default)
    {
        SafeFiles.NoLinks(file);
        if (DesktopClientDownloads.IsMirror(release))
            return await DesktopClientDownloads.VerifyContent(file, release, ct) &&
                release.Platform == "windows" && await DesktopPackages.VerifyPackage(file, release.Kind, ct, release.Architecture);
        if (release.HashAlgorithm == "Authenticode") return await DesktopPackages.VerifyPackage(file, release.Kind, ct);
        await using var source = File.OpenRead(file);
        var bytes = release.HashAlgorithm == "SHA512" ? await SHA512.HashDataAsync(source, ct) : await SHA256.HashDataAsync(source, ct);
        byte[] expected;
        try { expected = release.HashAlgorithm == "SHA512" ? Convert.FromBase64String(release.Digest) : Convert.FromHexString(release.Digest); }
        catch (FormatException) { throw new UserException("官方校验信息格式无效，已停止安装。"); }
        return CryptographicOperations.FixedTimeEquals(bytes, expected);
    }
    public async Task Remove(DownloadRecord record)
    {
        // Only this application's saved destination and BITS job are eligible.
        await Task.Run(() =>
        {
            if (Guid.TryParse(record.BitsJobId, out var id))
            {
                var manager = (IBackgroundCopyManager)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("4991D34B-80A1-4291-83B6-3328366B9097"), true)!)!;
                try
                {
                    try
                    {
                        manager.GetJob(ref id, out var job);
                        try
                        {
                            job.GetDisplayName(out var displayName);
                            if (displayName != "MathGPT-" + record.Id) throw new UserException("下载任务不属于 MathGPT，已保留。");
                            job.Cancel();
                        }
                        finally { Marshal.ReleaseComObject(job); }
                    }
                    catch (COMException) { /* A completed job is already absent from BITS. */ }
                }
                finally { Marshal.ReleaseComObject(manager); }
            }
            SafeFiles.NoLinks(record.Destination);
            if (File.Exists(record.Destination)) File.Delete(record.Destination);
            if (record.PartialFile.Length > 0 &&
                Path.GetDirectoryName(record.PartialFile) == Path.GetDirectoryName(record.Destination) &&
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(record.PartialFile),
                    "^" + System.Text.RegularExpressions.Regex.Escape(Path.GetFileName(record.Destination)) + @"\.mathgpt-[a-f0-9]{64}\.partial$"))
            {
                SafeFiles.NoLinks(record.PartialFile);
                if (File.Exists(record.PartialFile)) File.Delete(record.PartialFile);
            }
            store.State.Downloads.Remove(record); store.Save();
        });
    }
}

[StructLayout(LayoutKind.Sequential)] internal struct BitsProgress
{ public ulong Total; public ulong Transferred; public uint FilesTotal; public uint FilesTransferred; }
[StructLayout(LayoutKind.Sequential)] internal struct BitsTimes
{ public long Created; public long Modified; public long Completed; }
[ComImport, Guid("5CE34C0D-0DC9-4C1F-897C-DAA1B78CEE7C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IBackgroundCopyManager
{
    void CreateJob([MarshalAs(UnmanagedType.LPWStr)] string name, int type, out Guid id, out IBackgroundCopyJob job);
    void GetJob(ref Guid id, out IBackgroundCopyJob job);
}
[ComImport, Guid("37668D37-507E-4160-9316-26306D150B12"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IBackgroundCopyJob
{
    void AddFileSet(uint count, IntPtr files);
    void AddFile([MarshalAs(UnmanagedType.LPWStr)] string remote, [MarshalAs(UnmanagedType.LPWStr)] string local);
    void EnumFiles(out IntPtr files);
    void Suspend();
    void Resume();
    void Cancel();
    void Complete();
    void GetId(out Guid id);
    void GetType(out int type);
    void GetProgress(out BitsProgress progress);
    void GetTimes(out BitsTimes times);
    void GetState(out int state);
    void GetError(out IntPtr error);
    void GetOwner([MarshalAs(UnmanagedType.LPWStr)] out string owner);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
    void GetDescription([MarshalAs(UnmanagedType.LPWStr)] out string description);
    void SetPriority(int priority);
    void GetPriority(out int priority);
    void SetNotifyFlags(uint flags);
    void GetNotifyFlags(out uint flags);
    void SetNotifyInterface(IntPtr callback);
    void GetNotifyInterface(out IntPtr callback);
    void SetMinimumRetryDelay(uint seconds);
    void GetMinimumRetryDelay(out uint seconds);
    void SetNoProgressTimeout(uint seconds);
}
