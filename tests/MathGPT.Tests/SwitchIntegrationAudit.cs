using MathGPT.Core;

namespace MathGPT.Tests;
internal static class SwitchIntegrationAudit
{
    public static Task Run(string root)
    {
        var profile = new ConnectionProfile { Model = "claude-opus-5-5", Group = "claude-pending",
            Protocol = WireProtocol.Anthropic, BaseUrl = "https://mathgpt.xyz/v1" };
        var route = new ModelRoute { Id = profile.Model, Group = profile.Group, Available = true,
            Protocols = [WireProtocol.Anthropic], BillingMode = "credit", CreditPrice = 0.135m };
        if (!ClientService.Supports(ClientKind.CodexDesktop, route) || !SwitchIntegration.CanRoute(ClientKind.CodexDesktop, route))
            throw new Exception("Native compatibility must stay distinct from local conversion.");
        var uri = new Uri(SwitchIntegration.BuildImport(ClientKind.CodexDesktop, profile, route, "sk-fixture-only"));
        var values = uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2))
            .ToDictionary(s => Uri.UnescapeDataString(s[0]), s => Uri.UnescapeDataString(s[1]));
        if (uri.Scheme != "ccswitch" || values["app"] != "codex" || values["apiFormat"] != "anthropic" ||
            values["endpoint"] != "https://mathgpt.xyz" || values["model"] != profile.Model || values["enabled"] != "false")
            throw new Exception("Import changed the model, protocol, endpoint or activation.");
        bool rejected = false;
        try { SwitchIntegration.BuildImport(ClientKind.ClaudeDesktop, profile, route, "sk-fixture-only"); }
        catch (UserException) { rejected = true; }
        if (!rejected) throw new Exception("Unsupported Claude Desktop deep link must be rejected.");
        var store = new StateStore(root);
        store.State.Clients.Add(new() { Kind = ClientKind.CodexDesktop, LocalRouting = true, ProfileId = profile.Id });
        store.Save();
        if (!new StateStore(root).State.Clients.Single().LocalRouting)
            throw new Exception("Local routing must survive a restart.");
        if (File.ReadAllText(Path.Combine(root, "settings.json")).Contains("sk-fixture-only"))
            throw new Exception("Import credentials were persisted.");
        return Task.CompletedTask;
    }
}
