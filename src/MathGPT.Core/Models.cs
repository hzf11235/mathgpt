using System.Text.Json;
using System.Text.Json.Serialization;

namespace MathGPT.Core;

public enum WireProtocol { Responses, ChatCompletions, Anthropic }
public enum ClientKind { Harness, Codex, Claude, CodexDesktop, ClaudeDesktop }

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 2;
    public string Appearance { get; set; } = "aurora";
    public bool DarkMode { get; set; }
    public string ActiveProfileId { get; set; } = "";
    public ClientKind PreferredClient { get; set; } = ClientKind.CodexDesktop;
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MathGPT", "Downloads");
    public string InstallDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MathGPT", "Clients");
    public string WorkDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MathGPT");
    public List<ConnectionProfile> Profiles { get; set; } = [];
    public List<ClientInstallation> Clients { get; set; } = [];
    public List<DownloadRecord> Downloads { get; set; } = [];
}

public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "MathGPT";
    public string BaseUrl { get; set; } = "https://mathgpt.xyz/v1";
    public string Group { get; set; } = "default";
    public bool GroupVerified { get; set; }
    public int? WebsiteKeyId { get; set; }
    public string SetupPlanId { get; set; } = "";
    public string Model { get; set; } = "";
    public WireProtocol Protocol { get; set; } = WireProtocol.Responses;
    public string Reasoning { get; set; } = "";
    public string LastSync { get; set; } = "";
    public string SecretRef { get; set; } = "";
    public string PreviousSecretRef { get; set; } = "";
    public decimal? CustomInputPrice { get; set; }
    public decimal? CustomOutputPrice { get; set; }
    public bool IsMathGpt => UrlPolicy.IsMathGpt(BaseUrl);
    public override string ToString() => $"{Name} · {(Group == "universal" ? "万能密钥" : Group)}";
}

public sealed class ClientInstallation
{
    public ClientKind Kind { get; set; }
    public string Executable { get; set; } = "";
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "";
    public bool Managed { get; set; }
    public string InstallationRoot { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string WorkDirectory { get; set; } = "";
    public string ConfigDirectory { get; set; } = "";
    public string PackageFamily { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string PackageSignatureKind { get; set; } = "";
    public bool ConfigPending { get; set; }
    public bool LocalRouting { get; set; }
    public bool BuiltinRouting { get; set; }
    public string ConfigurationMessage { get; set; } = "";
    public string ConfigurationFingerprint { get; set; } = "";
}

public sealed class DownloadRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Destination { get; set; } = "";
    public string BitsJobId { get; set; } = "";
    public string Status { get; set; } = "等待下载";
    public string PartialFile { get; set; } = "";
}

public sealed record PackageRelease(ClientKind Kind, string Version, string Url, string FileName,
    string HashAlgorithm, string Digest, bool Installer, string Architecture)
{
    public string OfficialUrl { get; init; } = "";
    public long ExpectedBytes { get; init; }
    public string PackageKind { get; init; } = "";
    public string Platform { get; init; } = "";
    public bool Offline { get; init; }
}
public sealed record TransferProgress(string Message, double Percent, long Received = 0, long Total = 0);
public sealed record WebsiteAccount(int Id, string Name, decimal Balance, decimal Used);
public sealed record WebsiteKey(int Id, string Name, string Group, int Status, decimal Remaining, bool Unlimited, long ExpiresAt)
{
    public bool Usable => Status == 1 && (Unlimited || Remaining > 0) &&
        (ExpiresAt <= 0 || ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public override string ToString() => $"{Name} · {(Group == "universal" ? "万能密钥" : Group)} · {(Usable ? Unlimited ? "不限额" : $"余 ¥{Remaining:0.####}" : "不可用")}";
}
public sealed record LoginChallenge(string FlowToken, string[] Methods);
public sealed record PriceTier(long? MaxInclusive, decimal Input, decimal Output);

public sealed class ModelRoute
{
    public string Id { get; init; } = "";
    public string Group { get; init; } = "";
    public string GroupName { get; init; } = "";
    public string SelectedGroup { get; init; } = "";
    public List<WireProtocol> Protocols { get; init; } = [];
    public bool Available { get; init; }
    public string Reason { get; init; } = "";
    public string BillingMode { get; init; } = "";
    public string PriceVersion { get; init; } = "";
    public decimal? InputPrice { get; init; }
    public decimal? OutputPrice { get; init; }
    public decimal? BudgetInputPrice { get; init; }
    public decimal? CreditPrice { get; init; }
    public int TokenUnit { get; init; } = 1_000_000;
    public bool FinalCny { get; init; }
    public List<PriceTier> Tiers { get; init; } = [];
    public string ProtocolText => string.Join(" / ", Protocols.Select(ProtocolName));
    public string PriceText => !Available ? $"暂不可用 · {Reason}" :
        BillingMode == "credit" && CreditPrice is not null ? $"¥{CreditPrice:0.######} / 积分" :
        InputPrice is not null && OutputPrice is not null ? $"输入 ¥{InputPrice:0.######} · 输出 ¥{OutputPrice:0.######} / {TokenUnit:N0} Token" :
        Tiers.Count > 0 ? "按上下文阶梯计价 · 以网站账单为准" :
        BillingMode == "unpublished" ? "站内未提供最终价 · 以账单为准" : "未提供价格";
    public bool Budgetable => Available && FinalCny && BillingMode == "token" && TokenUnit > 0 &&
        ((InputPrice is >= 0 && OutputPrice is >= 0) || Tiers.Count > 0 && Tiers.All(t => t.Input >= 0 && t.Output >= 0));
    public decimal Estimate(long input, long output) =>
        (input * Math.Max(Math.Max(InputPrice ?? 0, BudgetInputPrice ?? 0), Tiers.Select(t => t.Input).DefaultIfEmpty().Max()) +
         output * Math.Max(OutputPrice ?? 0, Tiers.Select(t => t.Output).DefaultIfEmpty().Max())) / Math.Max(1, TokenUnit);
    public static string ProtocolName(WireProtocol protocol) => protocol switch
    {
        WireProtocol.Responses => "Responses", WireProtocol.Anthropic => "Messages", _ => "Chat Completions"
    };
    public override string ToString() => Id;
}

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Str(this JsonElement value, string key, string fallback = "") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var x) && x.ValueKind != JsonValueKind.Null
            ? x.ValueKind == JsonValueKind.String ? x.GetString() ?? fallback : x.ToString() : fallback;
    public static JsonElement Obj(this JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var x) ? x : default;
    public static bool Flag(this JsonElement value, string key) => value.Obj(key).ValueKind == JsonValueKind.True;
    public static decimal? Money(this JsonElement value, string key) =>
        decimal.TryParse(value.Str(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    public static long Number(this JsonElement value, string key, long fallback = 0) =>
        long.TryParse(value.Str(key), out var n) ? n : fallback;
    public static IEnumerable<JsonElement> Items(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
