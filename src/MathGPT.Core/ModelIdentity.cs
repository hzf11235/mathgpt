namespace MathGPT.Core;

// Explicit connection aliases only. Never strip arbitrary model-ID suffixes.
public static class ModelIdentity
{
    public static string Canonical(string id) => id switch
    {
        "gemini-4-argon-high" => "gemini-4-argon",
        "gemini-3.8-flash-low" or "gemini-3.8-flash-medium" or "gemini-3.8-flash-high" or "gemini-3.8-flash-tiered" => "gemini-3.8-flash",
        "claude-opus-5.5" => "claude-opus-5-5",
        "claude-sonnet-5.5" => "claude-sonnet-5-5",
        "claude-haiku-5.5" => "claude-haiku-5-5",
        "claude-fable-5.1" => "claude-fable-5-1",
        "claude-sonnet-4" => "claude-sonnet-4-0",
        _ => id
    };
    public static string AliasEffort(string id) => Canonical(id) != id && id.StartsWith("gemini-", StringComparison.Ordinal) ?
        new[] { "low", "medium", "high" }.FirstOrDefault(e => id.EndsWith("-" + e, StringComparison.Ordinal)) ?? "" : "";
}
