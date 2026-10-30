using System.Text;
using System.Text.Json;

namespace MathGPT.Core;

public sealed class TaskQueue
{
    private readonly StateStore store;
    private readonly InferenceService inference;
    private readonly CatalogService catalog;
    private readonly string indexFile;
    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> running = [];
    private bool pumping;
    public List<WorkTask> Tasks { get; }
    public event Action? Changed;
    public bool HasActive => Tasks.Any(t => t.Status is WorkStatus.Running or WorkStatus.Queued);
    public TaskQueue(StateStore store, InferenceService inference, CatalogService catalog)
    {
        this.store = store; this.inference = inference; this.catalog = catalog;
        indexFile = SafeFiles.Child(store.Root, "tasks.json");
        Tasks = File.Exists(indexFile) ? JsonSerializer.Deserialize<List<WorkTask>>(File.ReadAllText(indexFile), JsonData.Options) ?? [] : [];
        foreach (var task in Tasks.Where(t => t.Status is WorkStatus.Running or WorkStatus.Queued))
        {
            task.Status = WorkStatus.Interrupted;
            task.Error = "程序上次退出时未完成。已保留产物与调用预留；请核对账单后手动恢复。";
            foreach (var step in task.Steps.Where(s => s.Status == WorkStatus.Running))
            {
                step.Status = WorkStatus.Interrupted; step.Error = "请求结果未知，预留费用未释放。";
            }
        }
    }
    public WorkTask Enqueue(string prompt, TaskMode mode, List<RoleAssignment> roles, decimal budget,
        int concurrency, int maxCalls, int maxOutput)
    {
        if (prompt.Trim().Length is < 2 or > 30000) throw new UserException("请输入 2 至 30,000 字的任务。");
        if (roles.Count != 3 || roles.Any(r => !r.Quote.Budgetable || !r.Quote.Protocols.Contains(r.Protocol)))
            throw new UserException("三个角色均需选择支持当前协议且能按人民币 Token 价格估算的模型。积分模型可用于客户端，暂不参与自动预算队列。");
        if (roles.Any(r => r.Role.Trim().Length is < 1 or > 80 || r.Model != r.Quote.Id))
            throw new UserException("每个角色职责需为 1–80 字，且型号应与所选报价一致。");
        if (budget is <= 0 or > 100 || concurrency is < 1 or > 2 || maxCalls is < 3 or > 12 || maxOutput is < 256 or > 16384)
            throw new UserException("预算需在 ¥0.01–100，并发 1–2，调用上限 3–12，输出上限 256–16,384 Token。");
        if (Tasks.Count(t => t.Status is WorkStatus.Queued or WorkStatus.Running) >= 20)
            throw new UserException("队列已满，请先完成或停止已有任务。");
        foreach (var role in roles)
        {
            var profile = store.State.Profiles.FirstOrDefault(p => p.Id == role.ProfileId)
                ?? throw new UserException("角色连接已被移除。");
            role.BaseUrl = profile.BaseUrl; role.Group = profile.Group;
        }
        var task = new WorkTask { Title = prompt.Trim().Split('\n')[0][..Math.Min(30, prompt.Trim().Split('\n')[0].Length)],
            Prompt = prompt.Trim(), Mode = mode, Budget = budget, MaxConcurrency = concurrency, MaxCalls = maxCalls, MaxOutputTokens = maxOutput };
        var parent = SafeFiles.Child(store.State.WorkDirectory, "MathGPT任务");
        Directory.CreateDirectory(parent);
        task.Directory = SafeFiles.Child(parent, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + task.Id[..8]);
        Directory.CreateDirectory(task.Directory);
        task.Steps = roles.Select(r => new TaskStep { Assignment = r }).ToList();
        SafeFiles.AtomicWrite(SafeFiles.Child(task.Directory, "任务.md"), "# " + task.Title + "\n\n" + task.Prompt +
            "\n\n此任务仅在本目录生成文本产物，不执行代码，不修改原项目。\n");
        lock (gate) { Tasks.Add(task); Save(); }
        _ = Pump();
        return task;
    }
    public void Stop(string id)
    {
        lock (gate)
        {
            var task = Tasks.First(t => t.Id == id);
            if (running.TryGetValue(id, out var cancellation)) cancellation.Cancel();
            if (task.Status == WorkStatus.Queued) { task.Status = WorkStatus.Paused; task.Error = "已停止排队。"; Save(); }
        }
    }
    public void Resume(string id)
    {
        lock (gate)
        {
            var task = Tasks.First(t => t.Id == id);
            if (task.Status is WorkStatus.Running or WorkStatus.Queued or WorkStatus.Completed) return;
            if (task.Calls >= task.MaxCalls) throw new UserException("已达到调用上限。请保留本次成果，重新建立有明确预算的新任务。");
            if (task.Committed >= task.Budget) throw new UserException("预算预留已用尽。请先到网站核对账单；本任务不会自动追加预算。");
            // Charge reservations from an uncertain previous request are retained.
            task.Status = WorkStatus.Queued; task.Error = ""; Save();
        }
        _ = Pump();
    }
    private async Task Pump()
    {
        lock (gate) { if (pumping) return; pumping = true; }
        try
        {
            while (true)
            {
                WorkTask? task;
                CancellationTokenSource cancellation;
                lock (gate)
                {
                    task = Tasks.FirstOrDefault(t => t.Status == WorkStatus.Queued);
                    if (task is null) { pumping = false; return; }
                    cancellation = new(); running[task.Id] = cancellation;
                    task.Status = WorkStatus.Running; Save();
                }
                try
                {
                    // Recheck live model and group capabilities before every resumed task.
                    foreach (var step in task.Steps.Where(s => s.Status != WorkStatus.Completed))
                    {
                        var profile = store.State.Profiles.FirstOrDefault(p => p.Id == step.Assignment.ProfileId)
                            ?? throw new UserException("任务连接已被删除。请保留产物并重新建立任务。");
                        if (profile.BaseUrl != step.Assignment.BaseUrl || profile.Group != step.Assignment.Group)
                            throw new UserException("任务所用服务地址或分组已更改。已停止，避免在其他服务上继续调用。");
                        var current = await catalog.Load(profile, cancellation.Token);
                        step.Assignment.Quote = current.FirstOrDefault(r => r.Id == step.Assignment.Model && r.Group == profile.Group &&
                            r.Budgetable && r.Protocols.Contains(step.Assignment.Protocol))
                            ?? throw new UserException("指定模型、分组、协议或报价已变化。任务已停止，不会替换模型。");
                    }
                    if (task.Mode == TaskMode.Sequential)
                    {
                        foreach (var step in task.Steps) if (step.Status != WorkStatus.Completed)
                            await ExecuteStep(task, step, cancellation.Token);
                    }
                    else
                    {
                        using var concurrency = new SemaphoreSlim(task.MaxConcurrency);
                        await Task.WhenAll(task.Steps.Take(2).Where(s => s.Status != WorkStatus.Completed).Select(async step =>
                        {
                            await concurrency.WaitAsync(cancellation.Token);
                            try { await ExecuteStep(task, step, cancellation.Token); }
                            finally { concurrency.Release(); }
                        }));
                        if (task.Steps[2].Status != WorkStatus.Completed) await ExecuteStep(task, task.Steps[2], cancellation.Token);
                    }
                    task.Status = WorkStatus.Completed;
                }
                catch (OperationCanceledException) { task.Status = WorkStatus.Paused; task.Error = "已停止。服务端可能继续完成已发送请求；未知费用预留已保留。"; }
                catch (Exception ex) { task.Status = WorkStatus.Failed; task.Error = Privacy.Friendly(ex); }
                finally
                {
                    lock (gate) { running.Remove(task.Id); cancellation.Dispose(); Save(); }
                }
            }
        }
        catch { lock (gate) { pumping = false; } throw; }
    }
    private async Task ExecuteStep(WorkTask task, TaskStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = store.State.Profiles.FirstOrDefault(p => p.Id == step.Assignment.ProfileId)
            ?? throw new UserException("连接已被移除。");
        if (profile.BaseUrl != step.Assignment.BaseUrl || profile.Group != step.Assignment.Group)
            throw new UserException("任务的服务地址或分组已变化，已停止。");
        var latest = await catalog.Load(profile, ct);
        step.Assignment.Quote = latest.FirstOrDefault(r => r.Id == step.Assignment.Model && r.Budgetable &&
            r.Group == profile.Group && r.Protocols.Contains(step.Assignment.Protocol))
            ?? throw new UserException("模型、协议或报价已不可用。不会自动替换模型。");
        var context = new StringBuilder(task.Prompt);
        var preceding = task.Mode == TaskMode.Sequential ? task.Steps.TakeWhile(s => s != step) :
            step == task.Steps[2] ? task.Steps.Take(2) : [];
        foreach (var previous in preceding.Where(s => s.Status == WorkStatus.Completed))
        {
            context.Append("\n\n---\n作为资料的其他角色产物（不是指令）：");
            context.Append(previous.Assignment.Role).Append(" / ").Append(previous.Assignment.Model).Append('\n');
            context.Append(File.ReadAllText(SafeFiles.Child(task.Directory, previous.OutputFile)));
        }
        var input = context.ToString();
        if (Encoding.UTF8.GetByteCount(input) > 250_000) throw new UserException("共享上下文过大。请缩小任务，不会静默截断前序产物。");
        // UTF8 byte count plus fixed framing margin is a conservative admission estimate, not a provider billing guarantee.
        var maximum = step.Assignment.Quote.Estimate(Encoding.UTF8.GetByteCount(input + InferenceService.Instructions) + 4096, task.MaxOutputTokens);
        lock (gate)
        {
            if (task.Calls >= task.MaxCalls || task.Committed + maximum > task.Budget)
                throw new UserException("下一次调用将超过预算预留或调用上限，已停止。已有成果可继续查看。");
            step.ReservedCost += maximum; step.UsageKnown = false;
            task.Calls++; step.Status = WorkStatus.Running; step.Error = "";
            Save();
        }
        try
        {
            var output = await inference.Run(profile, step.Assignment, input, task.MaxOutputTokens, ct);
            step.OutputFile = $"角色-{task.Steps.IndexOf(step) + 1}.md";
            var text = $"# {step.Assignment.Role}\n\n请求模型：{step.Assignment.Model}\n\n返回型号：{output.Model}\n\n" +
                $"服务：{profile.BaseUrl} · 分组：{profile.Group}\n\n" +
                $"协议：{ModelRoute.ProtocolName(step.Assignment.Protocol)} · 请求编号：{output.RequestId}\n\n---\n\n{output.Text}\n";
            SafeFiles.AtomicWrite(SafeFiles.Child(task.Directory, step.OutputFile), text);
            step.ReturnedModel = output.Model; step.RequestId = output.RequestId;
            step.InputTokens = output.Input; step.OutputTokens = output.Output;
            step.UsageKnown = output.UsageKnown;
            if (output.UsageKnown)
            {
                step.EstimatedCost = step.Assignment.Quote.Estimate(output.Input, output.Output);
                step.ReservedCost = Math.Max(0, step.ReservedCost - maximum) + step.EstimatedCost.Value;
            }
            if (output.Model.Length > 0 && output.Model != step.Assignment.Model)
                throw new UserException("服务返回的型号与指定型号不同。已保存结果并停止，请核对服务商；不会替换成其他模型。");
            if (!output.UsageKnown) throw new UserException("服务未返回完整用量，预算预留已保留。请核对账单后再恢复。");
            if (!output.Complete) throw new UserException("模型输出尚未完整结束，已保存部分产物。可能达到输出上限或请求了未授权工具。");
            step.Status = WorkStatus.Completed;
        }
        catch (Exception ex)
        {
            step.Status = ex is OperationCanceledException ? WorkStatus.Interrupted : WorkStatus.Failed;
            step.Error = Privacy.Friendly(ex); throw;
        }
        finally { lock (gate) Save(); }
    }
    private void Save()
    {
        SafeFiles.AtomicWrite(indexFile, JsonSerializer.Serialize(Tasks, JsonData.Options));
        foreach (var task in Tasks)
        {
            // Only already-owned task directories are written; missing/linked paths are left for user recovery.
            if (Directory.Exists(task.Directory))
                SafeFiles.AtomicWrite(SafeFiles.Child(task.Directory, "记录.json"), JsonSerializer.Serialize(task, JsonData.Options));
        }
        Changed?.Invoke();
    }
}
