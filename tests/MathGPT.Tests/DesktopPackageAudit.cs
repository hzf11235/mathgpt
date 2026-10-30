using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class DesktopPackageAudit
{
    public static async Task<int> Run(string root)
    {
        var results = new List<object>();
        foreach (var kind in new[] { ClientKind.CodexDesktop, ClientKind.ClaudeDesktop })
        {
            var found = await DesktopPackages.Detect(kind);
            var signed = found is not null && await DesktopPackages.VerifyPackage(found.Executable, kind);
            var fake = Path.Combine(root, kind + "-unsigned.msix");
            if (File.Exists(fake)) File.Delete(fake);
            using (var archive = ZipFile.Open(fake, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("AppxManifest.xml");
                using var stream = entry.Open();
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                var identity = new XElement(ns + "Package", new XElement(ns + "Identity",
                    new XAttribute("Name", kind == ClientKind.CodexDesktop ? "OpenAI.Codex" : "Claude"),
                    new XAttribute("Publisher", kind == ClientKind.CodexDesktop ? "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B" : "CN=Anthropic")));
                identity.Save(stream);
            }
            if (await DesktopPackages.VerifyPackage(fake, kind)) throw new InvalidOperationException("An unsigned desktop package was accepted.");
            results.Add(new { kind = kind.ToString(), installed = found is not null, version = found?.Version,
                executable_signature_valid = signed, fake_package_rejected = true, package_family = found?.PackageFamily,
                package_signature_kind = found?.PackageSignatureKind,
                application_id = found?.ApplicationId, no_config_changes = true, no_inference = true });
        }
        SafeFiles.AtomicWrite(Path.Combine(root, "desktop-package-results.json"), JsonSerializer.Serialize(results, JsonData.Options));
        Console.WriteLine(JsonSerializer.Serialize(results, JsonData.Options)); return 0;
    }
}
