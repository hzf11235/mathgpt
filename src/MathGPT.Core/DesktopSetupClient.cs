using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MathGPT.Core;

// The public verification code is only a lookup. The secret device credential
// stays in this process and is sent only in HTTPS POST bodies.
public sealed class DesktopSetupSession
{
    [JsonIgnore] internal string DeviceCode { get; init; } = "";
    public string UserCode { get; init; } = "";
    public Uri VerificationUri { get; init; } = new("https://mathgpt.xyz/desktop/connect");
    public DateTimeOffset ExpiresAt { get; init; }
    public int IntervalSeconds { get; internal set; } = 3;
    public override string ToString() => "MathGPT desktop approval " + UserCode;
}

public sealed class DesktopSetupGrant
{
    public string PlanId { get; init; } = "";
    public int TokenId { get; init; }
    public string TokenName { get; init; } = "";
    [JsonIgnore] public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "https://mathgpt.xyz/v1";
    public string Client { get; init; } = "";
    public string Model { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public string Group { get; init; } = "";
    public string Platform { get; init; } = "";
    public override string ToString() => "MathGPT setup " + PlanId;
}

public sealed class DesktopSetupClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Uri origin;
    public DesktopSetupClient(HttpMessageHandler? fixtureHandler = null, string origin = "https://mathgpt.xyz")
    {
        this.origin = UrlPolicy.Base(origin);
        if (!UrlPolicy.IsMathGpt(origin) && !this.origin.IsLoopback)
            throw new UserException("接入授权只能连接 MathGPT 官网。");
        http = fixtureHandler is null ? HttpSafety.Create() : new HttpClient(fixtureHandler);
    }
    public async Task<DesktopSetupSession> Start(CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri(origin, "/api/client-setup/device/start"),
            new { device_name = OperatingSystem.IsMacOS() ? "MathGPT · Mac" : "MathGPT · Windows", platform = OperatingSystem.IsMacOS() ? "mac" : "windows" }, ct);
        var json = await Read(response, ct);
        if (response.StatusCode != HttpStatusCode.OK || !json.Flag("success")) throw HttpSafety.Error(response.StatusCode);
        var data = json.Obj("data");
        var secret = data.Str("device_code");
        var code = data.Str("user_code");
        if (!Regex.IsMatch(secret, "^[A-Za-z0-9_-]{43}$") || !Regex.IsMatch(code, "^[A-Z0-9-]{8,12}$") ||
            !Uri.TryCreate(data.Str("verification_uri"), UriKind.Absolute, out var verification) ||
            verification.Scheme != "https" || !verification.Host.Equals("mathgpt.xyz", StringComparison.OrdinalIgnoreCase) ||
            !verification.IsDefaultPort || verification.AbsolutePath != "/desktop/connect" ||
            verification.UserInfo.Length != 0 || verification.Fragment.Length != 0 ||
            verification.Query != "?code=" + Uri.EscapeDataString(code))
            throw new UserException("网站返回的接入授权信息无效，请稍后重新连接。");
        var expiry = data.Number("expires_in");
        var interval = data.Number("interval");
        if (expiry is < 60 or > 900 || interval is < 3 or > 30)
            throw new UserException("网站返回的接入授权有效期无效。");
        return new DesktopSetupSession { DeviceCode = secret, UserCode = code, VerificationUri = verification,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiry), IntervalSeconds = (int)interval };
    }
    public async Task<DesktopSetupGrant?> Poll(DesktopSetupSession session, CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow >= session.ExpiresAt) throw new UserException("接入确认已过期，请重新点击继续网页中的配置。");
        using var response = await http.PostAsJsonAsync(new Uri(origin, "/api/client-setup/device/poll"), new { device_code = session.DeviceCode }, ct);
        var json = await Read(response, ct);
        var error = json.Str("error");
        if (response.StatusCode == HttpStatusCode.Accepted && error == "authorization_pending") return null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests && error == "slow_down")
        {
            session.IntervalSeconds = Math.Min(30, session.IntervalSeconds + 5);
            return null;
        }
        if (error is "expired_token" or "access_denied" || response.StatusCode == HttpStatusCode.Gone)
            throw new UserException("接入确认已过期、已使用或网站配置已变化，请重新发起连接。");
        if (response.StatusCode != HttpStatusCode.OK || !json.Flag("success")) throw HttpSafety.Error(response.StatusCode);
        var data = json.Obj("data");
        var key = data.Str("api_key");
        var baseUrl = data.Str("base_url");
        var plan = data.Str("plan_id");
        var client = data.Str("client");
        var model = data.Str("model");
        var group = data.Str("group");
        if (!Regex.IsMatch(plan, "^[a-f0-9]{32}$") || data.Number("token_id") is <= 0 or > int.MaxValue ||
            client is not ("harness" or "codex" or "claude") || data.Str("platform") is not ("windows" or "mac") ||
            baseUrl != "https://mathgpt.xyz/v1" || !key.StartsWith("sk-", StringComparison.Ordinal) ||
            key.Length is < 8 or > 8192 || key.Any(char.IsControl) || group.Length is < 1 or > 128 || group.Any(char.IsControl) ||
            model.Length > 192 || (model.Length > 0 && !Regex.IsMatch(model, "^[A-Za-z0-9][A-Za-z0-9._:/-]*$")) ||
            data.Str("reasoning").Length > 32 || data.Str("token_name").Length > 128)
            throw new UserException("接入配置未通过校验，请回网站重新保存配置。");
        return new DesktopSetupGrant { PlanId = plan, TokenId = (int)data.Number("token_id"), TokenName = data.Str("token_name"),
            ApiKey = key, BaseUrl = baseUrl, Client = client, Model = model, Reasoning = data.Str("reasoning"), Group = group, Platform = data.Str("platform") };
    }
    public async Task<DesktopSetupGrant> Wait(DesktopSetupSession session, CancellationToken ct = default)
    {
        while (DateTimeOffset.UtcNow < session.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(session.IntervalSeconds), ct);
            if (await Poll(session, ct) is { } grant) return grant;
        }
        throw new UserException("接入确认已过期，请重新发起连接。");
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 65536) throw new UserException("授权服务返回内容过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + count > 65536) throw new UserException("授权服务返回内容过大。");
            buffer.Write(block, 0, count);
        }
        try { using var json = JsonDocument.Parse(buffer.ToArray()); return json.RootElement.Clone(); }
        catch (JsonException) { throw new UserException("授权服务暂时不可用，请稍后重新连接。"); }
    }
    public void Dispose() => http.Dispose();
}
