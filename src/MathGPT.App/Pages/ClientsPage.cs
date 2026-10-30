using System.Windows;
using System.Windows.Controls;
using MathGPT.Core;
using Microsoft.Win32;

namespace MathGPT.App.Pages;

public sealed class ClientsPage : UserControl
{
    private readonly MainWindow window;
    private readonly AppServices services;
    private readonly WrapPanel cards = new() { ItemWidth = 340, Margin = new(0, 0, -14, 0) };
    private readonly StackPanel advanced = new();
    private readonly TextBox downloads;
    private readonly TextBox installs;
    private readonly TextBox workspace;
    public ClientsPage(MainWindow window)
    {
        this.window = window; services = window.Services;
        downloads = Ui.Input("download-directory", services.Store.State.DownloadDirectory);
        installs = Ui.Input("install-directory", services.Store.State.InstallDirectory);
        workspace = Ui.Input("work-directory", services.Store.State.WorkDirectory);
        Content = Ui.Stack(new QuickConnectionPanel(window), new Expander { Header = "安装与工作位置 · 点击选择目录", Margin = new(0, 0, 0, 16), Content = Ui.Card("",
            Ui.PathField("下载目录", downloads, "选择客户端下载目录"),
            Ui.PathField("客户端安装目录", installs, "选择客户端安装目录"),
            Ui.PathField("默认工作目录", workspace, "选择工作目录"),
            Ui.Hint("Harness 可自选安装位置。Codex 与 Claude Code 桌面版由 Windows 管理安装位置。"),
            Ui.Button("保存目录", () => _ = window.Run(ct => { SavePaths(); return Task.CompletedTask; }, "目录已保存。"))) },
            cards,
            new Expander { Header = "命令行工具 · 高级选项", Content = advanced },
            Ui.Card("接入你的模型",
                Ui.Text("连接 API → 选择模型 → 打开桌面应用。"),
                Ui.Hint("原生模型直接接入；跨厂商模型由 MathGPT 内置适配，无需另装切换工具。桌面应用正常退出后重新打开。"),
                Ui.Button("连接与模型", () => window.Navigate(1))));
        RefreshCards();
        SizeChanged += (_, _) =>
        {
            var columns = ActualWidth >= 990 ? 3 : ActualWidth >= 660 ? 2 : 1;
            cards.ItemWidth = (ActualWidth + 14) / columns;
            foreach (FrameworkElement card in cards.Children) card.Width = Math.Max(260, cards.ItemWidth - 14);
        };
    }
    private void SavePaths()
    {
        SafeFiles.Writable(downloads.Text); SafeFiles.Writable(installs.Text); SafeFiles.Writable(workspace.Text);
        services.Store.State.DownloadDirectory = Path.GetFullPath(downloads.Text);
        services.Store.State.InstallDirectory = Path.GetFullPath(installs.Text);
        services.Store.State.WorkDirectory = Path.GetFullPath(workspace.Text);
        services.Store.Save();
    }
    private void RefreshCards()
    {
        cards.Children.Clear();
        foreach (var kind in new[] { ClientKind.Harness, ClientKind.CodexDesktop, ClientKind.ClaudeDesktop })
        {
            var current = services.Store.State.Clients.FirstOrDefault(c => c.Kind == kind);
            var bound = services.Store.State.Profiles.FirstOrDefault(p => p.Id == current?.ProfileId);
            var status = current is null ? "尚未接入" : $"{current.Version} · {(current.Managed ? "MathGPT 托管" : "已有安装")}";
            var description = kind switch
            {
                ClientKind.CodexDesktop => "OpenAI 官方桌面应用 · Windows x64 / ARM64 · Responses",
                ClientKind.ClaudeDesktop => "Anthropic 官方应用 · 本机运行",
                _ => "DeepSeek 官方桌面版 · 预览版 · Windows x64 · 多协议"
            };
            var heading = new Grid { Margin = new(0, 0, 0, 18) };
            heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var monogram = Ui.Text(kind == ClientKind.Harness ? "DS" : kind == ClientKind.CodexDesktop ? "CX" : "CC", 15);
            monogram.FontWeight = FontWeights.SemiBold; monogram.HorizontalAlignment = HorizontalAlignment.Center;
            monogram.VerticalAlignment = VerticalAlignment.Center; monogram.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var tile = new Border { Width = 44, Height = 44, CornerRadius = new(12), Child = monogram, HorizontalAlignment = HorizontalAlignment.Left };
            tile.SetResourceReference(Border.BackgroundProperty, "Tint"); heading.Children.Add(tile);
            var more = Ui.Button("", () =>
            {
                var menu = new ContextMenu();
                void Add(string label, Action action)
                {
                    var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item);
                }
                Add("导入官方安装包", () => ImportPackage(kind));
                Add("检查更新 / 重新安装", () => _ = Install(kind, true));
                if (kind == ClientKind.ClaudeDesktop)
                {
                    Add("修复 Claude 组件", () => _ = Install(kind, true));
                    Add("检查 Claude 组件", () => _ = window.Run(async ct =>
                    {
                        var found = await DesktopPackages.Detect(kind, ct) ?? throw new UserException("尚未检测到 Claude，请先安装。");
                        var report = await ClaudeDesktopReadiness.Check(found, ct);
                        MessageBox.Show("Claude " + found.Version + "\n" + report.Message, "Claude 组件检查", MessageBoxButton.OK,
                            report.Ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    }, "", true));
                }
                if (DesktopConfiguration.IsDesktop(kind)) Add("打开官方安装说明", () => MainWindow.OpenUrl(kind == ClientKind.CodexDesktop ?
                    "https://learn.chatgpt.com/docs/enterprise/windows-deployment" : "https://claude.com/docs/third-party/claude-desktop/installation"));
                if (DesktopConfiguration.IsDesktop(kind)) Add("恢复先前配置", () => _ = window.Run(ct =>
                {
                    new DesktopConfiguration(services.Store).Rollback(Find(kind), services.Clients.Running(Find(kind))); RefreshCards();
                    return Task.CompletedTask;
                }, "已恢复先前配置。"));
                Add("创建快捷方式", () => _ = window.Run(ct =>
                    {
                        var installation = Find(kind);
                        services.Clients.Shortcut(installation, App.Executable); return Task.CompletedTask;
                    }, "快捷方式已创建，其中不含密钥。"));
                Add("移除接入", () => Forget(kind));
                menu.IsOpen = true;
            });
            more.Content = Ui.Icon("more", 19); more.Style = (Style)FindResource("Ghost"); more.Margin = new(0);
            more.VerticalAlignment = VerticalAlignment.Top; more.ToolTip = "更多操作"; Grid.SetColumn(more, 1); heading.Children.Add(more);
            var title = Ui.Text(ClientService.DisplayName(kind), 17); title.FontWeight = FontWeights.SemiBold;
            var desc = Ui.Text(description, 12, "#788399"); desc.Margin = new(0, 7, 0, 17); desc.MinHeight = 43;
            var stateText = Ui.Text(current is null ? "尚未安装" : "已安装 · " + current.Version, 12, "#788399");
            var binding = Ui.Text(bound is null ? "连接 API 后，即可开始使用。" : bound.Model + "\n" +
                (current?.BuiltinRouting == true ? "MathGPT 内置接入 · " + bound.Group : current?.LocalRouting == true ? "CC Switch 本机路由 · " + bound.Group : current?.ConfigPending == true ? "待生效 · 正常退出客户端后点打开" : bound.Group), 12, "#788399");
            binding.Margin = new(0, 7, 0, 20); binding.MinHeight = 44;
            var actions = current is null ?
                Ui.Actions(Ui.Button("安装并接入  →", () => _ = Start(kind), true, "install-" + kind),
                    Ui.Button("检测", () => _ = Detect(kind), id: "detect-" + kind)) :
                Ui.Actions(Ui.Button("打开  →", () => _ = Start(kind), true, "launch-" + kind),
                    Ui.Button("配置", () => _ = Configure(kind), id: "configure-" + kind),
                    Ui.Button("检测", () => _ = Detect(kind), id: "detect-" + kind));
            var card = Ui.Card("", heading, title, desc, stateText, binding, actions,
                new Expander { Header = "安装与配置详情", Content = Ui.Hint(current is null ?
                    "官方安装源；安装前验证哈希或 Windows 发布者签名。" : current.Executable + "\n" + status +
                    (bound is null ? "" : "\n" + ModelRoute.ProtocolName(bound.Protocol) + " · 推理：" + (bound.Reasoning.Length > 0 ? bound.Reasoning : "默认"))) });
            card.Width = Math.Max(260, cards.ItemWidth - 14); card.Margin = new(0, 0, 14, 18); card.MinHeight = 363;
            cards.Children.Add(card);
        }
        advanced.Children.Clear();
        foreach (var kind in new[] { ClientKind.Codex, ClientKind.Claude })
            advanced.Children.Add(Ui.Card(ClientService.DisplayName(kind),
                Ui.Actions(Ui.Button("安装", () => _ = Install(kind)), Ui.Button("检测", () => _ = Detect(kind)),
                    Ui.Button("配置", () => _ = Configure(kind), id: "configure-" + kind),
                    Ui.Button("打开", () => _ = Open(kind), id: "launch-" + kind))));
    }
    private ClientInstallation Find(ClientKind kind) => services.Store.State.Clients.FirstOrDefault(c => c.Kind == kind)
        ?? throw new UserException("请先检测或安装这个客户端。");
    private async Task Detect(ClientKind kind)
    {
        if (DesktopConfiguration.IsDesktop(kind))
        {
            await window.Run(async ct =>
            {
                var desktop = await DesktopPackages.Detect(kind, ct) ?? throw new UserException("尚未检测到官方桌面应用，请点下载安装；也可导入官方 MSIX 安装包。");
                RememberDesktop(desktop); services.Clients.AutomaticShortcut(Find(kind), App.Executable); RefreshCards();
            }, "已检测到官方桌面应用，原配置未修改。", true); return;
        }
        var found = PackageService.FindInstalled(kind);
        if (found is null)
        {
            var picker = new OpenFileDialog { Title = "选择已有的 " + ClientService.DisplayName(kind) + " 官方程序", Filter = "可执行文件 (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(window) != true) return;
            found = picker.FileName;
        }
        await window.Run(async ct =>
        {
            SafeFiles.NoLinks(found);
            var version = kind == ClientKind.Harness ? System.Diagnostics.FileVersionInfo.GetVersionInfo(found).ProductVersion ?? "已检测" :
                await PackageService.ReadVersion(found, ct);
            var existing = services.Store.State.Clients.FirstOrDefault(c => c.Kind == kind && c.Executable.Equals(found, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                services.Store.State.Clients.RemoveAll(c => c.Kind == kind);
                services.Store.State.Clients.Add(new() { Kind = kind, Executable = found, Version = version,
                    Managed = false, InstallationRoot = Path.GetDirectoryName(found)! });
            }
            services.Store.Save(); services.Clients.AutomaticShortcut(Find(kind), App.Executable); RefreshCards();
        }, "已接入本机客户端，尚未修改原有配置。", true);
    }
    private void RememberDesktop(ClientInstallation desktop)
    {
        var existing = services.Store.State.Clients.FirstOrDefault(c => c.Kind == desktop.Kind);
        if (existing is not null)
        {
            desktop.ProfileId = existing.ProfileId; desktop.ConfigDirectory = existing.ConfigDirectory;
            desktop.LocalRouting = existing.LocalRouting;
            desktop.BuiltinRouting = existing.BuiltinRouting;
            desktop.WorkDirectory = existing.WorkDirectory; desktop.ConfigPending = existing.ConfigPending;
            desktop.ConfigurationMessage = existing.ConfigurationMessage;
            desktop.ConfigurationFingerprint = existing.ConfigurationFingerprint;
        }
        services.Store.State.Clients.RemoveAll(c => c.Kind == desktop.Kind);
        services.Store.State.Clients.Add(desktop); services.Store.Save();
    }
    private Task Install(ClientKind kind, bool update = false) => window.Run(ct => InstallCore(kind, update, ct), "客户端已安装，请应用连接后打开。", true);
    private async Task InstallCore(ClientKind kind, bool update, CancellationToken ct)
    {
        SavePaths();
        var existing = DesktopConfiguration.IsDesktop(kind) ? await DesktopPackages.Detect(kind, ct) : null;
        ClaudeDesktopReadinessReport? readiness = null;
        if (kind == ClientKind.ClaudeDesktop && existing is not null)
        {
            readiness = await ClaudeDesktopReadiness.Check(existing, ct);
            if (readiness.Ready && !update) { RememberDesktop(existing); RefreshCards(); window.Status("Claude Code 本机组件已就绪，无需重复下载。"); return; }
            window.Status("正在检查并补齐 Claude Code 本机运行组件。");
        }
        else if (!update && existing is not null)
        { RememberDesktop(existing); RefreshCards(); window.Status("已接入本机桌面应用，无需重复下载。"); return; }
        if (!update && kind == ClientKind.Harness && PackageService.FindInstalled(kind) is { } harness)
        {
            ct.ThrowIfCancellationRequested();
            SafeFiles.NoLinks(harness);
            var installed = services.Store.State.Clients.FirstOrDefault(c => c.Kind == kind);
            if (installed is null) { installed = new() { Kind = kind }; services.Store.State.Clients.Add(installed); }
            installed.Executable = harness;
            installed.Version = System.Diagnostics.FileVersionInfo.GetVersionInfo(harness).ProductVersion ?? "已检测";
            installed.InstallationRoot = Path.GetDirectoryName(harness)!;
            installed.Managed = false;
            services.Store.Save(); RefreshCards();
            window.Status("已接入本机 DeepSeek Harness，无需重复下载。"); return;
        }
        var release = await services.Packages.Resolve(kind, ct);
        if (kind == ClientKind.ClaudeDesktop && existing is not null && readiness is not null)
        {
            if (readiness.Ready)
            {
                if (!Version.TryParse(existing.Version, out var installedVersion) || !Version.TryParse(release.Version, out var availableVersion))
                    throw new UserException("无法确认 Claude 的更新版本，现有可用安装已保留。");
                if (installedVersion >= availableVersion)
                { RememberDesktop(existing); RefreshCards(); window.Status("Claude Code 本机组件已就绪，当前无需更新。"); return; }
            }
            else
            {
                var plan = ClaudeDesktopReadiness.Plan(readiness, existing.Version, release);
                if (plan != ClaudeDesktopRepairAction.UpgradeOffline) throw new UserException(ClaudeDesktopReadiness.RepairMessage(plan));
            }
            if (DesktopConfiguration.Running(kind)) throw new UserException("Claude 需要更新完整组件。请保存工作并正常退出 Claude，再点打开；不会清理账号或历史。");
        }
        if (update && DesktopConfiguration.IsDesktop(kind) && !DesktopClientDownloads.IsMirror(release))
        {
            var paused = services.Store.State.Downloads.LastOrDefault(d => d.Url == release.Url && d.Status.StartsWith("已暂停", StringComparison.Ordinal));
            release = release with { FileName = paused is not null ? Path.GetFileName(paused.Destination) :
                kind.ToString().ToLowerInvariant() + "-" + release.Architecture + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".msix" };
        }
        if (DesktopConfiguration.IsDesktop(kind)) await DesktopPackages.CheckDownload(release, ct);
        var target = SafeFiles.Child(installs.Text, kind.ToString().ToLowerInvariant() + "-" + release.Version);
        if (services.Store.State.Clients.Any(c => c.Kind == kind && c.Managed &&
            c.InstallationRoot.Equals(target, StringComparison.OrdinalIgnoreCase) && File.Exists(c.Executable)))
        { window.Status("当前目录已有相同版本，无需重复安装。"); return; }
        var file = await services.Downloads.Fetch(release, downloads.Text, window.TransferProgress(), ct);
        ct.ThrowIfCancellationRequested();
        var hadShortcut = File.Exists(ClientService.ShortcutPath(kind));
        await services.Packages.Install(release, file, installs.Text, window.TransferProgress(), ct);
        if (kind == ClientKind.ClaudeDesktop && !(await ClaudeDesktopReadiness.Check(Find(kind), ct)).Ready)
            throw new UserException("Claude 已安装，但 Code 本机组件仍不完整。请在更多操作中点“检查 Claude 组件”；暂不启动，避免反复下载失败。");
        services.Clients.AutomaticShortcut(Find(kind), App.Executable, !hadShortcut);
        RefreshCards();
    }
    private Task Start(ClientKind kind) => window.Run(async ct =>
    {
        SavePaths();
        var profile = services.Store.Active;
        if (services.Store.Key(profile) is null) throw new UserException("先在连接与模型保存 API Key，即可安装并接入。");
        var routes = await services.Catalog.Load(profile, ct);
        var route = routes.FirstOrDefault(r => r.Id == profile.Model && r.Available)
            ?? throw new UserException("请先在连接与模型选择一个可用模型。");
        if (DesktopConfiguration.IsDesktop(kind) || !services.Store.State.Clients.Any(c => c.Kind == kind && File.Exists(c.Executable)))
            await InstallCore(kind, false, ct);
        var installation = Find(kind);
        services.Clients.Configure(installation, profile, route, routes, App.Executable);
        services.Clients.AutomaticShortcut(installation, App.Executable);
        RefreshCards();
        if (installation.ConfigPending && DesktopConfiguration.IsDesktop(kind) && services.Clients.Running(installation))
        {
            window.Status("接入已准备。保存工作并正常退出 " + ClientService.DisplayName(kind) + " 后，再点打开即可。");
            return;
        }
        if (installation.BuiltinRouting) await new BuiltinGateway(services.Store).Ensure(profile, ct: ct);
        services.Clients.Launch(installation, profile);
        window.Status("已安装并应用当前连接，正在打开 " + ClientService.DisplayName(kind) + "。");
    }, "", true);
    public Task StartClient(ClientKind kind) => Start(kind);
    public Task EnsureClient(ClientKind kind, CancellationToken ct) => InstallCore(kind, false, ct);
    private void ImportPackage(ClientKind kind)
    {
        var dialog = new OpenFileDialog { Title = "选择官方安装包", Filter = DesktopConfiguration.IsDesktop(kind) ?
            "官方 Windows 应用包 (*.msix)|*.msix" : kind == ClientKind.Codex ? "Codex 官方 ZIP (*.zip)|*.zip" : "官方安装包 (*.exe)|*.exe" };
        if (dialog.ShowDialog(window) != true) return;
        _ = window.Run(async ct =>
        {
            SavePaths();
            var release = await services.Packages.Resolve(kind, ct);
            var hadShortcut = File.Exists(ClientService.ShortcutPath(kind));
            await services.Packages.Install(release, dialog.FileName, installs.Text, window.TransferProgress(), ct);
            services.Clients.AutomaticShortcut(Find(kind), App.Executable, !hadShortcut);
            RefreshCards();
        }, "官方包校验通过，安装完成。", true);
    }
    private Task Configure(ClientKind kind) => window.Run(async ct =>
    {
        SavePaths();
        var profile = services.Store.Active;
        var routes = await services.Catalog.Load(profile, ct);
        var route = routes.FirstOrDefault(r => r.Id == profile.Model && r.Available)
            ?? throw new UserException("请在连接页选择当前可用模型。");
        if (!ClientService.Supports(kind, route) && SwitchIntegration.CanRoute(kind, route))
        {
            RoutingGuide.Show(window, Find(kind), profile, route); RefreshCards(); return;
        }
        services.Clients.Configure(Find(kind), profile, route, routes, App.Executable);
        services.Clients.AutomaticShortcut(Find(kind), App.Executable); RefreshCards();
        window.Status(Find(kind).ConfigPending ? "配置已准备。正常退出客户端后，点打开即可生效。" : "当前连接已应用。");
    }, "", true);
    private Task Open(ClientKind kind) => window.Run(async ct =>
    {
        var installation = Find(kind);
        var profile = services.Store.State.Profiles.FirstOrDefault(p => p.Id == installation.ProfileId) ?? services.Store.Active;
        var routes = await services.Catalog.Load(profile, ct);
        var route = routes.FirstOrDefault(r => r.Id == profile.Model && r.Available && (installation.LocalRouting || ClientService.Supports(kind, r)))
            ?? throw new UserException("当前密钥或所选模型不兼容。Codex 需要 Responses；Claude Code 桌面版需要 Claude Messages。请到连接与模型更换模型或对应分组密钥。");
        if (!installation.LocalRouting) services.Clients.Configure(installation, profile, route, routes, App.Executable);
        services.Clients.Launch(installation, profile);
    }, "已请求启动客户端；API 调用以客户端结果为准。", true);
    private void Forget(ClientKind kind)
    {
        var installation = services.Store.State.Clients.FirstOrDefault(c => c.Kind == kind);
        if (installation is null) return;
        if (!Ui.Confirm(window, "只移除接入记录，程序、配置与历史继续保留。", "移除接入")) return;
        _ = window.Run(ct => { services.Clients.Forget(installation); RefreshCards(); return Task.CompletedTask; }, "已移除接入。");
    }
}
