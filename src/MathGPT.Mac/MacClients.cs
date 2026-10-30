using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MathGPT.Core;

namespace MathGPT.Mac;

internal sealed class MacClients(StateStore store)
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string? persistentHelper;
    private string Helper => persistentHelper ?? Environment.ProcessPath ?? throw new UserException("MathGPT 程序入口不可用。");
    internal string SelfBundle => Directory.GetParent(Helper)?.Parent?.Parent?.FullName ?? throw new UserException("请从完整的 MathGPT.app 打开。");
    internal static string[] Names(ClientKind kind) => kind switch { ClientKind.CodexDesktop => ["Codex", "ChatGPT"], ClientKind.ClaudeDesktop => ["Claude"], _ => ["DeepSeek Harness"] };
    internal static async Task<string> Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new UserException("系统操作未启动。");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var result = await output; var error = await errors;
        if (process.ExitCode != 0) throw new UserException("操作未完成：" + Privacy.Clean(error.Trim()));
        return result;
    }
    internal static string DownloadUrl(ClientKind kind) => kind switch
    {
        ClientKind.CodexDesktop => "https://persistent.oaistatic.com/codex-app-prod/Codex.dmg",
        ClientKind.ClaudeDesktop => "https://claude.ai/api/desktop/darwin/universal/dmg/latest/redirect",
        ClientKind.Harness when RuntimeInformation.OSArchitecture == Architecture.Arm64 => "https://download.deepseek.com/desktop/dsh-latest-macos-arm64.dmg",
        ClientKind.Harness => "https://download.deepseek.com/desktop/dsh-latest-macos-x64.dmg",
        _ => throw new UserException("不是可安装的桌面客户端。")
    };
    internal static async Task Copy(string value)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/pbcopy") { UseShellExecute = false, RedirectStandardInput = true }) ?? throw new UserException("剪贴板不可用。");
        await process.StandardInput.WriteAsync(value); process.StandardInput.Close(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new UserException("复制未完成。");
    }
    internal async Task<ClientInstallation?> Detect(ClientKind kind)
    {
        foreach (var folder in new[] { Path.Combine(Home, "Applications"), "/Applications" })
        foreach (var name in Names(kind))
        {
            var app = Path.Combine(folder, name + ".app"); var plist = Path.Combine(app, "Contents", "Info.plist");
            if (!File.Exists(plist)) continue;
            var entry = (await Run("/usr/libexec/PlistBuddy", "-c", "Print :CFBundleExecutable", plist)).Trim();
            if (entry.Length == 0 || Path.GetFileName(entry) != entry) throw new UserException("应用入口格式异常，已保留现有应用。");
            var executable = SafeFiles.Child(app, "Contents/MacOS/" + entry);
            if (!File.Exists(executable)) continue;
            var found = new ClientInstallation { Kind = kind, Executable = executable, InstallationRoot = app };
            var old = store.State.Clients.FirstOrDefault(x => x.Kind == kind);
            if (old is not null && old.Executable == executable) return old;
            store.State.Clients.RemoveAll(x => x.Kind == kind); store.State.Clients.Add(found); store.Save(); return found;
        }
        return null;
    }
    internal async Task<ClientInstallation> Install(ClientKind kind, Action<string> progress, CancellationToken ct = default)
    {
        var existing = await Detect(kind); if (existing is not null) return existing;
        ct.ThrowIfCancellationRequested();
        var release = DesktopConfiguration.IsDesktop(kind)
            ? await DesktopClientDownloads.Resolve(kind, "mac", PackageService.Architecture, ct) : null;
        var url = release?.Url ?? DownloadUrl(kind);
        if (release is null) UrlPolicy.OfficialDownload(url);
        var directory = SafeFiles.DirectoryPath(store.State.DownloadDirectory); Directory.CreateDirectory(directory);
        var isZip = release?.PackageKind == "zip" || new Uri(url).AbsolutePath.EndsWith(".zip", StringComparison.Ordinal);
        string package;
        if (release is not null)
        {
            var transfer = new Progress<TransferProgress>(p => progress(p.Message + " · " +
                p.Received / 1048576 + " / " + p.Total / 1048576 + " MB"));
            package = await DesktopClientDownloads.FetchContent(release, directory, transfer, ct);
        }
        else
        {
            package = SafeFiles.Child(directory, kind + (isZip ? "-official.zip" : "-official.dmg")); var partial = package + ".partial";
            progress("下载官方桌面安装包…");
            using var http = HttpSafety.Create(timeout: TimeSpan.FromMinutes(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            for (var i = 0; (int)response.StatusCode is >= 300 and < 400 && i < 4; i++)
            {
                var target = response.Headers.Location ?? throw new UserException("官方下载地址没有返回目标。");
                var next = target.IsAbsoluteUri ? target : new Uri(response.RequestMessage!.RequestUri!, target); UrlPolicy.OfficialDownload(next.AbsoluteUri);
                response.Dispose(); response = await http.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            using (response)
            {
                response.EnsureSuccessStatusCode(); if (response.Content.Headers.ContentLength > 2_000_000_000) throw new UserException("安装包大小异常。");
                await using var input = await response.Content.ReadAsStreamAsync(ct); await using var output = File.Create(partial);
                var buffer = new byte[131072]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0) { total += count; if (total > 2_000_000_000) throw new UserException("安装包超过大小限制。"); await output.WriteAsync(buffer.AsMemory(0, count), ct); progress("下载官方桌面安装包 · " + total / 1048576 + " MB"); }
            }
            File.Move(partial, package, true);
        }
        ct.ThrowIfCancellationRequested();
        using var packageLock = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (release is not null && !await DesktopClientDownloads.VerifyContent(package, release, ct))
            throw new UserException("安装包 SHA256 校验失败，已阻止安装。");
        progress("验证原厂签名与应用身份…");
        string[] mounts;
        string? extraction = null;
        if (isZip)
        {
            extraction = SafeFiles.Child(directory, ".mathgpt-extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extraction); File.SetUnixFileMode(extraction,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            using (var archive = ZipFile.OpenRead(package)) MacPackageSafety.Validate(archive);
            await Run("/usr/bin/ditto","-x","-k",package,extraction); mounts=[extraction];
        }
        else
        {
            var attach = await Run("/usr/bin/hdiutil", "attach", "-readonly", "-nobrowse", "-plist", package);
            mounts = XDocument.Parse(attach).Descendants("key").Where(x => x.Value == "mount-point").Select(x => x.ElementsAfterSelf().First().Value).ToArray();
        }
        try
        {
            var apps = mounts.SelectMany(m => Directory.GetDirectories(m, "*.app", SearchOption.TopDirectoryOnly)).Where(p => Names(kind).Contains(Path.GetFileNameWithoutExtension(p))).ToArray();
            if (apps.Length != 1) throw new UserException("官方安装包内的应用与所选客户端不符。");
            if (release is not null) await VerifyVendorApp(apps[0], kind, release.Architecture);
            else await Run("/usr/bin/codesign", "--verify", "--deep", "--strict", apps[0]);
            await Run("/usr/sbin/spctl", "--assess", "--type", "execute", apps[0]);
            var destination = Path.Combine(Home, "Applications", Path.GetFileName(apps[0])); SafeFiles.NoLinks(destination);
            if (Directory.Exists(destination)) throw new UserException("目标应用已存在，请点击检测，不会覆盖原安装。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var staged = Path.Combine(Path.GetDirectoryName(destination)!, ".mathgpt-install-" + Guid.NewGuid().ToString("N") + ".app");
            SafeFiles.NoLinks(staged);
            try
            {
                await Run("/usr/bin/ditto", apps[0], staged);
                if (release is not null) await VerifyVendorApp(staged, kind, release.Architecture);
                else await Run("/usr/bin/codesign", "--verify", "--deep", "--strict", staged);
                ct.ThrowIfCancellationRequested();
                Directory.Move(staged, destination);
            }
            finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
        }
        finally
        {
            if (extraction is null) foreach (var mount in mounts) await Run("/usr/bin/hdiutil", "detach", mount);
            else if (Path.GetFullPath(extraction).StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,StringComparison.Ordinal) && new DirectoryInfo(extraction).LinkTarget is null) Directory.Delete(extraction,true);
        }
        return await Detect(kind) ?? throw new UserException("安装完成后未找到客户端入口。");
    }
    private static async Task VerifyVendorApp(string app, ClientKind kind, string architecture)
    {
        await Run("/usr/bin/codesign", "--verify", "--deep", "--strict", "-R", DesktopClientDownloads.MacSigningRequirement(kind), app);
        var plist = Path.Combine(app, "Contents", "Info.plist");
        var bundle = (await Run("/usr/libexec/PlistBuddy", "-c", "Print :CFBundleIdentifier", plist)).Trim();
        if (bundle != DesktopClientDownloads.MacBundleIdentifier(kind)) throw new UserException("安装包的应用身份与所选客户端不符。");
        var entry = (await Run("/usr/libexec/PlistBuddy", "-c", "Print :CFBundleExecutable", plist)).Trim();
        if (entry.Length == 0 || Path.GetFileName(entry) != entry) throw new UserException("安装包的应用入口格式异常。");
        var architectures = (await Run("/usr/bin/lipo", "-archs", SafeFiles.Child(app, "Contents/MacOS/" + entry)))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!architectures.Contains(architecture == "arm64" ? "arm64" : "x86_64", StringComparer.Ordinal))
            throw new UserException("安装包没有当前 Mac 架构的原生程序，已阻止安装。");
    }
    internal async Task InstallSelf()
    {
        var executable=Environment.ProcessPath ?? throw new UserException("MathGPT 程序入口不可用。");
        var source = Directory.GetParent(executable)?.Parent?.Parent?.FullName ?? throw new UserException("请从完整的 MathGPT.app 打开。");
        if (Path.GetFileName(source)!="MathGPT.app" || !File.Exists(Path.Combine(source,"Contents","Info.plist"))) throw new UserException("请先解压完整应用再打开。");
        var target=source.StartsWith("/Applications/",StringComparison.Ordinal) || source.StartsWith(Path.Combine(Home,"Applications")+Path.DirectorySeparatorChar,StringComparison.Ordinal) ? source : Path.Combine(Home,"Applications","MathGPT.app");
        if (source!=target)
        {
            SafeFiles.NoLinks(target);
            if (Directory.Exists(target))
            {
                var destination=Path.Combine(target,"Contents","MacOS");
                var origin=Path.GetDirectoryName(executable)!;
                foreach (var file in new[]{"MathGPT","MathGPT.dll","MathGPT.Core.dll","MathGPT.Bridge"})
                    if (!File.Exists(Path.Combine(destination,file)) || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(destination,file))).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(origin,file))))) throw new UserException("应用程序目录已存在其他版本的 MathGPT。请先完成版本更换，已有应用和配置不会被覆盖。");
            }
            else {Directory.CreateDirectory(Path.GetDirectoryName(target)!);await Run("/usr/bin/ditto",source,target);}
        }
        persistentHelper=Path.Combine(target,"Contents","MacOS","MathGPT");
        var shortcut=Path.Combine(Home,"Desktop","MathGPT.app");
        if (!File.Exists(shortcut) && !Directory.Exists(shortcut)) Directory.CreateSymbolicLink(shortcut,target);
    }
    internal async Task Undo(ClientKind kind)
    {
        var install=await Detect(kind)??throw new UserException("未找到客户端。");
        var running = new ClientService(store).Running(install);
        new DesktopConfiguration(store).Rollback(install,running);
    }
    internal async Task Configure(ClientInstallation install, ConnectionProfile profile, IReadOnlyList<ModelRoute> routes)
    {
        await InstallSelf();
        var route = routes.FirstOrDefault(x => x.Id == profile.Model && x.Available) ?? throw new UserException("请选择可用模型。");
        new ClientService(store).Configure(install, profile, route, routes, Helper); await Shortcut(install, profile.Id);
    }
    internal async Task Launch(ClientKind kind, ConnectionProfile profile)
    {
        var install = await Detect(kind) ?? throw new UserException("请先下载客户端，或点击检测已有安装。");
        if (install.ProfileId != profile.Id) throw new UserException("请先点击配置连接。");
        // The shared service supplies both isolated backend and desktop data
        // paths, and only checks the selected MathGPT instance's launch lease.
        new ClientService(store).Launch(install, profile);
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private async Task Shortcut(ClientInstallation install, string id)
    {
        var kind=install.Kind;
        var name = Names(kind)[0]; var app = SafeFiles.Child(store.Root, "Launchers/" + name + ".app"); var macos = Path.Combine(app, "Contents", "MacOS"); Directory.CreateDirectory(macos);
        var script = Path.Combine(macos, "launch");
        SafeFiles.AtomicWrite(script, "#!/bin/sh\nexec " + Quote(Helper) + " --launch " + kind + " --profile " + Quote(UrlPolicy.SafeId(id)) + " --data-dir " + Quote(store.Root) + "\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var icon="";
        try
        {
            var iconName=(await Run("/usr/libexec/PlistBuddy","-c","Print :CFBundleIconFile",Path.Combine(install.InstallationRoot,"Contents","Info.plist"))).Trim();
            if (iconName.Length>0 && Path.GetFileName(iconName)==iconName)
            {
                if (!iconName.EndsWith(".icns",StringComparison.Ordinal)) iconName+=".icns";
                var original=SafeFiles.Child(install.InstallationRoot,"Contents/Resources/"+iconName);
                if (File.Exists(original)) {var resources=Path.Combine(app,"Contents","Resources");Directory.CreateDirectory(resources);File.Copy(original,Path.Combine(resources,"app.icns"),true);icon="<key>CFBundleIconFile</key><string>app.icns</string>";}
            }
        }
        catch(UserException) { /* The original name remains usable even without an icon. */ }
        SafeFiles.AtomicWrite(Path.Combine(app, "Contents", "Info.plist"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleExecutable</key><string>launch</string><key>CFBundleName</key><string>" + name + "</string><key>CFBundleIdentifier</key><string>xyz.mathgpt.launch." + kind.ToString().ToLowerInvariant() + "</string><key>CFBundlePackageType</key><string>APPL</string>"+icon+"</dict></plist>");
        var link = Path.Combine(Home, "Desktop", name + ".app"); if (!Directory.Exists(link) && !File.Exists(link)) Directory.CreateSymbolicLink(link, app);
    }
}
