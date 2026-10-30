using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class PackageAudit
{
    public static async Task<int> Run(string root)
    {
        var records = new List<object>();
        var store = new StateStore(Path.Combine(root, "state"));
        var downloads = @"D:\Codex\Data\mathgpt-desktop\downloads";
        var installs = @"D:\Codex\Apps\mathgpt-clients\官方 客户端";
        Directory.CreateDirectory(downloads); Directory.CreateDirectory(installs);
        var packages = new PackageService(store);
        var downloader = new BitsDownload(store);
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        foreach (var kind in new[] { ClientKind.Codex, ClientKind.Claude, ClientKind.Harness })
        {
            try
            {
                var release = await packages.Resolve(kind);
                Console.WriteLine($"RESOLVED {kind} {release.Version} {release.Architecture}");
                var last = -1;
                var progress = new Progress<TransferProgress>(p =>
                {
                    var next = (int)p.Percent / 20;
                    if (last == next) return;
                    last = next; Console.WriteLine($"{kind}: {p.Message} {(int)p.Percent}%");
                });
                var cancellationObserved = false;
                if (kind == ClientKind.Codex && !File.Exists(Path.Combine(downloads, release.FileName)))
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    try { await downloader.Fetch(release, downloads, progress, cancel.Token); }
                    catch (OperationCanceledException) { cancellationObserved = true; Console.WriteLine("CANCEL observed, resuming saved BITS job"); }
                }
                var file = await downloader.Fetch(release, downloads, progress, default);
                var existing = PackageService.FindInstalled(kind);
                ClientInstallation installed;
                string behavior;
                if (kind == ClientKind.Harness && existing is not null)
                {
                    installed = new() { Kind = kind, Executable = existing, Version = FileVersionInfo.GetVersionInfo(existing).ProductVersion ?? "",
                        Managed = false, InstallationRoot = Path.GetDirectoryName(existing)! };
                    store.State.Clients.RemoveAll(x => x.Kind == kind); store.State.Clients.Add(installed); store.Save();
                    behavior = "existing vendor installation retained";
                }
                else
                {
                    var already = store.State.Clients.FirstOrDefault(x => x.Kind == kind && File.Exists(x.Executable));
                    installed = already ?? await packages.Install(release, file, installs, progress, default);
                    behavior = already is null ? "official payload installed in Chinese-space path" : "previous audit installation reused";
                }
                using var certificate = File.Exists(installed.Executable) ? TryCertificate(installed.Executable) : null;
                records.Add(new { kind = kind.ToString(), result = "passed", version = installed.Version, behavior,
                    architecture = release.Architecture, url = release.Url, digest = release.Digest, hashVerified = await BitsDownload.Verify(file, release),
                    executable = installed.Executable, signer = certificate?.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
                    cancellationObserved, isAdministrator = admin });
                Console.WriteLine($"INSTALLED {kind}: {installed.Version}");
            }
            catch (Exception ex)
            {
                records.Add(new { kind = kind.ToString(), result = "failed", error = ex.GetType().Name, message = Privacy.Friendly(ex) });
                Console.WriteLine($"FAILED {kind}: {Privacy.Friendly(ex)}");
            }
            SafeFiles.AtomicWrite(Path.Combine(root, "package-results.json"), JsonSerializer.Serialize(records, JsonData.Options));
        }
        return records.Any(r => JsonSerializer.Serialize(r).Contains("\"failed\"")) ? 1 : 0;
    }
    private static System.Security.Cryptography.X509Certificates.X509Certificate2? TryCertificate(string file)
    {
        try { return new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file)); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
