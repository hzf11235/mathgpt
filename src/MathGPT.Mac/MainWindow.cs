using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MathGPT.Core;

[assembly: SupportedOSPlatform("macos")]

namespace MathGPT.Mac;

public sealed class MainWindow : Window
{
    private readonly StateStore store;
    private readonly WebsiteClient site;
    private readonly MacClients clients;
    private readonly ComboBox profiles = new() { MinWidth = 240 };
    private readonly TextBox name = new() { PlaceholderText = "连接名称", Text = "MathGPT" };
    private readonly TextBox address = new() { Text = "https://mathgpt.xyz/v1" };
    private readonly TextBox secret = new() { PlaceholderText = "粘贴 API Key", PasswordChar = '●' };
    private readonly ComboBox models = new() { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox reasoning = new() { MinWidth = 140 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5B6380") };
    private readonly TextBlock account = new() { Text = "登录后读取已有密钥。", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox savedAccounts = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel loginInputs;
    private readonly StackPanel verificationInputs;
    private readonly StackPanel loginPanel;
    private readonly WrapPanel savedAccountRow;
    private readonly StackPanel accountKeys;
    private readonly Button cancelAccount;
    private bool addingAccount;
    private readonly ComboBox keys = new() { MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox username = new() { PlaceholderText = "MathGPT 账号" };
    private readonly TextBox password = new() { PlaceholderText = "密码", PasswordChar = '●' };
    private readonly TextBox entry = new() { PlaceholderText = "入口密钥", PasswordChar = '●' };
    private readonly TextBox verification = new() { PlaceholderText = "二步验证码（如需要）" };
    private readonly CheckBox consent = new() { Content = "同意服务条款与隐私政策" };
    private readonly TextBlock directory = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox preferredClient = new() { MinWidth = 200 };
    private readonly MacSetupPanel setup;
    private readonly TabControl tabs = new();
    private readonly StackPanel quick;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? catalogLoad;
    private CancellationTokenSource? clientDownload;
    private readonly CancellationTokenSource updateLifetime = new();
    private readonly TextBlock updateStatus = new() { Text = "启动时自动检查，正常关闭后完成更新。", TextWrapping = TextWrapping.Wrap };
    private readonly Avalonia.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private SelfUpdateService? selfUpdater;
    private bool checkingUpdate;
    private readonly Button pauseDownload = new() { Content = "暂停下载", IsVisible = false, Padding = new Thickness(16, 9),
        CornerRadius = new CornerRadius(9), Background = Brushes.White, Foreground = Brush.Parse("#254A9C"),
        BorderBrush = Brush.Parse("#DEE3F1"), BorderThickness = new Thickness(1) };
    private bool populating;
    private bool loadingCatalog;
    private bool closed;
    private int selectionGeneration;
    private List<ModelRoute> routes = [];
    private ConnectionProfile profile;
    private LoginChallenge? challenge;
    private bool busy;
    private readonly List<Button> actions = [];
    public MainWindow() : this(new StateStore(Program.Root), null) { }
    internal MainWindow(StateStore state, WebsiteClient? websiteClient)
    {
        store = state;
        site = websiteClient ?? new WebsiteClient(store); clients = new MacClients(store);
        profile = store.State.Profiles.FirstOrDefault(x => x.Id == store.State.ActiveProfileId) ?? new ConnectionProfile();
        if (!Directory.Exists(store.State.WorkDirectory)) Directory.CreateDirectory(store.State.WorkDirectory);
        Title = "MathGPT 安装助手"; Width = 1040; Height = 780; MinWidth = 740; MinHeight = 600; Background = Brush.Parse("#F6F7FC");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        var brand = new StackPanel { Spacing = 5 }; brand.Children.Add(new TextBlock { Text = "MathGPT 安装助手", FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#244EBC") }); brand.Children.Add(new TextBlock { Text = "安装、配置、打开", Foreground = Brush.Parse("#77819A") }); header.Children.Add(brand);
        var website = Action("打开官网", () => { Open("https://mathgpt.xyz"); return Task.CompletedTask; }); Grid.SetColumn(website, 1); header.Children.Add(website);
        setup = new MacSetupPanel(store, AcceptWebsiteSetup, () => StartClient(SelectedClient(), true), SetBusy, Open);
        preferredClient.ItemsSource = new[] { ClientKind.CodexDesktop, ClientKind.ClaudeDesktop, ClientKind.Harness };
        preferredClient.SelectedItem = store.State.PreferredClient;
        preferredClient.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ClientKind>((kind, _) => new TextBlock { Text = ClientService.DisplayName(kind) });
        var manual = Stack(Action("新建连接", () => { CancelCatalog(); profile = new(); routes = []; Fill(); return Task.CompletedTask; }), Label("连接名称"), name, Label("API 地址"), Row(address, Action("复制地址", () => MacClients.Copy(address.Text ?? ""))), Label("API Key"), secret, Row(Action("保存并读取模型", Load), Action("前往创建密钥", () => { Open("https://mathgpt.xyz/keys"); return Task.CompletedTask; })));
        quick = Stack(Row(Stack(Label("连接"), profiles), Stack(Label("打开应用"), preferredClient)),
            Row(Stack(Label("模型"), models), Stack(Label("推理强度"), reasoning)),
            Action("切换并打开", () => StartClient(SelectedClient(), false)));
        var connection = Stack(setup, quick,
            new Expander { Header = "手动接入与更多设置", Content = manual });
        var installs = Stack(Action("安装 MathGPT 助手", async () => { await clients.InstallSelf(); Report("已安装助手并创建桌面入口。"); }));
        foreach (var kind in new[] { ClientKind.CodexDesktop, ClientKind.ClaudeDesktop, ClientKind.Harness })
        {
            var heading = MacClients.Names(kind)[0];
            var card = Stack(new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeight.SemiBold },
                Action("安装并接入", () => StartClient(kind, true)),
                Row(Action("下载安装", async () => { await InstallClient(kind); Report("已安装。选择模型后点击配置连接。"); }),
                    Action("检测已有安装", async () => Report(await clients.Detect(kind) is null ? "尚未找到应用，请先下载安装。" : "已找到应用，可配置连接。")),
                    Action("配置连接", async () => { await EnsureRoutes(); var install = await clients.Detect(kind) ?? throw new UserException("请先下载或检测客户端。"); SelectModel(); await clients.Configure(install, profile, routes); Report("配置已准备，正常退出客户端后点击打开。已有桌面入口会保留。"); }),
                    Action("打开", () => StartClient(kind, false))));
            installs.Children.Add(Card(card));
            if (DesktopConfiguration.IsDesktop(kind)) card.Children.Add(Action("撤销本应用的连接配置", async () => { await clients.Undo(kind); Report("已恢复配置前的内容。用户后来修改的文件会保留。"); }));
        }
        installs.Children.Add(Label("下载目录")); installs.Children.Add(directory); installs.Children.Add(Action("更改下载目录", ChooseDirectory));
        installs.Children.Add(Action("运行本机自检", async () => { var report = await MacSelfCheck.Run(store.Root); Report(report.Summary); Open(report.Path); }));
        installs.Children.Add(Action("Mac 使用说明", () => { Open("https://mathgpt.xyz/docs/index.html#mac"); return Task.CompletedTask; }));
        installs.Children.Add(Card(Stack(Label("软件更新"), updateStatus, Action("检查 MathGPT 更新", () => CheckForUpdates(true)))));
        loginInputs = Stack(Label("登录账号"), username, password, entry, consent,
            Row(Action("登录", Login), Action("服务条款", () => { Open("https://mathgpt.xyz/user-agreement"); return Task.CompletedTask; }), Action("隐私政策", () => { Open("https://mathgpt.xyz/privacy-policy"); return Task.CompletedTask; })));
        verificationInputs = Stack(verification, Action("验证并登录", Verify));
        cancelAccount = Action("取消添加", CancelAccountLogin);
        loginPanel = Stack(loginInputs, verificationInputs, cancelAccount);
        savedAccountRow = Row(savedAccounts, Action("切换", SwitchAccount), Action("添加账号", ShowAccountLogin));
        accountKeys = Stack(Label("已有 API 密钥"), keys,
            Row(Action("刷新密钥", RefreshAccount), Action("导入所选密钥", ImportKey), Action("退出当前账号", LogoutAccount)));
        var login = Stack(account, savedAccountRow, loginPanel, accountKeys);
        RefreshAccountControls();
        tabs.ItemsSource = new[] { Tab("配置", connection), Tab("安装", installs), Tab("账号", login) };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(30) }; grid.Children.Add(header); Grid.SetRow(tabs, 1); grid.Children.Add(tabs);
        pauseDownload.Click += (_, _) => clientDownload?.Cancel();
        var transferStatus = Stack(status, pauseDownload);
        transferStatus.Margin = new Thickness(0, 18, 0, 0); Grid.SetRow(transferStatus, 2); grid.Children.Add(transferStatus); Content = grid;
        profiles.SelectionChanged += async (_, _) =>
        {
            if (populating || busy || profiles.SelectedItem is not ConnectionProfile selected || selected.Id == profile.Id) return;
            profile = selected; routes = []; Fill(false);
            await LoadSelected(selected);
        };
        models.SelectionChanged += (_, _) => { if (models.SelectedItem is string model) { reasoning.ItemsSource = new[] { "服务默认" }.Concat(ReasoningPolicy.Allowed(model)).ToArray(); reasoning.SelectedItem = ReasoningPolicy.Allowed(model).Contains(profile.Reasoning) ? profile.Reasoning : "服务默认"; } };
        Opened += async (_, _) =>
        {
            Fill(); var installedSelf = false;
            try
            {
                if (OperatingSystem.IsMacOS()) { await clients.InstallSelf(); installedSelf = true; }
                if (site.SignedIn) await RefreshAccount();
                else RefreshAccountControls();
                if (store.Key(profile) is not null) await LoadSelected(profile);
            }
            catch (Exception e) { Report(Privacy.Friendly(e)); }
            if (installedSelf) { _ = CheckForUpdates(false); updateTimer.Start(); }
        };
        updateTimer.Tick += (_, _) => _ = CheckForUpdates(false);
        Closed += (_, _) =>
        {
            closed = true; updateLifetime.Cancel(); updateTimer.Stop();
            try
            {
                var apply = selfUpdater?.HasPending == true ? NewSelfUpdater().BuildApplyStartInfo(Environment.ProcessId) : null;
                if (apply is not null) Process.Start(apply)?.Dispose();
            }
            catch (Exception) { /* A verified pending update is retained for a later normal exit. */ }
            lifetime.Cancel(); CancelCatalog(); setup.Dispose(); site.CancelAccountLogin(); site.Dispose(); lifetime.Dispose(); updateLifetime.Dispose();
        };
    }
    private SelfUpdateService NewSelfUpdater() => new(new SelfUpdateOptions
    {
        InstallationRoot = clients.SelfBundle,
        DataRoot = store.Root,
        CurrentVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.7.0",
        Platform = "mac",
        Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64",
        ProtectedDirectories = [store.State.WorkDirectory]
    });
    private async Task CheckForUpdates(bool showStatus)
    {
        if (checkingUpdate || closed) return;
        checkingUpdate = true;
        try
        {
            selfUpdater = NewSelfUpdater();
            var updater = selfUpdater;
            var token = updateLifetime.Token;
            var result = await Task.Run(() => updater.CheckAndStage(token), token);
            if (closed) return;
            updateStatus.Text = result.Message;
            if (showStatus || selfUpdater.HasPending) Report(result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (closed) return;
            updateStatus.Text = "暂时无法检查更新，现有版本可继续使用。";
            if (showStatus) Report(updateStatus.Text);
        }
        finally { checkingUpdate = false; }
    }
    private static TextBlock Label(string value) => new() { Text = value, FontWeight = FontWeight.Medium, Margin = new Thickness(0, 5, 0, 0) };
    private static StackPanel Stack(params Control[] children) { var panel = new StackPanel { Spacing = 12 }; foreach (var child in children) panel.Children.Add(child); return panel; }
    private static WrapPanel Row(params Control[] children) { var panel = new WrapPanel(); foreach (var child in children) { child.Margin = new Thickness(0, 0, 10, 8); panel.Children.Add(child); } return panel; }
    private static Border Card(Control child) => new() { Child = child, Padding = new Thickness(22), Margin = new Thickness(0, 5), Background = Brushes.White, BorderBrush = Brush.Parse("#DEE3F1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16) };
    private static TabItem Tab(string name, Control child) => new() { Header = name, Content = new ScrollViewer { Content = Card(child), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
    private Button Action(string text, Func<Task> work)
    {
        var primary=text.StartsWith("保存") || text is "配置连接" or "安装并接入" or "切换并打开";
        var button = new Button { Content = text, Padding = new Thickness(16, 9), CornerRadius = new CornerRadius(9), Background=primary?Brush.Parse("#3465D9"):Brushes.White, Foreground=primary?Brushes.White:Brush.Parse("#254A9C"), BorderBrush=Brush.Parse("#DEE3F1"), BorderThickness=new Thickness(1) }; actions.Add(button);
        button.Click += async (_, _) => { if (busy || loadingCatalog || closed) return; SetBusy(true); try { await work(); } catch (OperationCanceledException) { if (!closed) Report("已取消。"); } catch (Exception error) { Report(Privacy.Friendly(error)); } finally { if (!closed) SetBusy(false); } }; return button;
    }
    private void Fill(bool list = true)
    {
        populating = true;
        if (list) { profiles.ItemsSource = store.State.Profiles.ToArray(); profiles.SelectedItem = profile; }
        name.Text = profile.Name; address.Text = profile.BaseUrl; secret.Text = ""; directory.Text = store.State.DownloadDirectory;
        models.ItemsSource = routes.Where(x => x.Available).Select(x => x.Id).Distinct().ToArray(); models.SelectedItem = profile.Model;
        if (routes.Count == 0) { reasoning.ItemsSource = new[] { "服务默认" }; reasoning.SelectedIndex = 0; }
        preferredClient.SelectedItem = store.State.PreferredClient;
        quick.IsVisible = store.State.Profiles.Count > 0;
        populating = false;
    }
    private async Task Load()
    {
        var token = lifetime.Token;
        CancelCatalog();
        var selected = profile;
        selected.Name = name.Text ?? "MathGPT"; selected.BaseUrl = address.Text ?? ""; store.SaveProfile(selected, string.IsNullOrWhiteSpace(secret.Text) ? null : secret.Text);
        secret.Text = "";
        var loaded = await new CatalogService(store, site).Load(selected, token);
        token.ThrowIfCancellationRequested();
        if (closed || selected.Id != profile.Id) return;
        routes = loaded; store.State.ActiveProfileId = profile.Id; store.Save();
        if (!routes.Any(x => x.Available && x.Id == profile.Model)) profile.Model = routes.FirstOrDefault(x => x.Available)?.Id ?? "";
        store.Save();
        Fill(); Report(routes.Count(x => x.Available) + " 个可用模型。选择后进入客户端配置。");
    }
    private async Task EnsureRoutes() { if (routes.Count == 0) await Load(); }
    private void SelectModel()
    {
        profile.Model = models.SelectedItem as string ?? throw new UserException("请先读取并选择可用模型。"); var route = routes.First(x => x.Id == profile.Model && x.Available);
        profile.Protocol = ModelCatalogScope.Protocol(route, profile.Protocol); profile.Reasoning = reasoning.SelectedItem as string is { } effort && effort != "服务默认" ? effort : ""; store.SaveProfile(profile, null);
    }
    private void SetBusy(bool value) { busy = value; SetControlsEnabled(); }
    private void SetControlsEnabled()
    {
        foreach (var action in actions) action.IsEnabled = !busy && !loadingCatalog;
        profiles.IsEnabled = !busy; models.IsEnabled = !busy && !loadingCatalog;
        reasoning.IsEnabled = !busy && !loadingCatalog; preferredClient.IsEnabled = !busy && !loadingCatalog;
        name.IsEnabled = !busy && !loadingCatalog; address.IsEnabled = !busy && !loadingCatalog;
        secret.IsEnabled = !busy && !loadingCatalog;
        savedAccounts.IsEnabled = !busy && !loadingCatalog && challenge is null;
        username.IsEnabled = !busy && challenge is null; password.IsEnabled = !busy && challenge is null;
        entry.IsEnabled = !busy && challenge is null; verification.IsEnabled = !busy;
        setup.SetApplicationBusy(busy || loadingCatalog);
    }
    private void CancelCatalog() { selectionGeneration++; catalogLoad?.Cancel(); catalogLoad?.Dispose(); catalogLoad = null; loadingCatalog = false; }
    private async Task LoadSelected(ConnectionProfile selected)
    {
        CancelCatalog();
        var generation = selectionGeneration;
        catalogLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = catalogLoad.Token;
        loadingCatalog = true; SetControlsEnabled();
        try
        {
            if (store.Key(selected) is null) { routes = []; Fill(false); Report("此连接尚未保存密钥，可展开手动设置或继续网页配置。"); return; }
            var loaded = await new CatalogService(store, site).Load(selected, token);
            token.ThrowIfCancellationRequested();
            if (closed || generation != selectionGeneration || profile.Id != selected.Id) return;
            routes = loaded; store.State.ActiveProfileId = selected.Id;
            if (selected.Model.Length == 0) selected.Model = routes.FirstOrDefault(x => x.Available)?.Id ?? "";
            store.Save(); Fill(false); Report("已切换连接，模型目录已更新。选择模型后点“切换并打开”。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && generation == selectionGeneration) Report(Privacy.Friendly(error)); }
        finally { if (!closed && generation == selectionGeneration) { loadingCatalog = false; SetControlsEnabled(); } }
    }
    private async Task AcceptWebsiteSetup(DesktopSetupGrant grant, CancellationToken ct)
    {
        CancelCatalog();
        var imported = await DesktopSetupImporter.Import(store, new CatalogService(store, site), grant, ct);
        ct.ThrowIfCancellationRequested(); if (closed) return;
        profile = store.State.Profiles.Single(p => p.Id == imported.ProfileId);
        routes = await new CatalogService(store, site).Load(profile, ct);
        ct.ThrowIfCancellationRequested(); if (closed) return;
        Fill(); setup.ShowReady(imported.Client, imported.Model);
        Report("网页选择已接入，点击“安装并开始”即可继续。");
    }
    private ClientKind SelectedClient() => preferredClient.SelectedItem is ClientKind kind ? kind : store.State.PreferredClient;
    private async Task StartClient(ClientKind kind, bool allowInstall)
    {
        var token = lifetime.Token;
        await EnsureRoutes(); SelectModel();
        token.ThrowIfCancellationRequested();
        var install = await clients.Detect(kind);
        token.ThrowIfCancellationRequested();
        if (install is null)
        {
            if (!allowInstall) throw new UserException("尚未安装此应用，请到客户端页点击安装并接入。");
            install = await InstallClient(kind);
            token.ThrowIfCancellationRequested();
        }
        await clients.Configure(install, profile, routes);
        token.ThrowIfCancellationRequested();
        await clients.Launch(kind, profile);
        store.State.PreferredClient = kind; store.Save(); preferredClient.SelectedItem = kind;
        Report("已应用当前密钥和模型，并打开 " + MacClients.Names(kind)[0] + "。");
    }
    private async Task<ClientInstallation> InstallClient(ClientKind kind)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        clientDownload = download; pauseDownload.IsVisible = true;
        try { return await clients.Install(kind, Report, download.Token); }
        catch (OperationCanceledException)
        { if (closed) throw; throw new UserException("下载已暂停，已保留断点。再次安装可继续下载。"); }
        finally { clientDownload = null; if (!closed) pauseDownload.IsVisible = false; }
    }
    private async Task Login()
    {
        if (challenge is not null) throw new UserException("请完成或取消当前验证。");
        if (consent.IsChecked != true) throw new UserException("请阅读并同意服务条款与隐私政策。");
        if (!string.IsNullOrEmpty(entry.Text)) await site.UnlockEntry(entry.Text, lifetime.Token);
        try { challenge = await site.Login(username.Text ?? "", password.Text ?? "", lifetime.Token); }
        finally { password.Text = ""; entry.Text = ""; }
        if (challenge is null) { addingAccount = false; await RefreshAccount(); Report("已登录。"); }
        else { RefreshAccountControls(); Report("输入二步验证码，或取消返回原账号。"); }
    }
    private async Task Verify()
    {
        if (challenge is null) throw new UserException("请先登录。");
        await site.VerifyLogin(challenge.FlowToken, verification.Text ?? "", lifetime.Token);
        verification.Text = ""; challenge = null; addingAccount = false; await RefreshAccount(); Report("已登录。");
    }
    private Task ShowAccountLogin()
    {
        if (challenge is not null) throw new UserException("请先完成或取消当前验证。");
        addingAccount = true; username.Text = ""; password.Text = ""; verification.Text = "";
        RefreshAccountControls(); return Task.CompletedTask;
    }
    private Task CancelAccountLogin()
    {
        site.CancelAccountLogin(); challenge = null; addingAccount = false;
        password.Text = ""; verification.Text = ""; entry.Text = "";
        RefreshAccountControls(); Report(site.SignedIn ? "已返回原账号。" : "已取消添加。");
        return Task.CompletedTask;
    }
    private async Task SwitchAccount()
    {
        if (challenge is not null) throw new UserException("请先完成或取消当前验证。");
        var selected = savedAccounts.SelectedItem as SavedWebsiteAccount ?? throw new UserException("请选择已登录账号。");
        if (selected.Current) return;
        await site.SwitchAccount(selected.Id, lifetime.Token);
        addingAccount = false; await RefreshAccount(); Report("已切换账号。");
    }
    private async Task LogoutAccount()
    {
        try { await site.Logout(lifetime.Token); }
        finally { challenge = null; addingAccount = false; keys.ItemsSource = null; RefreshAccountControls(); }
        Report("已退出当前账号，其他账号仍保留。");
    }
    private void RefreshAccountControls()
    {
        if (closed) return;
        account.Text = site.Account is { } current ? current.Name + " · 余额 ¥" + current.Balance.ToString("0.####") : "登录后读取已有密钥。";
        var saved = site.SavedAccounts;
        savedAccounts.ItemsSource = saved;
        savedAccounts.SelectedItem = saved.FirstOrDefault(item => item.Current);
        savedAccountRow.IsVisible = saved.Count > 0;
        cancelAccount.IsVisible = addingAccount || challenge is not null;
        accountKeys.IsVisible = site.SignedIn;
        if (!site.SignedIn) keys.ItemsSource = null;
        loginPanel.IsVisible = !site.SignedIn || addingAccount || challenge is not null;
        loginInputs.IsVisible = challenge is null;
        verificationInputs.IsVisible = challenge is not null;
    }
    private async Task RefreshAccount()
    {
        await site.RefreshAccount(lifetime.Token); var listed = await site.Keys(lifetime.Token);
        if (closed) return;
        keys.ItemsSource = listed; RefreshAccountControls();
    }
    private async Task ImportKey() { var selected = keys.SelectedItem as WebsiteKey ?? throw new UserException("请选择密钥。"); if (!selected.Usable) throw new UserException("所选密钥暂不可用。"); profile = new() { Name = selected.Name, Group = selected.Group, WebsiteKeyId = selected.Id }; store.SaveProfile(profile, await site.KeySecret(selected.Id)); routes = []; Fill(); await Load(); Report("密钥已安全导入钥匙串，选择模型后即可配置客户端。"); }
    private async Task ChooseDirectory() { var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择下载目录", AllowMultiple = false }); if (chosen.Count == 0) return; var path = chosen[0].TryGetLocalPath(); if (path is null) return; SafeFiles.Writable(path); store.State.DownloadDirectory = path; store.Save(); directory.Text = path; }
    private void Report(string message) { if (!closed) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!closed) status.Text = message; }); }
    private static void Open(string url) => Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { url }, UseShellExecute = false })?.Dispose();
}
