using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class ClaudeReadinessAudit
{
    public static async Task Run(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        var fixtureRoot = Path.Combine(root, "installed");
        Directory.CreateDirectory(fixtureRoot);
        var install = new ClientInstallation { Kind = ClientKind.ClaudeDesktop, Version = "3.6556.2.0",
            InstallationRoot = fixtureRoot, Executable = Path.Combine(fixtureRoot, "Claude.exe") };
        File.WriteAllText(install.Executable, "main application alone", new UTF8Encoding(false));
        var signatureCalls = 0;
        Task ValidSignature(string file, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(Path.Combine("win32-x64", "claude.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Verifier received a path outside the current installation.");
            signatureCalls++;
            return Task.CompletedTask;
        }
        var missing = await ClaudeDesktopReadiness.Check(install, "x64", ValidSignature);
        Require(missing.Status == ClaudeDesktopReadinessStatus.MissingHostEngine && !missing.Ready && signatureCalls == 0,
            "Existing main executable does not prove engine readiness");
        Require(ClaudeDesktopReadiness.BundledHostPath(install, "x64") is null, "Fast guard refuses missing engine");
        var release = new PackageRelease(ClientKind.ClaudeDesktop, "4.29.0.0",
            DesktopClientDownloads.AssetRoot + "Claude-offline-x64.msix", "Claude-offline-x64.msix", "SHA256",
            new string('a', 64), true, "x64")
        { Offline = true, Platform = "windows", PackageKind = "msix", ExpectedBytes = 2_292_493_683,
            OfficialUrl = "https://downloads.claude.ai/releases-offline/win32/x64/4.29.0/Claude-offline.msix" };
        Require(ClaudeDesktopReadiness.Plan(missing, install.Version, release) == ClaudeDesktopRepairAction.UpgradeOffline,
            "Older missing-engine install permits verified offline upgrade");
        Require(ClaudeDesktopReadiness.Plan(missing, "4.29.0.0", release) == ClaudeDesktopRepairAction.SameVersionConflict,
            "Same-version standard/offline conflict is explicit");
        Require(ClaudeDesktopReadiness.Plan(missing, "4.29", release) == ClaudeDesktopRepairAction.SameVersionConflict,
            "Equivalent shortened versions cannot bypass conflict");
        Require(ClaudeDesktopReadiness.Plan(missing, "4.30.0.0", release) == ClaudeDesktopRepairAction.InstalledVersionNewer,
            "Newer installed version is never downgraded");
        Require(ClaudeDesktopReadiness.Plan(missing, "unknown", release) == ClaudeDesktopRepairAction.Blocked,
            "Unknown installed version fails closed");
        Require(ClaudeDesktopReadiness.Plan(missing, install.Version, release with { Offline = false }) == ClaudeDesktopRepairAction.Blocked,
            "Regular online package cannot be used as offline repair");
        var engine = missing.HostEnginePath;
        Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        using (var file = File.Create(engine)) file.SetLength(ClaudeDesktopReadiness.MinimumHostEngineBytes);
        var ready = await ClaudeDesktopReadiness.Check(install, "x64", ValidSignature);
        Require(ready.Ready && signatureCalls == 1, "Normal readable engine requires the injected signature verifier");
        Require(ClaudeDesktopReadiness.BundledHostPath(install, "x64") == engine, "Fast guard finds only current bundled engine");
        Require(ClaudeDesktopReadiness.Plan(ready, install.Version, release) == ClaudeDesktopRepairAction.Reuse,
            "Ready installation needs no repeat download");
        var signatureRejected = await ClaudeDesktopReadiness.Check(install, "x64",
            (_, _) => throw new UserException("fixture invalid vendor signature"));
        Require(signatureRejected.Status == ClaudeDesktopReadinessStatus.SignatureRejected && !signatureRejected.Ready,
            "Vendor signature rejection cannot report success");
        Require(ClaudeDesktopReadiness.Plan(signatureRejected, install.Version, release) == ClaudeDesktopRepairAction.Blocked,
            "Signature failure cannot silently become an upgrade plan");
        using (var file = File.Open(engine, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(1);
        var tooSmall = await ClaudeDesktopReadiness.Check(install, "x64", ValidSignature);
        Require(tooSmall.Status == ClaudeDesktopReadinessStatus.InvalidHostEngine && signatureCalls == 1,
            "Truncated engine fails before signature verification");
        using (var file = File.Open(engine, FileMode.Open, FileAccess.Write, FileShare.None))
            file.SetLength(ClaudeDesktopReadiness.MaximumHostEngineBytes + 1);
        var tooLarge = await ClaudeDesktopReadiness.Check(install, "x64", ValidSignature);
        Require(tooLarge.Status == ClaudeDesktopReadinessStatus.InvalidHostEngine && signatureCalls == 1,
            "Implausibly large engine fails before signature verification");
        using (var file = File.Open(engine, FileMode.Open, FileAccess.Write, FileShare.None))
            file.SetLength(ClaudeDesktopReadiness.MinimumHostEngineBytes);
        using (var held = File.Open(engine, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unreadable = await ClaudeDesktopReadiness.Check(install, "x64", ValidSignature);
            Require(unreadable.Status == ClaudeDesktopReadinessStatus.Unreadable, "Locked engine is not ready");
        }
        foreach (var unsafeRoot in new[] { "", "relative", Path.GetPathRoot(root)!, @"\\server\share\Claude",
            Path.Combine(root, "..", "outside"), fixtureRoot + ":alternate" })
        {
            var unsafeInstall = new ClientInstallation { Kind = ClientKind.ClaudeDesktop, InstallationRoot = unsafeRoot };
            var report = await ClaudeDesktopReadiness.Check(unsafeInstall, "x64", ValidSignature);
            Require(report.Status == ClaudeDesktopReadinessStatus.UnsafePath &&
                ClaudeDesktopReadiness.BundledHostPath(unsafeInstall, "x64") is null, "Unsafe installation root is rejected: " + unsafeRoot);
        }
        Require((await ClaudeDesktopReadiness.Check(install, "../x64", ValidSignature)).Status == ClaudeDesktopReadinessStatus.UnsafePath,
            "Architecture cannot introduce path traversal");
        Require((await ClaudeDesktopReadiness.Check(install, "arm64", (_, _) => Task.CompletedTask)).Status == ClaudeDesktopReadinessStatus.MissingHostEngine,
            "Wrong-architecture engine cannot satisfy readiness");
        var wrongKind = new ClientInstallation { Kind = ClientKind.CodexDesktop, InstallationRoot = fixtureRoot };
        Require((await ClaudeDesktopReadiness.Check(wrongKind, "x64", ValidSignature)).Status == ClaudeDesktopReadinessStatus.Unsupported,
            "Another desktop client cannot masquerade as Claude");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var sawCancellation = false;
            try { await ClaudeDesktopReadiness.Check(install, "x64", ValidSignature, cancelled.Token); }
            catch (OperationCanceledException) { sawCancellation = true; }
            Require(sawCancellation, "Cancellation remains cancellation");
        }
        var linkedRoot = Path.Combine(root, "linked-install");
        CreateDirectoryLink(linkedRoot, fixtureRoot);
        var linked = new ClientInstallation { Kind = ClientKind.ClaudeDesktop, InstallationRoot = linkedRoot };
        Require((await ClaudeDesktopReadiness.Check(linked, "x64", ValidSignature)).Status == ClaudeDesktopReadinessStatus.UnsafePath &&
            ClaudeDesktopReadiness.BundledHostPath(linked, "x64") is null, "Installation-root junction cannot bypass readiness");
        var ancestor = Path.Combine(root, "ancestor-install");
        Directory.CreateDirectory(Path.Combine(ancestor, "app"));
        CreateDirectoryLink(Path.Combine(ancestor, "app", "resources"), Path.Combine(fixtureRoot, "app", "resources"));
        var throughAncestor = new ClientInstallation { Kind = ClientKind.ClaudeDesktop, InstallationRoot = ancestor };
        Require((await ClaudeDesktopReadiness.Check(throughAncestor, "x64", ValidSignature)).Status == ClaudeDesktopReadinessStatus.UnsafePath,
            "Resource-directory junction cannot bypass readiness");
        File.WriteAllText(Path.Combine(root, "claude-readiness-results.json"), JsonSerializer.Serialize(new
        {
            checks, passed = checks.Count, realVendorSignatureTested = false, installedApplicationChanged = false,
            noInstallation = true, noLaunch = true, noUserConfigChanges = true
        }, JsonData.Options), new UTF8Encoding(false));
        Console.WriteLine($"Passed {checks.Count} Claude readiness path, signature-policy and upgrade-policy checks.");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create isolated junction fixture.");
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Junction fixture failed: " + output + error);
    }
}
