using System.Security.Cryptography;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class AuthenticodeAudit
{
    public static async Task Run(string root, string[] packages)
    {
        var results = new List<object>();
        void Check(string name, bool passed)
        {
            if (!passed) throw new InvalidOperationException(name);
            results.Add(new { name, passed });
            Console.WriteLine("PASS " + name);
        }
        var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        Check("known signed host exists", File.Exists(host));
        var subject = WindowsAuthenticode.TrustedSigner(host);
        Check("trusted native signer is returned", subject.Contains("Microsoft Corporation", StringComparison.Ordinal));
        Check("signed non-vendor host rejected by publisher policy",
            !await DesktopPackages.VerifyPackage(host, ClientKind.ClaudeDesktop));

        var unsigned = Path.Combine(root, "unsigned.exe");
        File.WriteAllText(unsigned, "unsigned fixture");
        Check("unsigned file rejected", Rejected(unsigned));
        var tampered = Path.Combine(root, "tampered.exe");
        File.Copy(host, tampered, false);
        using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 80;
            var original = stream.ReadByte();
            stream.Position = 80;
            stream.WriteByte((byte)(original ^ 1));
        }
        Check("modified signed file rejected", Rejected(tampered));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var cancelled = false;
        try { await DesktopPackages.Verify(host, ClientKind.CodexDesktop, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("cancelled verification does not proceed", cancelled);
        foreach (var package in packages)
        {
            var kind = Path.GetFileName(package).StartsWith("Claude-", StringComparison.Ordinal) ?
                ClientKind.ClaudeDesktop : ClientKind.CodexDesktop;
            using var source = File.OpenRead(package);
            var before = await SHA256.HashDataAsync(source);
            Console.WriteLine("VERIFY " + Path.GetFileName(package));
            Check("official MSIX trusted: " + kind, await DesktopPackages.VerifyPackage(package, kind));
            var other = kind == ClientKind.ClaudeDesktop ? ClientKind.CodexDesktop : ClientKind.ClaudeDesktop;
            Check("wrong MSIX publisher rejected: " + kind, !await DesktopPackages.VerifyPackage(package, other));
            source.Position = 0;
            var after = await SHA256.HashDataAsync(source);
            Check("official MSIX bytes unchanged: " + kind,
                CryptographicOperations.FixedTimeEquals(before, after));
        }
        SafeFiles.AtomicWrite(Path.Combine(root, "authenticode-results.json"),
            JsonSerializer.Serialize(new { checks = results, failed = 0, paid_requests = 0, native_signature_verification = true }, JsonData.Options));
        Console.WriteLine("RESULT " + results.Count + "/" + results.Count);
    }

    private static bool Rejected(string file)
    {
        try { WindowsAuthenticode.TrustedSigner(file); }
        catch (UserException) { return true; }
        return false;
    }
}
