using System.Windows;
using System.Windows.Controls;
using MathGPT.Core;

namespace MathGPT.App.Pages;

public sealed class QuickConnectionPanel : UserControl
{
    private readonly MainWindow window;
    private readonly ComboBox connections = Ui.Select("quick-connection");
    private readonly ComboBox models = Ui.Select("quick-model");
    private readonly ComboBox efforts = Ui.Select("quick-effort");
    private readonly ComboBox apps = Ui.Select("quick-app");
    private readonly TextBlock hint = Ui.Hint("");
    private readonly Button apply;
    private CancellationTokenSource? loading;
    private string catalogProfileId = "";
    private int generation;
    private bool populating;
    public QuickConnectionPanel(MainWindow window)
    {
        this.window = window;
        apply = Ui.Button("切换并打开", () => _ = Apply(), true, "quick-apply");
        if (window.Services.Store.State.Profiles.Count == 0)
        {
            Content = Ui.Card("先接上你的配置", Ui.Hint("网页选好的应用、模型和密钥，可以直接接着用。"),
                Ui.Actions(Ui.Button("继续网页配置", () => window.Navigate(4), true)));
            return;
        }
        connections.MinWidth = 180; models.MinWidth = 210; efforts.MinWidth = 100; apps.MinWidth = 155;
        connections.MaxWidth = 260; models.MaxWidth = 300; efforts.MaxWidth = 180; apps.MaxWidth = 230;
        foreach (var kind in new[] { ClientKind.CodexDesktop, ClientKind.ClaudeDesktop, ClientKind.Harness })
            apps.Items.Add(new ComboBoxItem { Content = ClientService.DisplayName(kind), Tag = kind });
        apps.SelectedItem = apps.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, window.Services.Store.State.PreferredClient)) ?? apps.Items[0];
        connections.ItemsSource = window.Services.Store.State.Profiles.ToArray();
        connections.SelectedItem = window.Services.Store.State.Profiles.FirstOrDefault(p => p.Id == window.Services.Store.State.ActiveProfileId);
        var fields = Ui.Actions(Ui.Field("连接 / 密钥", connections), Ui.Field("模型", models), Ui.Field("推理强度", efforts), Ui.Field("应用", apps));
        foreach (FrameworkElement field in fields.Children) field.Margin = new(0, 0, 12, 0);
        Content = Ui.Card("快速切换", fields,
            Ui.Actions(apply, Ui.Button("创建密钥", () => QuickKeyDialog.Show(window, true)),
                Ui.Button("接入已有密钥", () => QuickKeyDialog.Show(window, false)),
                Ui.Button("刷新模型", () => _ = Refresh(), id: "quick-refresh")), hint);
        connections.SelectionChanged += (_, _) => { if (!populating) _ = Refresh(); };
        models.SelectionChanged += (_, _) =>
        {
            if (models.SelectedItem is not ModelRoute route)
            {
                efforts.ItemsSource = null; efforts.SelectedItem = null;
                efforts.IsEnabled = false; apply.IsEnabled = false;
                return;
            }
            var old = efforts.SelectedItem as string;
            efforts.ItemsSource = new[] { "服务默认" }.Concat(ReasoningPolicy.Allowed(route.Id));
            var selected = connections.SelectedItem as ConnectionProfile;
            var desired = populating ? selected?.Reasoning ?? "" : old ?? selected?.Reasoning ?? "";
            efforts.SelectedItem = ReasoningPolicy.Allowed(route.Id).Contains(desired) ? desired : "服务默认";
            efforts.IsEnabled = loading is null && selected?.Id == catalogProfileId;
            apply.IsEnabled = loading is null && route.Available && selected?.Id == catalogProfileId;
        };
        Loaded += (_, _) => { if (!Environment.GetCommandLineArgs().Contains("--ui-smoke")) _ = Refresh(); };
        Unloaded += (_, _) => { generation++; loading?.Cancel(); };
        apply.IsEnabled = false;
    }
    private async Task Refresh()
    {
        var current = ++generation;
        loading?.Cancel();
        var operation = new CancellationTokenSource(); loading = operation;
        ClearCatalog();
        if (connections.SelectedItem is not ConnectionProfile profile) { hint.Text = "先接入一个密钥。"; loading = null; operation.Dispose(); return; }
        hint.Text = "正在读取此密钥的全部可用模型…";
        try
        {
            var routes = await window.Services.Catalog.Load(profile, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (generation != current) return;
            var items = routes.Where(r => r.Available).GroupBy(r => r.Id).Select(g => g.First()).ToList();
            catalogProfileId = profile.Id;
            populating = true;
            try
            {
                models.ItemsSource = items;
                models.SelectedItem = items.FirstOrDefault(r => r.Id == profile.Model) ?? (profile.Model.Length == 0 ? items.FirstOrDefault() : null);
            }
            finally { populating = false; }
            window.Services.Store.State.ActiveProfileId = profile.Id; window.Services.Store.Save();
            hint.Text = models.SelectedItem is null ? "原模型暂不可用，请从列表选择其他模型。" :
                profile.Group == "universal" ? $"万能密钥 · {items.Count} 个模型，切换连接不会重新安装应用。" : $"此密钥 · {items.Count} 个模型，切换连接不会重新安装应用。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { if (generation == current) ClearCatalog(); }
        catch (Exception error) { if (generation == current) { ClearCatalog(); hint.Text = Privacy.Friendly(error); } }
        finally
        {
            if (generation == current)
            {
                loading = null;
                models.IsEnabled = catalogProfileId.Length > 0 && models.Items.Count > 0;
                efforts.IsEnabled = models.IsEnabled && models.SelectedItem is ModelRoute;
                apply.IsEnabled = models.IsEnabled && models.SelectedItem is ModelRoute { Available: true } &&
                    connections.SelectedItem is ConnectionProfile selected && selected.Id == catalogProfileId;
            }
            operation.Dispose();
        }
    }
    private void ClearCatalog()
    {
        catalogProfileId = "";
        models.ItemsSource = null; models.SelectedItem = null;
        efforts.ItemsSource = null; efforts.SelectedItem = null;
        models.IsEnabled = false; efforts.IsEnabled = false; apply.IsEnabled = false;
    }
    private async Task Apply()
    {
        if (window.Busy || loading is not null || connections.SelectedItem is not ConnectionProfile profile || profile.Id != catalogProfileId ||
            models.SelectedItem is not ModelRoute { Available: true } route || !models.Items.Contains(route) ||
            apps.SelectedItem is not ComboBoxItem { Tag: ClientKind kind }) return;
        profile.Model = route.Id;
        profile.Reasoning = efforts.SelectedItem as string is { } effort && effort != "服务默认" ? effort : "";
        profile.Protocol = ModelCatalogScope.Protocol(route, profile.Protocol);
        window.Services.Store.SaveProfile(profile, null);
        window.Services.Store.State.PreferredClient = kind; window.Services.Store.Save();
        await new ClientsPage(window).StartClient(kind);
    }
}
