using System.Text.Json;
using System.Text.Json.Nodes;

namespace MathGPT.Core;

// MathGPT's conservative adapter profile, not vendor-certified metadata.
// Schema: openai/codex rust-v0.162.1 protocol/openai_models.rs ModelInfo.
public static class CompatibilityCatalog
{
    public static string Create(ConnectionProfile profile, IReadOnlyList<ModelRoute> routes) => JsonSerializer.Serialize(new
    {
        models = ModelCatalogScope.Available(profile, routes).Select(r =>
            {
                var levels = ReasoningPolicy.Allowed(r.Id);
                // Unknown models remain selectable without advertising an
                // invented effort level or forcing an unsupported parameter.
                return new
                {
                slug = r.Id, display_name = r.Id,
                description = "MathGPT compatibility adapter · text and function tools · 32K local context limit",
                supported_reasoning_levels = levels.Select(e => new { effort = e, description = e }).ToArray(), shell_type = "shell_command",
                visibility = "list", supported_in_api = true, priority = 0,
                support_verbosity = false, default_verbosity = (string?)null,
                apply_patch_tool_type = (string?)null,
                supports_reasoning_summary_parameter = false,
                truncation_policy = new { mode = "bytes", limit = 10000 },
                context_window = 32768, max_context_window = 32768,
                experimental_supported_tools = Array.Empty<string>(), input_modalities = new[] { "text" },
                include_apps_usage_instructions = false,
                supports_parallel_tool_calls = false, supports_search_tool = false,
                model_messages = new { instructions_template = "You are a coding assistant. Complete the user's task using the available tools. Preserve existing work and report actual verification results. Follow the host's permissions and approval requirements." },
                upgrade = (object?)null, availability_nux = (object?)null,
                };
            })
    }, JsonData.Options);

    // Keep exact vendor metadata where present, and do not hide other authorized
    // models merely because an earlier application cache did not know them yet.
    // Missing metadata uses the documented conservative compatibility profile.
    public static string CreateNative(ConnectionProfile profile, IReadOnlyList<ModelRoute> routes, string? vendorCache)
    {
        var scope = ModelCatalogScope.Available(profile, routes)
            .Where(r => r.Protocols.Contains(WireProtocol.Responses)).ToArray();
        var fallback = JsonNode.Parse(Create(profile, scope))!.AsObject();
        if (vendorCache is null) return fallback.ToJsonString(JsonData.Options);
        JsonNode? cached;
        try { cached = JsonNode.Parse(vendorCache); }
        catch (JsonException) { return fallback.ToJsonString(JsonData.Options); }
        var models = cached is JsonObject obj ? obj["models"] as JsonArray : null;
        if (models is null) return fallback.ToJsonString(JsonData.Options);
        var known = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in models.OfType<JsonObject>())
        {
            if (item["slug"] is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrEmpty(id)) continue;
            known.TryAdd(id, item);
        }
        var complete = fallback["models"]!.AsArray();
        for (var index = 0; index < complete.Count; index++)
        {
            var id = complete[index]!["slug"]!.GetValue<string>();
            if (known.TryGetValue(id, out var native)) complete[index] = native.DeepClone();
        }
        return fallback.ToJsonString(JsonData.Options);
    }
}
