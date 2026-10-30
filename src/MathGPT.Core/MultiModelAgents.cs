using System.Text.Json;

namespace MathGPT.Core;

// Native agent definitions use the parent's provider and permissions. They
// contain model IDs only; credentials remain in the existing helper/DPAPI path.
public static class MultiModelAgents
{
    public const string Marker = "MathGPT managed model agent";
    public static IEnumerable<(string Name, string Text)> Create(ClientKind kind, IReadOnlyList<ModelRoute> routes)
    {
        var available = routes.Where(r => r.Available && r.Group == "universal").Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, candidates) in new (string, string[])[]
        {
            ("mathgpt_gpt_sol", ["gpt-6.1-sol", "gpt-6-sol", "gpt-5.6-sol"]),
            ("mathgpt_gpt_astra", ["gpt-6-astra"]),
            ("mathgpt_claude", ["claude-opus-5-5", "claude-sonnet-5"]),
            ("mathgpt_gemini", ["gemini-4-argon"])
        })
        {
            var model = candidates.FirstOrDefault(available.Contains);
            if (model is null) continue;
            var description = "Use " + model + " when the user asks for this model to review, implement, or polish a task.";
            var instructions = "Work on the parent's explicit task. Preserve the user's files and the parent's approval policy. Return the result and actual verification evidence. Do not claim to be another model or to have verified provider identity. Do not spawn more agents unless explicitly requested.";
            static string Q(string value) => JsonSerializer.Serialize(value);
            if (kind == ClientKind.CodexDesktop || kind == ClientKind.Codex)
                yield return (name + ".toml", "# " + Marker + "\nname = " + Q(name) + "\ndescription = " + Q(description) +
                    "\nmodel = " + Q(model) + (ReasoningPolicy.Allowed(model).Contains("low") ? "\nmodel_reasoning_effort = \"low\"" : "") + "\ndeveloper_instructions = " + Q(instructions) + "\n");
            else
                yield return (name.Replace('_', '-') + ".md", "---\nname: " + name.Replace('_', '-') + "\ndescription: " + Q(description) +
                    "\nmodel: " + Q(model) + "\n---\n<!-- " + Marker + " -->\n" + instructions + "\n");
        }
    }
}
