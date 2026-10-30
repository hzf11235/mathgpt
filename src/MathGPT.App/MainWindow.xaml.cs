using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Runtime.InteropServices;
using MathGPT.Core;
using MathGPT.App.Pages;

namespace MathGPT.App;

public partial class MainWindow : Window
{
    public AppServices Services { get; }
    public bool Busy { get; private set; }
    public string LastDiagnostic { get; private set; } = "暂无错误";
    private CancellationTokenSource? operation;
    private readonly List<Button> navButtons = [];
    private readonly int[] navTargets = [0, 5, 1, 3];
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly CancellationTokenSource updateLifetime = new();
    private SelfUpdateService? selfUpdater;
    private bool updateChecking;
    private bool updateClosed;
    private DateTimeOffset lastUpdateCheck;
    public string UpdateMessage { get; private set; } = "可手动检查更新，自动检查可在帮助中设置。";
    public event Action<string>? UpdateMessageChanged;
    public MainWindow(AppServices services)
    {
        Services = services; ThemeManager.Apply(services.Store.State); InitializeComponent();
        VersionLabel.Text = "MathGPT · " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.8.0");
        ThemeIcon.Content = Ui.Icon("palette", 17);
        UpdateAppearanceControls();
        SourceInitialized += (_, _) => ThemeManager.Caption(this, Services.Store.State.DarkMode);
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(500, area.Width - 20));
        MinHeight = Math.Min(MinHeight, Math.Max(400, area.Height - 20));
        Width = Math.Min(Width, area.Width - 20); Height = Math.Min(Height, area.Height - 20);
        var names = new[] { ("arrow", "安装配置"), ("link", "快速切换"), ("monitor", "账号"), ("settings", "帮助") };
        for (int i = 0; i < names.Length; i++)
        {
            var at = navTargets[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Icon(names[i].Item1, 16));
            var label = new TextBlock { Text = names[i].Item2, Margin = new(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
                { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            row.Children.Add(label);
            var button = new Button { Content = row, Style = (Style)FindResource("NavigationButton") };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "nav-" + i);
            System.Windows.Automation.AutomationProperties.SetName(button, names[i].Item2);
            button.ToolTip = names[i].Item2;
            button.Click += (_, _) => Navigate(at);
            navButtons.Add(button); Navigation.Children.Add(button);
        }
        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 1050;
            BrandName.Visibility = ActualWidth < 820 ? Visibility.Collapsed : Visibility.Visible;
            ThemeName.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            SiteButton.Content = compact ? "网站 ↗" : "打开网站 ↗";
        };
        Closing += (_, args) =>
        {
            if (!Busy && !Services.Queue.HasActive) return;
            if (MessageBox.Show(this, "仍有下载或任务进行。关闭后任务会保留为待恢复；已发送请求可能仍计费。确定关闭？",
                "退出 MathGPT", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) args.Cancel = true;
            else { operation?.Cancel(); foreach (var task in Services.Queue.Tasks.Where(t => t.Status is WorkStatus.Running or WorkStatus.Queued)) Services.Queue.Stop(task.Id); }
        };
        timer.Tick += async (_, _) =>
        {
            if (Services.Store.Network.Allows(AutomaticNetworkOperation.Updates) && DateTimeOffset.UtcNow - lastUpdateCheck > TimeSpan.FromHours(6)) _ = CheckForUpdates(false);
            if (!Services.Site.SignedIn || Busy) return;
            try
            {
                await Services.Store.Network.RunAutomatic(AutomaticNetworkOperation.AccountRefresh, async () =>
                {
                    await Services.Site.RefreshAccount();
                    if (PageHost.Content is ConnectionsPage connections) connections.UpdateAccountLabel();
                });
            }
            catch (Exception ex) { Status("账号同步暂停：" + Privacy.Friendly(ex), true); }
        };
        timer.Start();
        Closed += (_, _) =>
        {
            updateClosed = true; updateLifetime.Cancel(); timer.Stop();
            try
            {
                var apply = selfUpdater?.HasPending == true ? NewSelfUpdater().BuildApplyStartInfo(Environment.ProcessId) : null;
                if (apply is not null) Process.Start(apply)?.Dispose();
            }
            catch (Exception) { /* Keep the verified pending package for a later normal exit. */ }
            updateLifetime.Dispose();
        };
        Navigate(0);
    }
    public Task StartUpdateChecks() => Services.Store.Network.RunAutomatic(AutomaticNetworkOperation.Updates, () => CheckForUpdates(false));
    private SelfUpdateService NewSelfUpdater() => new(new SelfUpdateOptions
    {
        InstallationRoot = AppContext.BaseDirectory,
        DataRoot = Services.Store.Root,
        CurrentVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.7.0",
        Platform = "windows",
        Architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64",
        ProtectedDirectories = [Services.Store.State.WorkDirectory]
    });
    public async Task CheckForUpdates(bool showStatus = true)
    {
        if (!showStatus && !Services.Store.Network.Allows(AutomaticNetworkOperation.Updates)) return;
        if (updateChecking || updateClosed) return;
        updateChecking = true;
        lastUpdateCheck = DateTimeOffset.UtcNow;
        try
        {
            selfUpdater = NewSelfUpdater();
            var updater = selfUpdater;
            var token = updateLifetime.Token;
            var result = await Task.Run(() => updater.CheckAndStage(token), token);
            if (updateClosed) return;
            UpdateMessage = result.Message; UpdateMessageChanged?.Invoke(UpdateMessage);
            if (showStatus || selfUpdater.HasPending) Status(UpdateMessage);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (updateClosed) return;
            UpdateMessage = "暂时无法检查更新，现有版本可继续使用。";
            UpdateMessageChanged?.Invoke(UpdateMessage);
            if (showStatus) Status(UpdateMessage);
        }
        finally { updateChecking = false; }
    }
    public void Navigate(int index)
    {
        if (Busy) { Status("请等待当前操作完成，或先暂停。"); return; }
        for (int i = 0; i < navButtons.Count; i++)
        {
            var selected = navTargets[i] == index || index is 2 or 4 && navTargets[i] == 0;
            navButtons[i].Tag = selected ? "active" : "";
            navButtons[i].SetResourceReference(Control.ForegroundProperty, selected ? "Accent" : "Muted");
            navButtons[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        (string Title, string Subtitle, UserControl Page) view = index switch
        {
            1 => ("连接与模型", "登录 MathGPT，或连接你自己的兼容 API。", new ConnectionsPage(this)),
            2 => ("客户端", "安装、配置与打开，统一在这里完成。", new ClientsPage(this)),
            3 => ("设置与帮助", "目录、隐私与使用支持。", new SettingsPage(this)),
            4 => ("继续网页配置", "承接已选应用、模型与密钥。", new DesktopSetupPage(this)),
            5 => ("快速切换", "选择连接，打开客户端。", new QuickConnectionPanel(this)),
            _ => ("安装配置", "", new SetupWizardPage(this))
        };
        PageTitle.Text = view.Title; PageSubtitle.Text = view.Subtitle; PageHost.Content = view.Page;
        HeadingRegion.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        PageScroll.Margin = index == 0 ? new(0, 35, 0, 0) : new(0);
        PageScroll.ScrollToTop();
    }
    public void Status(string message, bool error = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => Status(message, error)); return; }
        OperationText.Text = message;
        if (error) OperationText.Foreground = ThemeManager.Brush(Services.Store.State.DarkMode ? "#F19BA4" : "#B53F48");
        else OperationText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }
    public async Task Run(Func<CancellationToken, Task> action, string success = "", bool cancellable = false)
    {
        if (Busy) return;
        Busy = true; operation = new();
        PageHost.IsEnabled = false; OperationProgress.Visibility = Visibility.Visible;
        OperationProgress.IsIndeterminate = true;
        CancelOperation.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        Status("正在处理…");
        try
        {
            await action(operation.Token);
            if (success.Length > 0) Status(success);
        }
        catch (Exception ex)
        {
            LastDiagnostic = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {ex.GetType().Name} · {Privacy.Friendly(ex)}";
            Status(Privacy.Friendly(ex), true);
        }
        finally
        {
            Busy = false; operation.Dispose(); operation = null;
            PageHost.IsEnabled = true; OperationProgress.Visibility = Visibility.Collapsed; CancelOperation.Visibility = Visibility.Collapsed;
        }
    }
    public IProgress<TransferProgress> TransferProgress() => new Progress<TransferProgress>(p =>
    {
        OperationProgress.IsIndeterminate = p.Total == 0 && p.Percent == 0;
        OperationProgress.Value = p.Percent;
        Status(p.Message + (p.Total > 0 ? $" · {p.Percent:0}% · {p.Received / 1048576d:0.0} / {p.Total / 1048576d:0.0} MB" : ""));
    });
    public Task LaunchClient(ClientKind kind, string profileId) => Run(async ct =>
    {
        if (kind == ClientKind.ClaudeDesktop) await new ClientsPage(this).EnsureClient(kind, ct);
        var installation = Services.Store.State.Clients.FirstOrDefault(c => c.Kind == kind)
            ?? throw new UserException("客户端未安装。");
        var profile = Services.Store.State.Profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new UserException("连接已被移除。");
        var routes = await Services.Catalog.Load(profile, ct);
        var route = routes.FirstOrDefault(r => r.Id == profile.Model && ClientService.Supports(kind, r))
            ?? throw new UserException("所选模型当前不可用。");
        Services.Clients.Configure(installation, profile, route, routes, App.Executable); Services.Clients.Launch(installation, profile);
    }, "客户端启动请求已发送。", true);
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !new[] { "https", "mailto" }.Contains(uri.Scheme)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }
    public static void OpenFolder(string path)
    {
        SafeFiles.NoLinks(path);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(path)); Process.Start(start)?.Dispose();
    }
    private void OpenSite(object sender, RoutedEventArgs e) => OpenUrl("https://mathgpt.xyz");
    private void UpdateAppearanceControls()
    {
        ThemeName.Text = ThemeManager.Name(Services.Store.State.Appearance);
        ModeButton.Content = Ui.Icon(Services.Store.State.DarkMode ? "moon" : "sun", 18);
    }
    internal void PreviewAppearance(string id, bool dark)
    {
        Services.Store.State.Appearance = id; Services.Store.State.DarkMode = dark;
        ThemeManager.Apply(Services.Store.State); UpdateAppearanceControls();
    }
    public Task SetAppearance(string id) => Run(ct =>
    {
        if (!ThemeManager.Themes.Any(t => t.Id == id)) return Task.CompletedTask;
        Services.Store.State.Appearance = id; Services.Store.Save();
        ThemeManager.Apply(Services.Store.State); UpdateAppearanceControls();
        return Task.CompletedTask;
    }, "已切换为" + ThemeManager.Name(id) + "风格。");
    private void ChooseAppearance(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var theme in ThemeManager.Themes)
        {
            var title = Ui.Stack(Ui.Text(theme.Name, 13), Ui.Hint(theme.Description));
            var item = new MenuItem { Header = title, IsChecked = Services.Store.State.Appearance == theme.Id, MinWidth = 205 };
            item.Click += (_, _) => _ = SetAppearance(theme.Id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void ToggleMode(object sender, RoutedEventArgs e) => _ = Run(ct =>
    {
        Services.Store.State.DarkMode = !Services.Store.State.DarkMode; Services.Store.Save();
        ThemeManager.Apply(Services.Store.State); UpdateAppearanceControls(); return Task.CompletedTask;
    }, "明暗模式已切换。");
    private void CancelCurrent(object sender, RoutedEventArgs e) { operation?.Cancel(); Status("正在停止；官方安装器如已启动，将等待它安全结束。"); }
}
