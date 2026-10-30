using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MathGPT.Core;

public static class DesktopPackages
{
    private static string PowerShell => File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
        ? @"C:\Program Files\PowerShell\7\pwsh.exe" : Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    private static async Task<string> Run(string code, Dictionary<string, string>? variables = null, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // A parent PowerShell 7 can export its Core-only modules to a Windows PowerShell
        // child. Let that child construct its own native module path (notably Security).
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + code }) start.ArgumentList.Add(arg);
        if (variables is not null) foreach (var item in variables) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start) ?? throw new UserException("无法运行 Windows 应用安装组件。");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        // Windows manages an in-flight MSIX deployment atomically. Never kill it on cancellation.
        await process.WaitForExitAsync(CancellationToken.None);
        var text = await output; var detail = await error;
        if (process.ExitCode != 0)
        {
            if (detail.Contains("0x80073D02", StringComparison.OrdinalIgnoreCase)) throw new UserException("客户端正在运行，请正常退出后重试安装。");
            if (detail.Contains("SIGNER_REJECTED", StringComparison.Ordinal)) throw new UserException("安装包签名或发布者不符，已阻止安装。");
            throw new UserException("Windows 应用操作未完成。请确认系统支持 MSIX；可从客户端的官方页面安装，再点检测。");
        }
        return text.Trim();
    }
    public static async Task<ClientInstallation?> Detect(ClientKind kind, CancellationToken ct = default)
    {
        if (!DesktopConfiguration.IsDesktop(kind)) throw new UserException("不是桌面客户端。");
        var code = " $kind=$env:MATHGPT_PACKAGE_KIND; $packages=@(Get-AppxPackage | Where-Object { $_.Status -eq 'Ok' -and $(if($kind -eq 'CodexDesktop'){ $_.Name -eq 'OpenAI.Codex' -and $_.Publisher -match 'OpenAI|50BDFD77-8903-4850-9FFE-6E8522F64D5B' }else{ $_.Name -eq 'Claude' -and $_.Publisher -match 'Anthropic' }) }); $package=$packages | Sort-Object Version -Descending | Select-Object -First 1; if($package){ $manifest=Get-AppxPackageManifest -Package $package; $entry=$manifest.Package.Applications.Application | Select-Object -First 1; $exe=Join-Path $package.InstallLocation $entry.Executable; [pscustomobject]@{ Kind=$kind; Executable=$exe; Version=$package.Version.ToString(); Architecture=$package.Architecture.ToString().ToLowerInvariant(); InstallationRoot=$package.InstallLocation; PackageFamily=$package.PackageFamilyName; ApplicationId=$entry.Id; PackageSignatureKind=$package.SignatureKind.ToString(); Managed=$false } | ConvertTo-Json -Compress }";
        var json = await Run(code, new() { ["MATHGPT_PACKAGE_KIND"] = kind.ToString() }, ct);
        if (json.Length > 0)
        {
            var found = JsonSerializer.Deserialize<ClientInstallation>(json, JsonData.Options);
            if (found is not null && File.Exists(found.Executable)) return found;
        }
        if (kind == ClientKind.ClaudeDesktop)
        {
            // Also support the vendor's existing per-user EXE installation.
            var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude", "claude.exe");
            if (File.Exists(candidate))
            {
                await Verify(candidate, kind, ct);
                return new() { Kind = kind, Executable = candidate,
                    InstallationRoot = Path.GetDirectoryName(candidate)!, Version = FileVersionInfo.GetVersionInfo(candidate).ProductVersion ?? "已安装" };
            }
        }
        return null;
    }
    public static async Task CheckDownload(PackageRelease release, CancellationToken ct)
    {
        if (DesktopClientDownloads.IsMirror(release))
        {
            // The downloader performs the real request and its sole authorized
            // fallback. A separate preflight would defeat that same strategy.
            ct.ThrowIfCancellationRequested();
            DesktopClientDownloads.ValidateRelease(release);
            return;
        }
        using var client = HttpSafety.Create(timeout: TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, release.Url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.UnavailableForLegalReasons)
            throw new UserException("官方安装源当前拒绝下载。可在更多菜单导入已取得的官方 MSIX 安装包，或打开官方安装说明；不会反复等待或更改网络设置。");
        if (!response.IsSuccessStatusCode && (int)response.StatusCode is not 301 and not 302 and not 303 and not 307 and not 308)
            throw new UserException("官方安装源暂不可用，请稍后重试或导入官方安装包。");
    }
    public static async Task Verify(string file, ClientKind kind, CancellationToken ct = default, string? expectedArchitecture = null)
    {
        var msix = Path.GetExtension(file).Equals(".msix", StringComparison.OrdinalIgnoreCase);
        if (msix)
        {
            try
            {
                using var archive = ZipFile.OpenRead(file);
                var entry = archive.GetEntry("AppxManifest.xml");
                if (entry is null || entry.Length > 1_000_000) throw new InvalidDataException();
                using var stream = entry.Open();
                using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
                var identity = XDocument.Load(reader).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
                var name = identity?.Attribute("Name")?.Value ?? ""; var publisher = identity?.Attribute("Publisher")?.Value ?? "";
                if (!IsVendorIdentity(kind, name, publisher) ||
                    expectedArchitecture is not null && identity?.Attribute("ProcessorArchitecture")?.Value != expectedArchitecture)
                    throw new InvalidDataException();
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException)
            { throw new UserException("安装包不是所选官方桌面应用，已阻止安装。"); }
        }
        var subject = await Task.Run(() => WindowsAuthenticode.TrustedSigner(file), ct);
        ct.ThrowIfCancellationRequested();
        if (!IsVendorSigner(kind, subject, msix)) throw new UserException("安装包签名或发布者不符，已阻止安装。");
    }
    public static async Task<bool> VerifyPackage(string file, ClientKind kind, CancellationToken ct = default, string? expectedArchitecture = null)
    {
        try { SafeFiles.NoLinks(file); await Verify(file, kind, ct, expectedArchitecture); return true; }
        catch (UserException) { return false; }
    }
    public static bool IsVendorIdentity(ClientKind kind, string name, string publisher)
    {
        var fields = SubjectFields(publisher);
        return kind == ClientKind.CodexDesktop
            ? name == "OpenAI.Codex" && fields.Count == 1 && fields.GetValueOrDefault("2.5.4.3") == "50BDFD77-8903-4850-9FFE-6E8522F64D5B"
            : kind == ClientKind.ClaudeDesktop && name == "Claude" && IsAnthropic(fields);
    }
    public static bool IsVendorSigner(ClientKind kind, string subject, bool msix)
    {
        var fields = SubjectFields(subject);
        var commonName = fields.GetValueOrDefault("2.5.4.3");
        var organization = fields.GetValueOrDefault("2.5.4.10");
        // Microsoft's store identity certificate for the actual Codex MSIX uses
        // the assigned publisher GUID as its sole subject attribute.
        if (msix && kind == ClientKind.CodexDesktop && fields.Count == 1 &&
            commonName == "50BDFD77-8903-4850-9FFE-6E8522F64D5B") return true;
        if (msix && kind == ClientKind.CodexDesktop && commonName == "Microsoft Corporation" &&
            organization == "Microsoft Corporation" && fields.GetValueOrDefault("2.5.4.6") == "US") return true;
        return kind == ClientKind.ClaudeDesktop ? IsAnthropic(fields) :
            kind == ClientKind.CodexDesktop && commonName == organization &&
            organization is "OpenAI OpCo, LLC" or "OpenAI, LLC" or "OpenAI, L.L.C." &&
            fields.GetValueOrDefault("2.5.4.6") == "US";
    }
    private static bool IsAnthropic(Dictionary<string, string> fields) =>
        fields.GetValueOrDefault("2.5.4.3") == "Anthropic, PBC" &&
        fields.GetValueOrDefault("2.5.4.10") == "Anthropic, PBC" &&
        fields.GetValueOrDefault("2.5.4.6") == "US" &&
        fields.GetValueOrDefault("2.5.4.5") == "4860621";
    private static Dictionary<string, string> SubjectFields(string value)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var reader = new AsnReader(new X500DistinguishedName(value).RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            while (sequence.HasData)
            {
                var set = sequence.ReadSetOf();
                while (set.HasData)
                {
                    var pair = set.ReadSequence(); var oid = pair.ReadObjectIdentifier();
                    var tag = pair.PeekTag();
                    if (tag.TagClass != TagClass.Universal) return [];
                    var text = pair.ReadCharacterString((UniversalTagNumber)tag.TagValue);
                    if (pair.HasData || !fields.TryAdd(oid, text)) return [];
                }
            }
            if (reader.HasData) return [];
            return fields;
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or ArgumentException) { return []; }
    }
    public static async Task<ClientInstallation> Install(string file, ClientKind kind, CancellationToken ct = default)
    {
        SafeFiles.NoLinks(file);
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Verify(file, kind, ct);
        if (DesktopConfiguration.Running(kind)) throw new UserException("请正常退出客户端后重试安装；不会强制关闭现有会话。");
        if (kind == ClientKind.ClaudeDesktop)
        {
            // Its signed MSIX registers the vendor's Cowork service. Windows requires UAC.
            var code = "$ErrorActionPreference='Stop'; try { Add-AppxPackage -Path '" + Path.GetFullPath(file).Replace("'", "''") + "'; exit 0 } catch { exit 1 }";
            var elevated = new ProcessStartInfo(PowerShell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", code }) elevated.ArgumentList.Add(arg);
            try
            {
                using var setup = Process.Start(elevated) ?? throw new UserException("Windows 未启动安装程序。");
                await setup.WaitForExitAsync(CancellationToken.None);
                if (setup.ExitCode != 0) throw new UserException("Claude 安装未完成，请检查 Windows 安装授权及系统版本。原有数据未清理。");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new UserException("已取消 Windows 安装授权，可稍后重试。"); }
        }
        else await Run(" Add-AppxPackage -Path $env:MATHGPT_PACKAGE_FILE; 'installed'",
            new() { ["MATHGPT_PACKAGE_FILE"] = Path.GetFullPath(file) }, ct);
        return await Detect(kind, ct) ?? throw new UserException("Windows 已完成安装操作，但未检测到官方应用。请从开始菜单检查后重试。");
    }
    public static ProcessStartInfo LaunchInfo(ClientInstallation install)
    {
        if (install.PackageFamily.Length > 0 && install.ApplicationId.Length > 0)
        {
            if (install.PackageFamily.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-') ||
                install.ApplicationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-'))
                throw new UserException("Windows 应用标识无效，请重新检测。");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.ArgumentList.Add("shell:AppsFolder\\" + install.PackageFamily + "!" + install.ApplicationId); return start;
        }
        return new(install.Executable) { UseShellExecute = true, WorkingDirectory = install.WorkDirectory };
    }
}
