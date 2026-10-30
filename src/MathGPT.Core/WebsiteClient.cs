using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MathGPT.Core;

public static class HttpSafety
{
    public static HttpClient Create(CookieContainer? cookies = null, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (cookies is null) handler.UseCookies = false;
        else handler.CookieContainer = cookies;
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MathGPT-Desktop/" + (typeof(HttpSafety).Assembly.GetName().Version?.ToString(3) ?? "1.6.0"));
        return client;
    }
    public static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw Error(response.StatusCode);
        if (response.Content.Headers.ContentLength > 8_000_000) throw new UserException("服务返回内容过大，已停止读取。");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var bounded = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (bounded.Length + count > 8_000_000) throw new UserException("服务返回内容过大，已停止读取。");
            bounded.Write(buffer, 0, count);
        }
        try
        {
            using var doc = JsonDocument.Parse(bounded.ToArray());
            var root = doc.RootElement;
            if (root.Obj("success").ValueKind == JsonValueKind.False)
                throw new UserException(SiteError(root.Str("message"), root.Str("code")));
            if (root.Obj("error").ValueKind == JsonValueKind.Object)
                throw new UserException("服务拒绝了请求。请核对密钥、模型、分组与余额。");
            return root.Clone();
        }
        catch (JsonException) { throw new UserException("服务没有返回有效 JSON。请检查 API 地址、网站入口或维护状态。"); }
    }
    private static string SiteError(string message, string code)
    {
        var value = (message + " " + code).ToLowerInvariant();
        if (value.Contains("password") || value.Contains("credentials")) return "账号或密码不正确，或账号已停用。请检查后重试。";
        if (value.Contains("verification") || value.Contains("flow")) return "验证已失效或验证码不正确。请重新登录。";
        if (value.Contains("quota") || value.Contains("balance")) return "余额或密钥额度不足。请前往钱包充值或调整密钥额度。";
        if (value.Contains("group") || value.Contains("model")) return "此分组或模型当前不可用。请刷新目录后重新选择。";
        return "网站没有接受此操作。请检查账号、权限和输入，或到网站查看详情。";
    }
    public static UserException Error(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized => "凭据已失效或密钥不可用。请重新登录或更换 API Key。",
        HttpStatusCode.Forbidden => "访问被拒绝。请检查网站入口密码、账号状态、密钥分组或网络地区限制。",
        HttpStatusCode.PaymentRequired => "余额不足。请前往 MathGPT 钱包充值。",
        HttpStatusCode.TooManyRequests => "请求过于频繁或额度已用尽。请稍后手动重试。",
        HttpStatusCode.NotFound => "接口不存在。请检查 Base URL 与客户端协议。",
        HttpStatusCode.BadRequest => "请求参数不被支持。请核对模型、协议、推理强度与分组。",
        _ when (int)status >= 500 => "服务暂时不可用或正在维护。请稍后手动重试。",
        _ => $"服务返回 HTTP {(int)status}。请检查连接设置。"
    });
}

