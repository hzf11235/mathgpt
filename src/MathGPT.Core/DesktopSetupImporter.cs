namespace MathGPT.Core;

public sealed record ImportedDesktopSetup(string ProfileId, ClientKind Client, string Model, string Group, int ModelCount);

public static class DesktopSetupImporter
{
    public static async Task<ImportedDesktopSetup> Import(StateStore store, CatalogService catalog, DesktopSetupGrant grant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (grant.BaseUrl != "https://mathgpt.xyz/v1" || grant.TokenId <= 0 || grant.ApiKey.Length < 8)
            throw new UserException("接入配置无效，未保存连接。");
        var kind = grant.Client switch { "codex" => ClientKind.CodexDesktop, "claude" => ClientKind.ClaudeDesktop,
            "harness" => ClientKind.Harness, _ => throw new UserException("此应用不支持自动接入。") };
        var profile = store.State.Profiles.FirstOrDefault(p => p.IsMathGpt && p.WebsiteKeyId == grant.TokenId) ?? new ConnectionProfile();
        profile.Name = grant.TokenName.Length > 0 ? grant.TokenName : "网页接入";
        profile.BaseUrl = grant.BaseUrl; profile.WebsiteKeyId = grant.TokenId; profile.Group = grant.Group; profile.GroupVerified = true;
        profile.Model = ModelIdentity.Canonical(grant.Model);
        profile.Reasoning = grant.Reasoning;
        profile.SetupPlanId = grant.PlanId;
        ct.ThrowIfCancellationRequested();
        store.SaveProfile(profile, grant.ApiKey);
        var routes = await catalog.Load(profile, ct);
        ct.ThrowIfCancellationRequested();
        var eligible = routes.Where(r => r.Available && ClientService.Supports(kind, r)).ToList();
        var route = eligible.FirstOrDefault(r => r.Id == profile.Model);
        if (profile.Model.Length > 0 && route is null)
            throw new UserException("网页选择的模型暂不可用。已保留密钥，请选择其他可用模型后继续。");
        route ??= new[] { "gpt-6.1-sol", "claude-opus-5-5", "gemini-4-argon" }
            .Select(id => eligible.FirstOrDefault(r => r.Id == id)).FirstOrDefault(r => r is not null) ?? eligible.FirstOrDefault();
        if (route is null) throw new UserException("此密钥暂时没有可用模型，请回网站检查分组。");
        profile.Model = route.Id;
        profile.Protocol = ModelCatalogScope.Protocol(route, profile.Protocol);
        if (!ReasoningPolicy.Allowed(route.Id).Contains(profile.Reasoning)) profile.Reasoning = "";
        ct.ThrowIfCancellationRequested();
        store.SaveProfile(profile, null);
        store.State.PreferredClient = kind;
        store.Save();
        return new(profile.Id, kind, profile.Model, profile.Group, eligible.Select(r => r.Id).Distinct().Count());
    }
}
