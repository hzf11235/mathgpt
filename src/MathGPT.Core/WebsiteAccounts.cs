using System.Net;
using System.Text.Json;

namespace MathGPT.Core;

public sealed record SavedWebsiteAccount(int Id, string Name, bool Current)
{
    public override string ToString() => Name + (Current ? " · 当前" : "");
}

public sealed partial class WebsiteClient
{
    private sealed record AccountSlot(WebsiteAccount Account, SavedSession Session);
    private AccountSlot? loginRollback;
    private const string AccountSlotsKey = "site-accounts-v1";

    private List<AccountSlot> ReadAccountSlots() =>
        JsonSerializer.Deserialize<List<AccountSlot>>(store.Vault.Get(AccountSlotsKey) ?? "[]", JsonData.Options) ?? [];

    public IReadOnlyList<SavedWebsiteAccount> SavedAccounts => ReadAccountSlots()
        .Where(s => s.Session.Origin == origin.AbsoluteUri)
        .Select(s => new SavedWebsiteAccount(s.Account.Id, s.Account.Name, s.Account.Id == Account?.Id)).ToArray();

    private SavedSession CaptureSession() => new(origin.AbsoluteUri, access, expires,
        cookies.GetAllCookies().Cast<Cookie>().Where(c => !c.Expired && c.Domain.TrimStart('.').Equals(origin.Host, StringComparison.OrdinalIgnoreCase))
            .Select(c => new SavedCookie(c.Name, c.Value, c.Path, c.Secure)).ToList());

    private void RememberAccount(SavedSession session)
    {
        if (Account is not { } account || string.IsNullOrEmpty(session.Access)) return;
        var slots = ReadAccountSlots();
        if (!slots.Any(s => s.Session.Origin == origin.AbsoluteUri && s.Account.Id == account.Id) &&
            slots.Count(s => s.Session.Origin == origin.AbsoluteUri) >= 8)
            throw new UserException("已保存 8 个账号，请先移除一个再添加。");
        slots.RemoveAll(s => s.Session.Origin == origin.AbsoluteUri && s.Account.Id == account.Id);
        slots.Add(new(account, session));
        // Eight independently authenticated local accounts; never evict a session silently.
        store.Vault.Put(AccountSlotsKey, JsonSerializer.Serialize(slots, JsonData.Options));
    }

    private AccountSlot? BeginAccountLogin()
    {
        if (loginRollback is not null) throw new UserException("请先完成或取消当前账号验证。");
        Persist();
        var previous = Account is { } account ? new AccountSlot(account, CaptureSession()) : null;
        loginRollback = previous;
        ClearRuntime(preserveEntrance: true);
        return previous;
    }

    private void ClearRuntime(bool preserveEntrance = false)
    {
        access = ""; expires = 0; Account = null;
        foreach (Cookie cookie in cookies.GetAllCookies())
        {
            var entrance = cookie.Name.StartsWith("__Host-mathgpt-entry", StringComparison.Ordinal) ||
                cookie.Name.StartsWith("__Host-mathgpt-navigation", StringComparison.Ordinal) ||
                cookie.Name.StartsWith("__Host-mathgpt-challenge", StringComparison.Ordinal);
            if (!preserveEntrance || !entrance) cookie.Expired = true;
        }
    }

    private void RestoreAccount(AccountSlot? slot)
    {
        ClearRuntime();
        if (slot is not null)
        {
            if (slot.Session.Origin != origin.AbsoluteUri) throw new UserException("账号地址不匹配。");
            access = slot.Session.Access; expires = slot.Session.Expires; Account = slot.Account;
            foreach (var cookie in slot.Session.Cookies)
                cookies.Add(origin, new Cookie(cookie.Name, cookie.Value, cookie.Path) { Secure = cookie.Secure, HttpOnly = true });
        }
        Persist();
    }

    public void CancelAccountLogin()
    {
        if (loginRollback is { } previous) RestoreAccount(previous);
        loginRollback = null;
    }

    public async Task<WebsiteAccount> SwitchAccount(int id, CancellationToken ct = default)
    {
        var target = ReadAccountSlots().SingleOrDefault(s => s.Session.Origin == origin.AbsoluteUri && s.Account.Id == id)
            ?? throw new UserException("这个账号尚未登录，请先添加账号。");
        Persist();
        var previous = Account is { } account ? new AccountSlot(account, CaptureSession()) : null;
        loginRollback = null;
        try
        {
            RestoreAccount(target);
            var confirmed = await RefreshAccount(ct);
            if (confirmed.Id != id) throw new UserException("账号身份不匹配，已保留原账号。");
            return confirmed;
        }
        catch { RestoreAccount(previous); throw; }
    }

    public void ForgetSavedAccount(int id)
    {
        var slots = ReadAccountSlots();
        slots.RemoveAll(s => s.Session.Origin == origin.AbsoluteUri && s.Account.Id == id);
        store.Vault.Put(AccountSlotsKey, JsonSerializer.Serialize(slots, JsonData.Options));
    }
}