public sealed partial class WebsiteClient : IDisposable
{
    private readonly StateStore store;
    private readonly Uri origin;
    private readonly CookieContainer cookies = new();
    private readonly HttpClient client;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string access = "";
    private long expires;
    public WebsiteAccount? Account { get; private set; }
    public decimal QuotaPerYuan { get; private set; } = 500_000;
    public bool SignedIn => !string.IsNullOrEmpty(access);
    public WebsiteClient(StateStore store, string origin = "https://mathgpt.xyz", HttpMessageHandler? fixtureHandler = null)
    {
        this.store = store;
        this.origin = UrlPolicy.Base(origin);
        var persisted = store.Vault.Get("site-session");
        if (persisted is not null)
        {
            var session = JsonSerializer.Deserialize<SavedSession>(persisted, JsonData.Options);
            if (session?.Origin == this.origin.AbsoluteUri)
            {
                access = session.Access;
                expires = session.Expires;
                foreach (var c in session.Cookies)
                    cookies.Add(this.origin, new Cookie(c.Name, c.Value, c.Path) { Secure = c.Secure, HttpOnly = true });
            }
        }
        client = fixtureHandler is null ? HttpSafety.Create(cookies) : new HttpClient(fixtureHandler);
    }
    private sealed record SavedCookie(string Name, string Value, string Path, bool Secure);
    private sealed record SavedSession(string Origin, string Access, long Expires, List<SavedCookie> Cookies);
    private void Persist()
    {
        var kept = cookies.GetAllCookies().Cast<Cookie>().Where(c => !c.Expired &&
            c.Domain.TrimStart('.').Equals(origin.Host, StringComparison.OrdinalIgnoreCase))
            .Select(c => new SavedCookie(c.Name, c.Value, c.Path, c.Secure)).ToList();
        var saved = new SavedSession(origin.AbsoluteUri, access, expires, kept);
        store.Vault.Put("site-session", JsonSerializer.Serialize(saved));
        RememberAccount(saved);
    }
    public async Task<JsonElement> Status(CancellationToken ct = default)
    {
        var json = await Send(HttpMethod.Get, "/api/status", null, false, ct);
        var data = json.Obj("data");
        var unit = data.Money("quota_per_unit");
        if (unit is > 0) QuotaPerYuan = unit.Value;
        return data;
    }
    public async Task UnlockEntry(string password, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/"));
        using var response = await client.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        var match = Regex.Match(html, "name=[\"']csrf[\"'][^>]*value=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            await Status(ct);
            return;
        }
        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/__entry"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["csrf"] = WebUtility.HtmlDecode(match.Groups[1].Value), ["entry_password"] = password
            })
        };
        post.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        using var result = await client.SendAsync(post, ct);
        if ((int)result.StatusCode != 302 && (int)result.StatusCode != 303 && !result.IsSuccessStatusCode)
            throw new UserException("网站入口密码不正确或授权已过期。请检查后重试。");
        await Status(ct);
        Persist();
    }
    public async Task<LoginChallenge?> Login(string username, string password, CancellationToken ct = default)
    {
        if (SignedIn && Account is null) await RefreshAccount(ct);
        var previous = BeginAccountLogin();
        try
        {
        var status = await Status(ct);
        if (status.Flag("turnstile_check"))
            throw new UserException("网站当前要求浏览器人机验证。请先在网站使用账号功能；桌面端仍可直接连接 API Key。");
        var encryption = (await Send(HttpMethod.Get, "/api/user/login/encryption-key", null, false, ct)).Obj("data");
        object body;
        if (encryption.Flag("enabled"))
        {
            body = new { username, password_encrypted = PasswordCipher.Encrypt(password, encryption.Str("public_key"), encryption.Str("kid")),
                encryption_key_id = encryption.Str("kid") };
        }
        else body = new { username, password };
        var data = (await Send(HttpMethod.Post, "/api/user/login", body, false, ct)).Obj("data");
        if (data.Flag("require_verification"))
            return new LoginChallenge(data.Str("flow_token"), data.Obj("methods").Items().Select(m => m.Str("method", m.Str("id"))).ToArray());
        AcceptSession(data);
        await RefreshAccount(ct);
        loginRollback = null;
        return null;
        }
        catch
        {
            RestoreAccount(previous);
            loginRollback = null;
            throw;
        }
    }
    public async Task VerifyLogin(string flowToken, string code, CancellationToken ct = default)
    {
        var data = (await Send(HttpMethod.Post, "/api/user/login/verify",
            new { flow_token = flowToken, method = "2fa", code }, false, ct)).Obj("data");
        AcceptSession(data);
        await RefreshAccount(ct);
        loginRollback = null;
    }
    private void AcceptSession(JsonElement data)
    {
        var token = data.Str("access_token");
        if (string.IsNullOrEmpty(token)) throw new UserException("登录未完成。请使用网站完成必要验证。");
        access = token;
        expires = data.Number("access_expires_at");
        Persist();
    }
    public async Task<WebsiteAccount> RefreshAccount(CancellationToken ct = default)
    {
        var user = (await Send(HttpMethod.Get, "/api/user/self", null, true, ct)).Obj("data");
        if (user.Number("id") <= 0) throw new UserException("网站未返回有效账号。请重新登录。");
        Account = new((int)user.Number("id"), user.Str("display_name", user.Str("username")),
            (user.Money("quota") ?? 0) / QuotaPerYuan, (user.Money("used_quota") ?? 0) / QuotaPerYuan);
        Persist();
        return Account;
    }
    public async Task<List<WebsiteKey>> Keys(CancellationToken ct = default)
    {
        var keys = new List<WebsiteKey>();
        for (int page = 1; page <= 100; page++)
        {
            var data = (await Send(HttpMethod.Get, $"/api/token/?p={page}&page_size=100", null, true, ct)).Obj("data");
            var rows = data.Obj("items").Items().ToList();
            foreach (var k in rows)
                keys.Add(new((int)k.Number("id"), k.Str("name"), k.Str("group", "default"), (int)k.Number("status"),
                    (k.Money("remain_quota") ?? 0) / QuotaPerYuan, k.Flag("unlimited_quota"), k.Number("expired_time")));
            if (rows.Count < 100) break;
        }
        return keys;
    }
    public async Task<string> KeySecret(int id, CancellationToken ct = default) =>
        (await Send(HttpMethod.Post, $"/api/token/{id}/key", new { }, true, ct)).Obj("data").Str("key");
    public async Task<int> CreateKey(string name, string group, decimal yuan, CancellationToken ct = default)
    {
        if (yuan <= 0 || yuan > 10000) throw new UserException("新密钥额度应在 ¥0.01 至 ¥10,000 之间。");
        var result = await Send(HttpMethod.Post, "/api/token/", new
        {
            name, group, remain_quota = (long)decimal.Floor(yuan * QuotaPerYuan), unlimited_quota = false,
            expired_time = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
            model_limits_enabled = false, cross_group_retry = false, allow_user_quota_query = true
        }, true, ct);
        return (int)result.Obj("data").Number("id");
    }
    public async Task<int> CreateQuickKey(string name, string group, CancellationToken ct = default)
    {
        var result = await Send(HttpMethod.Post, "/api/token/", new
        {
            name, group, remain_quota = 0, unlimited_quota = true, expired_time = -1,
            model_limits_enabled = false, model_limits = "", cross_group_retry = false,
            allow_user_quota_query = true
        }, true, ct);
        return (int)result.Obj("data").Number("id");
    }
    public async Task RevokeKey(int id, CancellationToken ct = default)
    {
        await Send(HttpMethod.Delete, $"/api/token/{id}", null, true, ct);
        foreach (var profile in store.State.Profiles.Where(p => p.WebsiteKeyId == id))
        {
            store.Vault.Remove(profile.Id);
            if (profile.SecretRef.Length > 0) store.Vault.Remove(profile.SecretRef);
            if (profile.PreviousSecretRef.Length > 0) store.Vault.Remove(profile.PreviousSecretRef);
            profile.LastSync = "网站密钥已删除，请更换连接。";
        }
        store.Save();
    }
    public async Task Logout(CancellationToken ct = default)
    {
        var removing = Account?.Id;
        try { await Send(HttpMethod.Post, "/api/user/auth/logout", new { }, false, ct); }
        finally
        {
            access = ""; expires = 0; Account = null;
            foreach (Cookie cookie in cookies.GetAllCookies()) cookie.Expired = true;
            store.Vault.Remove("site-session");
            if (removing is { } id) ForgetSavedAccount(id);
            loginRollback = null;
        }
    }
    public async Task<JsonElement> Pricing(CancellationToken ct = default) =>
        await Send(HttpMethod.Get, "/api/pricing", null, SignedIn, ct);
    private async Task<JsonElement> Send(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        if (authenticated && string.IsNullOrEmpty(access)) throw new UserException("请先登录 MathGPT 网站账号。");
        if (authenticated && expires > 0 && expires < DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds())
            await RefreshSession(ct);
        using var request = new HttpRequestMessage(method, new Uri(origin, path));
        request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated)
        {
            access = ""; expires = 0; Account = null; Persist();
        }
        var result = await HttpSafety.Json(response, ct);
        Persist();
        return result;
    }
    public async Task RefreshSession(CancellationToken ct = default)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            if (expires > DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()) return;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/api/user/auth/refresh"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
            using var response = await client.SendAsync(request, ct);
            var json = await HttpSafety.Json(response, ct);
            AcceptSession(json.Obj("data"));
        }
        finally { refreshLock.Release(); }
    }
    public void Dispose() { client.Dispose(); refreshLock.Dispose(); }
}
