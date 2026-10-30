using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Mac;

internal static class MacSelfCheck
{
    internal sealed record Check(string Name, bool Passed, string Detail);
    internal sealed record Report(string Summary, string Path, bool Passed);

    internal static async Task<Report> Run(string root)
    {
        if (!OperatingSystem.IsMacOS()) throw new UserException("此检查需要在 Mac 上运行。");
        var checks = new List<Check>();
        async Task Check(string name, Func<Task<string>> action)
        {
            try { checks.Add(new(name, true, await action())); }
            catch (Exception error) { checks.Add(new(name, false, Privacy.Friendly(error))); }
        }
        await Check("系统", () => Task.FromResult($"macOS {Environment.OSVersion.Version} · {RuntimeInformation.OSArchitecture}"));
        await Check("个人数据目录", () =>
        {
            var path = SafeFiles.Child(root, ".self-check-" + Guid.NewGuid().ToString("N"));
            try { SafeFiles.AtomicWrite(path, "MathGPT local check"); if (File.ReadAllText(path) != "MathGPT local check") throw new IOException(); }
            finally { if (File.Exists(path)) File.Delete(path); }
            return Task.FromResult("可写；未改动已有配置。");
        });
        await Check("系统钥匙串", () =>
        {
            var vault = new SecretVault(root); var id = "self-check-" + Guid.NewGuid().ToString("N");
            var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try { vault.Put(id, value); if (vault.Get(id) != value) throw new UserException("钥匙串读写结果不一致。"); }
            finally { vault.Remove(id); }
            if (vault.Contains(id)) throw new UserException("临时钥匙串项目未清理。");
            return Task.FromResult("临时项目读写、删除通过；未读取 API 密钥。");
        });
        await Check("内置协议适配器", Bridge);
        await Check("官网连接", async () =>
        {
            using var http = HttpSafety.Create(timeout: TimeSpan.FromSeconds(12));
            using var response = await http.GetAsync("https://mathgpt.xyz/api/status");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!json.RootElement.GetProperty("success").GetBoolean()) throw new UserException("官网状态检查未通过。");
            return "HTTPS 状态接口正常；没有发起收费模型请求。";
        });
        await Check("系统安装工具", () =>
        {
            foreach (var command in new[] { "/usr/bin/ditto", "/usr/bin/hdiutil", "/usr/bin/codesign", "/usr/sbin/spctl", "/usr/libexec/PlistBuddy", "/usr/bin/open" })
                if (!File.Exists(command)) throw new UserException("系统安装工具缺失：" + System.IO.Path.GetFileName(command));
            return Task.FromResult("安装、签名核对及应用启动工具可用。");
        });
        var directory = SafeFiles.Child(root, "Diagnostics"); Directory.CreateDirectory(directory);
        var path = SafeFiles.Child(directory, "mac-self-check-latest.txt");
        var summary = $"本机自检：{checks.Count(x => x.Passed)}/{checks.Count} 项通过。";
        var text = new StringBuilder("MathGPT Mac 本机自检\n").Append(DateTimeOffset.Now.ToString("O")).Append('\n').Append(summary).Append("\n\n");
        foreach (var check in checks) text.Append(check.Passed ? "通过 · " : "未通过 · ").Append(check.Name).Append("：").Append(check.Detail).Append('\n');
        text.Append("\n此报告不包含 API Key。未安装或配置厂商应用，未发送收费请求。模型调用、厂商应用完整任务及签名公证需要分别验收。\n");
        SafeFiles.AtomicWrite(path, text.ToString());
        SafeFiles.AtomicWrite(SafeFiles.Child(directory, "mac-self-check-latest.json"), JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.Now, checks, paidRequests = 0, realMacExecution = true }, JsonData.Options));
        return new(summary, path, checks.All(x => x.Passed));
    }

    private static async Task<string> Bridge()
    {
        var executable = System.IO.Path.Combine(AppContext.BaseDirectory, "MathGPT.Bridge");
        SafeFiles.NoLinks(executable);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new UserException("内置适配器无法启动。");
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { port, token, key = "self-check-only-unused-key", baseUrl = "https://mathgpt.xyz/v1", revision = "self-check", models = new Dictionary<string, string> { ["self-check-no-inference"] = "responses" } }));
            process.StandardInput.Close();
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (ready is null) throw new UserException("内置适配器未返回启动状态。");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var url = "http://127.0.0.1:" + port;
            using var response = await http.GetAsync(url + "/v1/models"); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("data")[0].GetProperty("id").GetString() != "self-check-no-inference") throw new UserException("内置适配器目录检查未通过。");
            using var stopped = await http.PostAsync(url + "/shutdown", null); stopped.EnsureSuccessStatusCode();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            if (process.ExitCode != 0) throw new UserException("内置适配器退出异常。");
            return "本机启动、鉴权、目录及退出通过；未连接模型服务。";
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
