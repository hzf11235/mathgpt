using System.Windows;
using System.Windows.Controls;
using MathGPT.Core;

namespace MathGPT.App.Pages;

public sealed class TasksPage : UserControl
{
    private sealed record RoleRow(TextBox Role, ComboBox Profile, ComboBox Model, ComboBox Protocol);
    private readonly MainWindow window;
    private readonly AppServices services;
    private readonly List<RoleRow> roles = [];
    private readonly TextBox prompt = Ui.Input("task-prompt");
    private readonly ComboBox mode = Ui.Select("task-mode");
    private readonly TextBox budget = Ui.Input("task-budget", "1");
    private readonly TextBox calls = Ui.Input("task-max-calls", "6");
    private readonly TextBox concurrency = Ui.Input("task-concurrency", "2");
    private readonly TextBox outputTokens = Ui.Input("task-output-limit", "2048");
    private readonly ListBox tasks = new() { Height = 165 };
    private readonly TextBlock details = Ui.Text("选择任务查看进度、用量与产物。", 13, "#687384");
    private readonly TextBox result = Ui.Input("task-result");
    private WorkTask? selected;
    public TasksPage(MainWindow window)
    {
        this.window = window; services = window.Services;
        prompt.AcceptsReturn = true; prompt.TextWrapping = TextWrapping.Wrap;
        prompt.Height = 125; prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        mode.ItemsSource = new[] { "顺序接力：规划 → 实现 → 审阅", "并行候选：两个候选 → 汇总" }; mode.SelectedIndex = 0;
        var rolePanel = new StackPanel();
        string[] names = ["规划", "实现", "审阅"];
        for (int i = 0; i < 3; i++)
        {
            var number = i;
            var role = Ui.Input("task-role-" + i, names[i]);
            var profile = Ui.Select("task-profile-" + i);
            profile.ItemsSource = services.Store.State.Profiles.ToList();
            profile.SelectedItem = services.Store.State.Profiles.FirstOrDefault(p => p.Id == services.Store.State.ActiveProfileId);
            var model = Ui.Select("task-model-" + i);
            var protocol = Ui.Select("task-protocol-" + i);
            Ui.ProtocolNames(protocol);
            var row = new RoleRow(role, profile, model, protocol); roles.Add(row);
            profile.SelectionChanged += (_, _) => { model.ItemsSource = null; protocol.ItemsSource = null; };
            model.SelectionChanged += (_, _) =>
            {
                if (model.SelectedItem is ModelRoute route)
                {
                    protocol.ItemsSource = route.Protocols; protocol.SelectedIndex = 0;
                    var p = row.Profile.SelectedItem as ConnectionProfile;
                    if (p is not null && route.Protocols.Contains(p.Protocol)) protocol.SelectedItem = p.Protocol;
                }
            };
            rolePanel.Children.Add(Ui.Card($"角色 {i + 1}",
                Ui.Columns(Ui.Field("角色职责", role), Ui.Field("连接 / 分组", profile)),
                Ui.Columns(Ui.Field("模型", model), Ui.Field("协议", protocol)),
                Ui.Button("读取此连接的模型", () => _ = window.Run(async ct =>
                {
                    var p = row.Profile.SelectedItem as ConnectionProfile ?? throw new UserException("请先添加并选择 API 连接。");
                    var available = (await services.Catalog.Load(p, ct)).Where(r => r.Budgetable).ToList();
                    row.Model.ItemsSource = available; row.Model.SelectedItem = available.FirstOrDefault(r => r.Id == p.Model);
                    if (available.Count == 0) throw new UserException("此连接没有可用于预算的最终 Token 价。请选择已提供最终人民币报价的分组；积分模型可用于客户端。");
                }, $"角色 {number + 1} 的模型已读取。", true))));
        }
        mode.SelectionChanged += (_, _) =>
        {
            var labels = mode.SelectedIndex == 1 ? new[] { "候选 A", "候选 B", "审阅与汇总" } : names;
            for (int i = 0; i < roles.Count; i++) roles[i].Role.Text = labels[i];
        };
        result.IsReadOnly = true; result.AcceptsReturn = true; result.TextWrapping = TextWrapping.Wrap;
        result.Height = 280; result.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        result.FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Microsoft YaHei UI");
        tasks.SelectionChanged += (_, _) => { selected = tasks.SelectedItem as WorkTask; ShowDetails(); };
        Content = Ui.Stack(Ui.Card("提交一次工作",
            Ui.Field("任务", prompt), Ui.Field("协作方式", mode),
            Ui.Hint("角色共享本任务的产物。默认只生成文本文件，不执行命令或修改已有项目。")),
            rolePanel, Ui.Card("本次边界",
                Ui.Columns(Ui.Field("人民币预算预留", budget), Ui.Field("最多调用次数", calls)),
                Ui.Columns(Ui.Field("并发上限（1–2）", concurrency), Ui.Field("每次输出 Token 上限", outputTokens)),
                Ui.Hint("预算按报价和保守 Token 估计准入，不是服务端硬扣费封顶。停止不能撤销已发送请求的计费；未知用量保留费用预留。没有自动重试或模型替换。"),
                Ui.Hint("积分计费缺少调用上界，暂不进入自动预算队列。手填服务价格与手选分组均未由服务端核验。"),
                Ui.Button("加入任务队列", () => _ = Submit(), true, "submit-task")),
            Ui.Card("任务队列", tasks, details,
                Ui.Actions(Ui.Button("停止所选任务", Stop, id: "stop-task"),
                    Ui.Button("核对账单后恢复", Resume, id: "resume-task"),
                    Ui.Button("打开产物目录", OpenArtifacts),
                    Ui.Button("网站账单 ↗", () => MainWindow.OpenUrl("https://mathgpt.xyz/usage-logs"))),
                result));
        Loaded += (_, _) => services.Queue.Changed += QueueChanged;
        Unloaded += (_, _) => services.Queue.Changed -= QueueChanged;
        Refresh();
    }
    private Task Submit() => window.Run(ct =>
    {
        if (!decimal.TryParse(budget.Text, out var yuan) || !int.TryParse(calls.Text, out var max) ||
            !int.TryParse(concurrency.Text, out var parallel) || !int.TryParse(outputTokens.Text, out var tokens))
            throw new UserException("请填写有效的预算、调用次数、并发与输出上限。");
        var assignments = roles.Select(row =>
        {
            var p = row.Profile.SelectedItem as ConnectionProfile ?? throw new UserException("每个角色都需要选择连接。");
            var model = row.Model.SelectedItem as ModelRoute ?? throw new UserException("请为每个角色读取并选择模型。");
            var protocol = row.Protocol.SelectedItem is WireProtocol wire ? wire : throw new UserException("请选择协议。");
            return new RoleAssignment { Role = row.Role.Text.Trim(), ProfileId = p.Id, Model = model.Id, Protocol = protocol,
                Reasoning = ReasoningPolicy.Allowed(model.Id).Contains(p.Reasoning) ? p.Reasoning : "", Quote = model };
        }).ToList();
        selected = services.Queue.Enqueue(prompt.Text, mode.SelectedIndex == 1 ? TaskMode.ParallelSynthesis : TaskMode.Sequential,
            assignments, yuan, parallel, max, tokens);
        Refresh(); return Task.CompletedTask;
    }, "任务已入队。每一步进度与产物会保存在下方。");
    private void QueueChanged() { _ = Dispatcher.InvokeAsync(Refresh); }
    private void Refresh()
    {
        var id = selected?.Id;
        tasks.ItemsSource = services.Queue.Tasks.AsEnumerable().Reverse().ToList();
        selected = services.Queue.Tasks.FirstOrDefault(t => t.Id == id) ?? services.Queue.Tasks.LastOrDefault();
        tasks.SelectedItem = selected; ShowDetails();
    }
    private void ShowDetails()
    {
        if (selected is null) { details.Text = "还没有任务。添加连接并选择三个角色的模型后即可开始。"; result.Text = ""; return; }
        var task = selected;
        details.Text = $"{WorkTask.StatusText(task.Status)} · 预留/估算 ¥{task.Committed:0.######} / ¥{task.Budget:0.##}\n{task.Error}\n" +
            string.Join("\n", task.Steps.Select((step, index) =>
                $"{index + 1}. {step.Assignment.Role} · {step.Assignment.Model} · {WorkTask.StatusText(step.Status)}" +
                (step.UsageKnown ? $" · 输入 {step.InputTokens} / 输出 {step.OutputTokens} Token · 估算 ¥{step.EstimatedCost:0.######}" : "") +
                (step.Error.Length > 0 ? "\n" + step.Error : "")));
        var text = new System.Text.StringBuilder();
        foreach (var step in task.Steps.Where(s => s.OutputFile.Length > 0))
        {
            try { text.AppendLine(File.ReadAllText(SafeFiles.Child(task.Directory, step.OutputFile))).AppendLine(); }
            catch (IOException) { text.AppendLine("此产物文件已被移动或暂时无法读取。"); }
            catch (UserException) { text.AppendLine("产物路径已变化，请从自己的任务目录核对。"); }
        }
        result.Text = text.ToString();
    }
    private void Stop()
    {
        if (selected is null) return;
        services.Queue.Stop(selected.Id); window.Status("已请求停止；已发出请求可能仍计费。"); Refresh();
    }
    private void Resume()
    {
        if (selected is null) return;
        if (!Ui.Confirm(window, "恢复将从未完成的角色重新调用，可能产生新费用。已完成产物会复用，未知请求的费用预留会保留。已核对网站账单并继续？", "恢复任务")) return;
        _ = window.Run(ct => { services.Queue.Resume(selected.Id); Refresh(); return Task.CompletedTask; }, "任务已重新入队。");
    }
    private void OpenArtifacts()
    {
        if (selected is null) return;
        _ = window.Run(ct => { MainWindow.OpenFolder(selected.Directory); return Task.CompletedTask; });
    }
}
