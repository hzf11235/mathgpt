namespace MathGPT.Core;

public static class ReasoningPolicy
{
    // Official model and effort references checked on 2026-10-29.
    public static string[] Allowed(string model) => ModelIdentity.Canonical(model) switch
    {
        "gemini-4-argon" or "gemini-3.8-flash" => ["low", "medium", "high"],
        "claude-opus-5-5" or "claude-sonnet-5-5" or "claude-haiku-5-5" or "claude-fable-5-1" => ["low", "medium", "high", "xhigh", "max"],
        "gpt-6.1-sol" or "gpt-6-astra" => ["low", "medium", "high", "xhigh", "max"],
        "gpt-6.1-terra" or "gpt-6-sol" or "gpt-6-luna" or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna" => ["none", "low", "medium", "high", "xhigh", "max"],
        _ => []
    };
}
