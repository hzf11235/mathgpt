namespace MathGPT.Core;

public enum TaskMode { Sequential, ParallelSynthesis }
public enum WorkStatus { Queued, Running, Paused, Completed, Failed, Interrupted }
public sealed class RoleAssignment
{
    public string Role { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Model { get; set; } = "";
    public WireProtocol Protocol { get; set; }
    public string Reasoning { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Group { get; set; } = "";
    public ModelRoute Quote { get; set; } = new();
}
public sealed class TaskStep
{
    public RoleAssignment Assignment { get; set; } = new();
    public WorkStatus Status { get; set; } = WorkStatus.Queued;
    public string OutputFile { get; set; } = "";
    public string Error { get; set; } = "";
    public string ReturnedModel { get; set; } = "";
    public string RequestId { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal ReservedCost { get; set; }
    public decimal? EstimatedCost { get; set; }
    public bool UsageKnown { get; set; }
}
public sealed class WorkTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public TaskMode Mode { get; set; }
    public WorkStatus Status { get; set; } = WorkStatus.Queued;
    public decimal Budget { get; set; } = 1m;
    public int MaxCalls { get; set; } = 6;
    public int MaxConcurrency { get; set; } = 2;
    public int MaxOutputTokens { get; set; } = 2048;
    public int Calls { get; set; }
    public string Directory { get; set; } = "";
    public string Error { get; set; } = "";
    public List<TaskStep> Steps { get; set; } = [];
    public string Display => $"{Title} · {StatusText(Status)} · {Steps.Count(s => s.Status == WorkStatus.Completed)}/{Steps.Count} · {Calls}/{MaxCalls} 次";
    public decimal Committed => Steps.Sum(s => s.ReservedCost);
    public static string StatusText(WorkStatus status) => status switch
    {
        WorkStatus.Queued => "排队", WorkStatus.Running => "进行中", WorkStatus.Paused => "已停止",
        WorkStatus.Completed => "完成", WorkStatus.Failed => "需处理", _ => "上次运行中断"
    };
    public override string ToString() => Display;
}
public sealed record ModelOutput(string Text, string Model, string RequestId, long Input, long Output, bool UsageKnown, bool Complete);
