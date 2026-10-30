using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MathGPT.Core;

// A private, bundled process. Neither a public proxy nor a second user-installed app.
public sealed class BuiltinGateway(StateStore store)
{
    private sealed class Configuration
    {
        public int Port { get; set; }
        public string Token { get; set; } = "";
        public string Key { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Revision { get; set; } = "";
        public Dictionary<string, string> Models { get; set; } = [];
        public Dictionary<string, string> DefaultEfforts { get; set; } = [];
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Reference(ConnectionProfile profile) => "builtin-gateway-" + UrlPolicy.SafeId(profile.Id);
    public static bool Required(ClientKind kind, ConnectionProfile profile, IReadOnlyList<ModelRoute>? routes = null)
    {
        if (profile.IsMathGpt && profile.Group == "universal" ||
            kind is ClientKind.Codex or ClientKind.CodexDesktop && profile.Protocol != WireProtocol.Responses ||
            kind is ClientKind.Claude or ClientKind.ClaudeDesktop && (profile.Reasoning.Length > 0 || profile.Protocol != WireProtocol.Anthropic || !profile.Model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))) return true;
        if (routes is null) return false;
        var protocol = kind switch
        {
            ClientKind.Codex or ClientKind.CodexDesktop => WireProtocol.Responses,
            ClientKind.Claude or ClientKind.ClaudeDesktop => WireProtocol.Anthropic,
            _ => profile.Protocol
        };
        return ModelCatalogScope.Available(profile, routes).Any(r => !r.Protocols.Contains(protocol) ||
            kind is ClientKind.Claude or ClientKind.ClaudeDesktop && !r.Id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase));
    }
    public string Prepare(ConnectionProfile profile, IReadOnlyList<ModelRoute> routes)
    {
        UrlPolicy.Base(profile.BaseUrl);
        var saved = store.Vault.Get(Reference(profile));
        var config = saved is null ? new Configuration() : JsonSerializer.Deserialize<Configuration>(saved, Json) ?? new();
        if (config.Port == 0 || config.Token.Length < 32)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try { config.Port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
            config.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
        config.BaseUrl = profile.BaseUrl.TrimEnd('/');
        config.Models = ModelCatalogScope.Available(profile, routes)
            .ToDictionary(r => r.Id, r => Format(ModelCatalogScope.Protocol(r, profile.Protocol)), StringComparer.Ordinal);
        if (!config.Models.ContainsKey(profile.Model)) throw new UserException("所选模型不在当前密钥目录中。");
        config.DefaultEfforts = profile.Reasoning.Length > 0 ? new() { [profile.Model] = profile.Reasoning } : [];
        store.Vault.Put(Reference(profile), JsonSerializer.Serialize(config, Json));
        return "http://127.0.0.1:" + config.Port + "/v1";
    }
    private static string Format(WireProtocol protocol) => protocol switch
    { WireProtocol.Responses => "responses", WireProtocol.Anthropic => "messages", _ => "chat" };
    public string Address(ConnectionProfile profile)
    {
        var saved = store.Vault.Get(Reference(profile)) ?? throw new UserException("请先应用当前连接。");
        var config = JsonSerializer.Deserialize<Configuration>(saved, Json) ?? throw new UserException("本机适配器配置无效。");
        return "http://127.0.0.1:" + config.Port + "/v1";
    }
    public async Task<string> Ensure(ConnectionProfile profile, string? executable = null, CancellationToken ct = default)
    {
        var saved = store.Vault.Get(Reference(profile)) ?? throw new UserException("请先在 MathGPT 应用当前连接。");
        var config = JsonSerializer.Deserialize<Configuration>(saved, Json) ?? throw new UserException("本机适配器配置无效，请重新应用连接。");
        config.Key = store.Key(profile) ?? throw new UserException("请先保存 API Key。");
        config.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, Json))));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        var address = "http://127.0.0.1:" + config.Port;
        var lockPath = SafeFiles.Child(store.Root, "gateway-" + UrlPolicy.SafeId(profile.Id) + ".lock");
        SafeFiles.NoLinks(lockPath);
        FileStream? lease = null;
        for (var i = 0; lease is null && i < 60; i++)
        {
            try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, ct); }
        }
        if (lease is null) throw new UserException("本机适配器正在启动，请稍后重试。");
        using (lease)
        {
            var revision = await Health(client, address, ct);
            if (revision == config.Revision) return config.Token;
            if (revision is not null)
            {
                using var stopped = await client.PostAsync(address + "/shutdown", null, ct);
                if (!stopped.IsSuccessStatusCode) throw new UserException("模型任务仍在运行，请完成任务后再切换连接。");
                for (var i = 0; i < 20 && await Health(client, address, ct) is not null; i++) await Task.Delay(100, ct);
            }
            executable ??= Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "MathGPT.Bridge.exe" : "MathGPT.Bridge");
            if (!File.Exists(executable)) throw new UserException("内置适配器缺失，请重新安装完整版 MathGPT。");
            SafeFiles.NoLinks(executable);
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start) ?? throw new UserException("本机适配器无法启动。");
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config, Json));
            process.StandardInput.Close(); config.Key = "";
            var ready = await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(8), ct);
            if (ready is null || !(await Health(client, address, ct) == config.Revision))
                throw new UserException("本机适配器无法监听。端口可能被其他程序占用，请重新保存连接。");
            return config.Token;
        }
    }
    private static async Task<string?> Health(HttpClient client, string address, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(address + "/health", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return body.RootElement.Str("service") == "MathGPT.Bridge" ? body.RootElement.Str("revision") : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
