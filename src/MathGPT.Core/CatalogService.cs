using System.Net.Http.Headers;
using System.Text.Json;

namespace MathGPT.Core;

public sealed class CatalogService(StateStore store, WebsiteClient site, HttpClient? http = null)
{
    private readonly HttpClient client = http ?? HttpSafety.Create();
    public async Task<List<ModelRoute>> Load(ConnectionProfile profile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (profile.IsMathGpt)
        {
            if (profile.Reasoning.Length == 0) profile.Reasoning = ModelIdentity.AliasEffort(profile.Model);
            profile.Model = ModelIdentity.Canonical(profile.Model);
        }
        var key = store.Key(profile) ?? throw new UserException("API Key 不存在，请重新保存连接。");
        var endpoint = UrlPolicy.Endpoint(profile.BaseUrl, "models");
        if (profile.IsMathGpt) endpoint = new Uri(endpoint.AbsoluteUri + "?mathgpt_catalog=1");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request, ct);
        var json = await HttpSafety.Json(response, ct);
        ct.ThrowIfCancellationRequested();
        var ids = json.Obj("data").Items().Select(x => x.Str("id")).Where(x => x.Length > 0).ToHashSet();
        List<ModelRoute> routes;
        if (profile.IsMathGpt)
        {
            var scoped = json.Obj("mathgpt");
            if (scoped.Number("schema_version") == 1)
            {
                if (!scoped.Flag("group_verified")) throw new UserException("此密钥使用旧版自动分组。请在网站创建万能密钥或固定分组密钥。");
                profile.Group = scoped.Str("group"); profile.GroupVerified = true;
                routes = ParsePricing(scoped.Obj("pricing")).Where(r => r.Group == profile.Group && ids.Contains(r.Id)).ToList();
            }
            else routes = ParsePricing(await site.Pricing(ct)).Where(r => r.Group == profile.Group && ids.Contains(r.Id)).ToList();
        }
        else routes = ids.OrderBy(x => x).Select(id => new ModelRoute
        {
            Id = id, Group = profile.Group, GroupName = profile.Group, Protocols = [profile.Protocol], Available = true,
            BillingMode = "token", InputPrice = profile.CustomInputPrice, OutputPrice = profile.CustomOutputPrice,
            FinalCny = profile.CustomInputPrice is >= 0 && profile.CustomOutputPrice is >= 0,
            PriceVersion = "用户填写的价格，未由服务端核验"
        }).ToList();
        if (routes.Count == 0) throw new UserException(ids.Count == 0 ? "密钥有效，但当前没有已开放模型。请检查分组是否暂停或密钥是否限制了型号。" :
            "模型目录已返回，但没有此密钥分组的可结算线路。请更换已开放分组的密钥，或联系站点管理员。");
        if (!routes.Any(r => r.Available && r.Protocols.Count > 0))
            throw new UserException("当前分组暂不可用：" + string.Join("、", routes.Select(r => r.Reason).Where(r => r.Length > 0).Distinct().Take(3)) + "。请更换已开放分组的密钥。");
        ct.ThrowIfCancellationRequested();
        profile.LastSync = $"{DateTime.Now:MM-dd HH:mm} · 已读取目录，模型调用未验证";
        store.Save();
        return routes;
    }
    public static List<ModelRoute> ParsePricing(JsonElement root)
    {
        var result = new List<ModelRoute>();
        var names = root.Obj("usable_group");
        foreach (var model in root.Obj("data").Items())
        {
            var groups = model.Obj("group_pricing");
            var quotedGroups = new HashSet<string>();
            foreach (var group in groups.ValueKind == JsonValueKind.Object ? groups.EnumerateObject().ToArray() : [])
            {
                quotedGroups.Add(group.Name);
                var p = group.Value;
                var available = p.Flag("available") && !model.Flag("runtime_paused") &&
                    model.Obj("enable_groups").Items().Any(x => x.GetString() == group.Name);
                // A model-wide protocol declaration must never widen a group's contract.
                var protocols = model.Obj("group_endpoint_types").Obj(group.Name).Items()
                    .Select(x => x.GetString()).Select(ParseProtocol).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
                var rates = p.Obj("rates");
                var tiers = p.Obj("tiers").Items().Where(t => t.Obj("rates").Money("input_price") is not null &&
                        t.Obj("rates").Money("output_price") is not null).Select(t => new PriceTier(
                        t.Obj("max_inclusive").ValueKind == JsonValueKind.Null ? null : t.Number("max_inclusive"),
                        t.Obj("rates").Money("input_price")!.Value, t.Obj("rates").Money("output_price")!.Value)).ToList();
                result.Add(new ModelRoute
                {
                    Id = model.Str("model_name"), Group = group.Name, GroupName = group.Name == "universal" ? "万能密钥" : names.Str(group.Name, group.Name),
                    SelectedGroup = model.Str("selected_group", group.Name),
                    Available = available && protocols.Count > 0, Protocols = protocols,
                    Reason = model.Flag("runtime_paused") ? "型号暂停" : available ? "协议未开放" : "线路或计费合同不可用",
                    BillingMode = p.Str("billing_mode"), PriceVersion = p.Str("source_version", root.Str("pricing_version")),
                    InputPrice = rates.Money("input_price"), OutputPrice = rates.Money("output_price"),
                    BudgetInputPrice = new[] { rates.Money("input_price"), rates.Money("cache_write_price"), rates.Money("cache_write_1h_price") }
                        .Where(p => p.HasValue).Select(p => p!.Value).DefaultIfEmpty().Max(),
                    CreditPrice = p.Money("per_credit_price"), TokenUnit = (int)p.Number("token_unit", 1_000_000),
                    FinalCny = p.Str("currency") == "CNY" && p.Flag("markup_included") && !p.Flag("additional_group_ratio"),
                    Tiers = tiers
                });
            }
            foreach (var group in model.Obj("enable_groups").Items().Select(g => g.GetString() ?? "")
                .Where(g => g.Length > 0 && !quotedGroups.Contains(g)))
            {
                var protocols = model.Obj("group_endpoint_types").Obj(group).Items().Select(e => ParseProtocol(e.GetString()))
                    .Where(e => e.HasValue).Select(e => e!.Value).Distinct().ToList();
                result.Add(new ModelRoute
                {
                    Id = model.Str("model_name"), Group = group, GroupName = names.Str(group, group), Protocols = protocols,
                    Available = !model.Flag("runtime_paused") && protocols.Count > 0,
                    Reason = model.Flag("runtime_paused") ? "型号暂停" : "协议未开放",
                    BillingMode = "unpublished", PriceVersion = root.Str("pricing_version"), FinalCny = false
                });
            }
        }
        return result;
    }
    public static WireProtocol? ParseProtocol(string? value) => value switch
    {
        "openai" => WireProtocol.ChatCompletions, "openai-response" => WireProtocol.Responses,
        "anthropic" => WireProtocol.Anthropic, _ => null
    };
}
