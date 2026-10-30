namespace MathGPT.Core;

public static class QuickConnection
{
    public static async Task<ConnectionProfile> Save(StateStore store, CatalogService catalog, string key,
        ClientKind client, string name = "MathGPT", string baseUrl = "https://mathgpt.xyz/v1",
        int? websiteKeyId = null, CancellationToken ct = default)
    {
        var previous = store.State.ActiveProfileId;
        var profile = new ConnectionProfile { Name = name, BaseUrl = UrlPolicy.Base(baseUrl).AbsoluteUri.TrimEnd('/'), WebsiteKeyId = websiteKeyId };
        try
        {
            store.SaveProfile(profile, key);
            var routes = await catalog.Load(profile, ct);
            var eligible = routes.Where(r => r.Available && ClientService.Supports(client, r)).ToArray();
            var preferred = client == ClientKind.ClaudeDesktop
                ? new[] { "claude-opus-5-5", "claude-sonnet-5-5", "gpt-6.1-sol", "gemini-4-argon" }
                : new[] { "gpt-6.1-sol", "gemini-4-argon", "claude-opus-5-5", "gpt-6-astra" };
            var route = preferred.Select(id => eligible.FirstOrDefault(r => r.Id == id)).FirstOrDefault(r => r is not null) ?? eligible.FirstOrDefault()
                ?? throw new UserException("当前密钥没有可用模型，请更换分组。");
            profile.Model = route.Id;
            profile.Protocol = ModelCatalogScope.Protocol(route, profile.Protocol);
            store.SaveProfile(profile, null);
            return profile;
        }
        catch
        {
            if (store.State.Profiles.Contains(profile)) store.RemoveProfile(profile);
            store.State.ActiveProfileId = previous;
            store.Save();
            throw;
        }
    }
}
