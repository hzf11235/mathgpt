using System.Net;
using System.Text;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class ScopedCatalogAudit
{
    private sealed class Handler(bool automatic = false, bool universal = false) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri?.AbsoluteUri != "https://mathgpt.xyz/v1/models?mathgpt_catalog=1" ||
                request.Headers.Authorization?.ToString() != "Bearer fixture-scoped-key")
                throw new InvalidOperationException("API-key setup attempted an unexpected account, pricing or inference request.");
            var scoped = automatic ? """{"schema_version":1,"group_verified":false,"group":"auto"}""" :
                """{"schema_version":1,"group_verified":true,"group":"gpt-standard","pricing":{"data":[{"model_name":"gpt-6.1-sol","enable_groups":["gpt-standard"],"group_endpoint_types":{"gpt-standard":["openai-response"]},"group_pricing":{"gpt-standard":{"available":true,"currency":"CNY","billing_mode":"token","markup_included":true,"token_unit":1000000,"rates":{"input_price":3,"output_price":15}}}}]}}""";
            if (universal) scoped = scoped.Replace("gpt-standard", "universal");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"id\":\"gpt-6.1-sol\"}],\"mathgpt\":" + scoped + "}", Encoding.UTF8, "application/json") });
        }
    }
    public static async Task Run(string root)
    {
        var store = new StateStore(root); var profile = new ConnectionProfile(); store.SaveProfile(profile, "fixture-scoped-key");
        var handler = new Handler(); using var client = new HttpClient(handler); using var site = new WebsiteClient(store, fixtureHandler: handler);
        var catalog = new CatalogService(store, site, client);
        var routes = await catalog.Load(profile);
        if (handler.Calls != 1 || routes.Count != 1 || !profile.GroupVerified || profile.Group != "gpt-standard" ||
            !ClientService.Supports(ClientKind.CodexDesktop, routes[0]) || routes[0].InputPrice != 3)
            throw new InvalidOperationException("API-key-only model/group/CNY synchronization failed.");
        var universalHandler = new Handler(universal: true); using var universalClient = new HttpClient(universalHandler);
        var universalRoutes = await new CatalogService(store, site, universalClient).Load(profile);
        if (profile.Group != "universal" || universalRoutes.Count != 1 || universalRoutes[0].GroupName != "万能密钥" ||
            !BuiltinGateway.Required(ClientKind.CodexDesktop, profile) || !BuiltinGateway.Required(ClientKind.Harness, profile))
            throw new InvalidOperationException("Verified universal pricing or mandatory cross-model adapter was lost.");
        var autoHandler = new Handler(true); using var autoClient = new HttpClient(autoHandler);
        try { await new CatalogService(store, site, autoClient).Load(profile); }
        catch (UserException ex) when (ex.Message.Contains("自动分组")) { return; }
        throw new InvalidOperationException("An ambiguous automatic group was guessed.");
    }
}
