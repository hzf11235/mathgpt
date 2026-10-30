using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MathGPT.Core;

public sealed partial class SelfUpdateService
{
    private sealed record ExitIntent(int Pid, long StartTimeTicks, string Executable, string PlanSha256,
        string Nonce, DateTimeOffset CreatedAtUtc);
    private static (string PlanFile, ExitIntent Intent)? normalExit;

    private static void PrepareExitIntent(string planFile, string digest, string executable)
    {
        using var process = Process.GetCurrentProcess();
        var intent = new ExitIntent(process.Id, process.StartTime.ToUniversalTime().Ticks, executable, digest,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow);
        var folder = Path.GetDirectoryName(planFile)!;
        SelfUpdateFiles.NoLinks(folder);
        var marker = Path.Combine(folder, "normal-exit.json");
        SelfUpdateFiles.NoLinks(marker);
        if (File.Exists(marker)) File.Delete(marker);
        SafeFiles.AtomicWrite(Path.Combine(folder, "exit-intent.json"), JsonSerializer.Serialize(intent, JsonOptions));
        normalExit = (planFile, intent);
    }

    // Only the actual application shutdown hook calls this, after a successful
    // normal close. A kill/crash cannot authorize applying a prepared update.
    public static void CommitNormalExit(int exitCode)
    {
        var pending = normalExit;
        normalExit = null;
        if (pending is null || exitCode != 0) return;
        try
        {
            var (file, intent) = pending.Value;
            SelfUpdateFiles.NoLinks(file);
            if (Environment.ProcessId != intent.Pid || !SelfUpdateFiles.Hash(file).Equals(intent.PlanSha256, StringComparison.OrdinalIgnoreCase)) return;
            SafeFiles.AtomicWrite(Path.Combine(Path.GetDirectoryName(file)!, "normal-exit.json"), JsonSerializer.Serialize(intent, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserException) { }
    }

    private static ExitIntent ReadExitIntent(string file, string digest, int parentPid, string target, string platform)
    {
        var path = Path.Combine(Path.GetDirectoryName(file)!, "exit-intent.json");
        var intent = ReadIntent(path);
        var expected = platform == "windows" ? Path.Combine(target, "MathGPT.exe") : Path.Combine(target, "Contents", "MacOS", "MathGPT");
        if (intent.Pid != parentPid || !SelfUpdateFiles.Same(intent.Executable, expected) || intent.StartTimeTicks <= 0 ||
            !intent.PlanSha256.Equals(digest, StringComparison.OrdinalIgnoreCase) || intent.Nonce.Length != 64 ||
            intent.CreatedAtUtc > DateTimeOffset.UtcNow.AddSeconds(10) || intent.CreatedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-3))
            throw new UserException("更新退出凭据已过期或父进程不符。");
        return intent;
    }

    private static ExitIntent ReadIntent(string path)
    {
        SelfUpdateFiles.NoLinks(path);
        if (new FileInfo(path).Length > 16_384) throw new UserException("更新退出凭据无效。");
        return JsonSerializer.Deserialize<ExitIntent>(File.ReadAllText(path), JsonOptions) ?? throw new UserException("更新退出凭据无效。");
    }

    private static void VerifyNormalExit(string file, ExitIntent intent)
    {
        if (ReadIntent(Path.Combine(Path.GetDirectoryName(file)!, "normal-exit.json")) != intent)
            throw new UserException("未确认原程序正常退出，更新保留待下次重试。");
    }
}
