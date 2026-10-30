using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class DesktopMirrorAudit
{
    public static async Task<int> Run(string root)
    {
        Directory.CreateDirectory(root);
        var results = new List<object>();
        void Check(string name, bool valid)
        {
            if (!valid) throw new InvalidOperationException(name);
            results.Add(new { name, passed = true });
        }
        var payload = Encoding.UTF8.GetBytes("official desktop offline fixture");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        JsonObject Entry(string client = "codex", string platform = "windows", string arch = "x64") => new()
        {
            ["client"] = client, ["platform"] = platform, ["architecture"] = arch, ["version"] = "26.10.29",
            ["file_name"] = "desktop-" + client + "-" + platform + "-" + arch + (platform == "windows" ? ".msix" : ".dmg"),
            ["url"] = DesktopClientDownloads.AssetRoot + "desktop-" + client + "-" + platform + "-" + arch + (platform == "windows" ? ".msix" : ".dmg"),
            ["official_url"] = client == "codex" ? "https://persistent.oaistatic.com/codex-app-prod/ChatGPT-" + arch + (platform == "windows" ? ".msix" : ".dmg") :
                "https://downloads.claude.ai/releases-offline/" + (platform == "windows" ? "win32/" : "darwin/") + arch + "/4.29.0/Claude-offline." + (platform == "windows" ? "msix" : "dmg"),
            ["bytes"] = payload.Length, ["sha256"] = digest, ["package_kind"] = platform == "windows" ? "msix" : "dmg", ["offline"] = true
        };
        string Manifest(params JsonObject[] entries) => new JsonObject { ["schema"] = 1, ["assets"] = new JsonArray(entries.Select(x => (JsonNode)x).ToArray()) }.ToJsonString();
        var release = DesktopClientDownloads.Parse(Manifest(Entry()), ClientKind.CodexDesktop, "windows", "x64");
        Check("desktop manifest resolves SHA256 and exact target", release.Kind == ClientKind.CodexDesktop && release.Digest == digest && release.ExpectedBytes == payload.Length);
        var large = Entry(); large["bytes"] = 2_292_493_683L;
        Check("offline package larger than 2GB accepted", DesktopClientDownloads.Parse(Manifest(large),
            ClientKind.CodexDesktop, "windows", "x64").ExpectedBytes == 2_292_493_683L);
        foreach (var client in new[] { "codex", "claude" })
        foreach (var platform in new[] { "windows", "mac" })
        foreach (var arch in new[] { "x64", "arm64" })
        {
            var target = DesktopClientDownloads.Parse(Manifest(Entry(client, platform, arch)),
                client == "codex" ? ClientKind.CodexDesktop : ClientKind.ClaudeDesktop, platform, arch);
            Check("target " + client + "/" + platform + "/" + arch, target.Architecture == arch && target.Platform == platform && target.Offline);
        }
        void Rejected(string name, Action<JsonObject> mutate)
        {
            var entry = Entry(); mutate(entry); var rejected = false;
            try { _ = DesktopClientDownloads.Parse(Manifest(entry), ClientKind.CodexDesktop, "windows", "x64"); }
            catch (UserException) { rejected = true; }
            Check(name, rejected);
        }
        Rejected("arbitrary mirror host rejected", e => e["url"] = "https://evil.example/desktop.msix");
        Rejected("mirror query rejected", e => e["url"] = e["url"]!.GetValue<string>() + "?url=https://evil.example");
        Rejected("encoded mirror path rejected", e => e["url"] = DesktopClientDownloads.AssetRoot + "%2e%2e/desktop.msix");
        Rejected("mirror traversal filename rejected", e => e["file_name"] = "../desktop.msix");
        Rejected("non HTTPS origin rejected", e => e["official_url"] = "http://persistent.oaistatic.com/codex-app-prod/a.msix");
        Rejected("arbitrary official source rejected", e => e["official_url"] = "https://github.com/openai/codex/cli.zip");
        Rejected("CLI client rejected", e => e["client"] = "codex-cli");
        Rejected("wrong platform package type rejected", e => e["package_kind"] = "zip");
        Rejected("non offline manifest rejected", e => e["offline"] = false);
        Rejected("standard Claude shell cannot be labeled offline", e =>
        {
            e["client"] = "claude"; e["official_url"] = "https://downloads.claude.ai/releases/win32/x64/4.29.0/Claude.msix";
        });
        Rejected("unbounded package rejected", e => e["bytes"] = 8_000_000_001L);
        Rejected("zero digest rejected", e => e["sha256"] = new string('0', 64));
        Rejected("missing digest rejected", e => e.Remove("sha256"));
        var duplicateRejected = false;
        try { _ = DesktopClientDownloads.Parse(Manifest(Entry(), Entry()), ClientKind.CodexDesktop, "windows", "x64"); }
        catch (UserException) { duplicateRejected = true; }
        Check("duplicate target rejected", duplicateRejected);
        var propertyRejected = false;
        try { _ = DesktopClientDownloads.Parse("{\"schema\":1,\"schema\":1,\"assets\":[]}", ClientKind.CodexDesktop, "windows", "x64"); }
        catch (UserException) { propertyRejected = true; }
        Check("duplicate JSON property rejected", propertyRejected);
        async Task Scenario(string name, Func<HttpRequestMessage, int, HttpResponseMessage> response,
            bool accepted, Action<string>? seed = null, Action<string, int>? inspect = null, PackageRelease? target = null)
        {
            target ??= release;
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); seed?.Invoke(directory);
            var calls = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                Check(name + " sends no authorization header", request.Headers.Authorization is null);
                return response(request, ++calls);
            }));
            var passed = true;
            try
            {
                var file = await DesktopClientDownloads.FetchContent(target, directory, null, http: http);
                passed = await File.ReadAllBytesAsync(file).ContinueWith(t => t.Result.SequenceEqual(payload));
            }
            catch (UserException) { passed = false; }
            Check(name, passed == accepted); inspect?.Invoke(directory, calls);
        }
        HttpResponseMessage Body(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK, long? start = null)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
            if (start is not null) response.Content.Headers.ContentRange = new(start.Value, payload.Length - 1, payload.Length);
            return response;
        }
        await Scenario("mirror byte and SHA256 success", (_, _) => Body(payload), true,
            inspect: (_, calls) => Check("successful mirror has one request", calls == 1));
        await Scenario("mirror connection failure uses same official package", (request, call) =>
        {
            if (call == 1) throw new HttpRequestException("network fixture");
            Check("fallback fixed upstream URL", request.RequestUri!.AbsoluteUri == release.OfficialUrl); return Body(payload);
        }, true, inspect: (_, calls) => Check("one official fallback", calls == 2));
        await Scenario("redirect never changes mirror origin", (_, _) =>
            new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.example/a.msix") } }, false,
            inspect: (_, calls) => Check("redirect has no fallback", calls == 1));
        await Scenario("wrong bytes do not fall back", (_, _) => Body(new byte[payload.Length]), false,
            inspect: (dir, calls) => Check("bad hash keeps blocked partial without fallback",
                calls == 1 && File.Exists(DesktopClientDownloads.PartialPath(release, dir))));
        await Scenario("incorrect declared size does not fall back", (_, _) => Body([1]), false,
            inspect: (_, calls) => Check("size mismatch has no fallback", calls == 1));
        await Scenario("HTTP forbidden does not fall back", (_, _) => new(HttpStatusCode.Forbidden), false,
            inspect: (_, calls) => Check("forbidden has no fallback", calls == 1));
        await Scenario("HTTP unavailable permits same digest fallback", (_, call) => call == 1 ? new(HttpStatusCode.ServiceUnavailable) : Body(payload), true);
        await Scenario("strict Range resume", (request, _) =>
        {
            Check("Range starts at existing partial length", request.Headers.Range!.Ranges.Single().From == 6);
            return Body(payload[6..], HttpStatusCode.PartialContent, 6);
        }, true, dir => File.WriteAllBytes(DesktopClientDownloads.PartialPath(release, dir), payload[..6]));
        await Scenario("Range ignored preserves partial", (_, _) => Body(payload), false,
            dir => File.WriteAllBytes(DesktopClientDownloads.PartialPath(release, dir), payload[..6]),
            (dir, calls) => Check("ignored Range neither truncates nor falls back", calls == 1 &&
                File.ReadAllBytes(DesktopClientDownloads.PartialPath(release, dir)).SequenceEqual(payload[..6])));
        await Scenario("wrong Content-Range rejected", (_, _) => Body(payload[6..], HttpStatusCode.PartialContent, 5), false,
            dir => File.WriteAllBytes(DesktopClientDownloads.PartialPath(release, dir), payload[..6]),
            inspect: (_, calls) => Check("wrong range has no fallback", calls == 1));
        await Scenario("completed partial only verified and renamed", (_, _) => throw new InvalidOperationException("Unexpected HTTP request"),
            true, dir => File.WriteAllBytes(DesktopClientDownloads.PartialPath(release, dir), payload),
            (_, calls) => Check("completed partial makes zero requests", calls == 0));
        await Scenario("corrupt cache is retained", (_, _) => throw new InvalidOperationException("Unexpected HTTP request"), false,
            dir => File.WriteAllBytes(Path.Combine(dir, release.FileName), [7]),
            (dir, calls) => Check("corrupt cached package remains intact", calls == 0 && File.ReadAllBytes(Path.Combine(dir, release.FileName)).SequenceEqual(new byte[] { 7 })));
        await Scenario("fallback redirect constrained to vendor", (_, call) =>
        {
            if (call == 1) throw new HttpRequestException();
            return new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.example/a.msix") } };
        }, false, inspect: (_, calls) => Check("untrusted official redirect never requested", calls == 2));
        await Scenario("network stream interruption resumes from verified length", (request, call) =>
        {
            if (call == 1)
            {
                var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(payload, null)) };
                first.Content.Headers.ContentLength = payload.Length; return first;
            }
            Check("stream fallback continues partial with Range", request.Headers.Range!.Ranges.Single().From == 6);
            return Body(payload[6..], HttpStatusCode.PartialContent, 6);
        }, true);
        var cancelledDirectory = Path.Combine(root, "cancelled"); Directory.CreateDirectory(cancelledDirectory);
        using (var cancel = new CancellationTokenSource())
        {
            var calls = 0;
            using var http = new HttpClient(new Handler(_ =>
            {
                calls++;
                var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(payload, cancel.Cancel)) };
                first.Content.Headers.ContentLength = payload.Length; return first;
            }));
            var cancelled = false;
            try { await DesktopClientDownloads.FetchContent(release, cancelledDirectory, null, cancel.Token, http); }
            catch (OperationCanceledException) { cancelled = true; }
            Check("user cancellation never falls back and retains partial", cancelled && calls == 1 &&
                File.ReadAllBytes(DesktopClientDownloads.PartialPath(release, cancelledDirectory)).SequenceEqual(payload[..6]));
        }
        Check("Codex Mac anchor pins Apple team and bundle", DesktopClientDownloads.MacSigningRequirement(ClientKind.CodexDesktop)
            .Contains("com.openai.codex", StringComparison.Ordinal) && DesktopClientDownloads.MacSigningRequirement(ClientKind.CodexDesktop)
            .Contains("2DC432GLL2", StringComparison.Ordinal));
        Check("Claude Mac anchor pins Apple team and bundle", DesktopClientDownloads.MacSigningRequirement(ClientKind.ClaudeDesktop)
            .Contains("com.anthropic.claudefordesktop", StringComparison.Ordinal) && DesktopClientDownloads.MacSigningRequirement(ClientKind.ClaudeDesktop)
            .Contains("Q6L2SF6YDW", StringComparison.Ordinal));
        var claudePublisher = "CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", L=San Francisco, S=California, C=US, SERIALNUMBER=4860621";
        Check("Claude exact vendor identity accepted", DesktopPackages.IsVendorIdentity(ClientKind.ClaudeDesktop, "Claude", claudePublisher));
        Check("Claude vendor signer accepted", DesktopPackages.IsVendorSigner(ClientKind.ClaudeDesktop, claudePublisher, true));
        Check("lookalike Anthropic signer rejected", !DesktopPackages.IsVendorSigner(ClientKind.ClaudeDesktop, claudePublisher.Replace("Anthropic, PBC", "Anthropic Evil"), true));
        Check("wrong app identity rejected", !DesktopPackages.IsVendorIdentity(ClientKind.ClaudeDesktop, "ClaudeCode", claudePublisher));
        Check("Codex store identity accepted", DesktopPackages.IsVendorIdentity(ClientKind.CodexDesktop, "OpenAI.Codex", "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B"));
        Check("Codex store signer accepted", DesktopPackages.IsVendorSigner(ClientKind.CodexDesktop, "CN=Microsoft Corporation, O=Microsoft Corporation, C=US", true));
        Check("Codex publisher GUID signer accepted only for MSIX", DesktopPackages.IsVendorSigner(ClientKind.CodexDesktop,
            "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", true) && !DesktopPackages.IsVendorSigner(ClientKind.CodexDesktop,
            "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", false));
        Check("Microsoft lookalike signer rejected", !DesktopPackages.IsVendorSigner(ClientKind.CodexDesktop, "CN=Microsoft Evil, O=Microsoft Evil, C=US", true));
        Check("Microsoft signer cannot sign arbitrary Claude", !DesktopPackages.IsVendorSigner(ClientKind.ClaudeDesktop, "CN=Microsoft Corporation, O=Microsoft Corporation, C=US", true));
        var unsigned = Path.Combine(root, "unsigned-official-identity.msix");
        using (var archive = ZipFile.Open(unsigned, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open(), new UTF8Encoding(false));
            writer.Write("<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"OpenAI.Codex\" Publisher=\"CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B\" ProcessorArchitecture=\"x64\" Version=\"1.0.0.0\" /></Package>");
        }
        var unsignedRelease = release with { Digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(unsigned))), ExpectedBytes = new FileInfo(unsigned).Length };
        Check("matching hash and vendor identity still require real Authenticode", !await BitsDownload.Verify(unsigned, unsignedRelease));
        var artifact = Path.Combine(root, "desktop-mirror-results.json");
        SafeFiles.AtomicWrite(artifact, JsonSerializer.Serialize(new { results, noInstallation = true, noLaunch = true, noLiveNetwork = true }, JsonData.Options));
        Console.WriteLine($"Passed {results.Count} desktop manifest, download, Range, fallback and vendor identity checks."); return 0;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
    private sealed class InterruptedStream(byte[] bytes, Action? cancel) : Stream
    {
        private bool first = true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (first) { first = false; bytes.AsSpan(0, 6).CopyTo(buffer.Span); return ValueTask.FromResult(6); }
            if (cancel is not null) { cancel(); throw new OperationCanceledException(ct); }
            throw new IOException("network fixture");
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
