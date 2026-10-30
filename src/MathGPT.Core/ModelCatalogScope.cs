namespace MathGPT.Core;

// A saved default is a preference, never a restriction on a key's model scope.
public static class ModelCatalogScope
{
    public static ModelRoute[] Available(ConnectionProfile profile, IReadOnlyList<ModelRoute> routes) =>
        routes.Where(r => r.Available && r.Group == profile.Group && r.Protocols.Count > 0 && r.Id.Length > 0)
            .GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderByDescending(r => r.Id == profile.Model).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();

    public static WireProtocol Protocol(ModelRoute route, WireProtocol preferred)
    {
        if (route.Id.StartsWith("gpt-", StringComparison.Ordinal) && route.Protocols.Contains(WireProtocol.Responses))
            return WireProtocol.Responses;
        if (route.Id.StartsWith("claude-", StringComparison.Ordinal) && route.Protocols.Contains(WireProtocol.Anthropic))
            return WireProtocol.Anthropic;
        if (route.Id.StartsWith("gemini-", StringComparison.Ordinal) && route.Protocols.Contains(WireProtocol.ChatCompletions))
            return WireProtocol.ChatCompletions;
        if (route.Protocols.Contains(preferred)) return preferred;
        return route.Protocols.FirstOrDefault();
    }
}
