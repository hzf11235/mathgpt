using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows;
using MathGPT.Core;

namespace MathGPT.App.Pages;

public sealed class SettingsPage : UserControl
{
    public SettingsPage(MainWindow window)
    {
        var services = window.Services;
        var state = services.Store.State;
        var records = new ListBox { Height = 130 };
        void RefreshDownloads()
        {
            records.ItemsSource = state.Downloads.Select(d => new DownloadItem(d)).ToList();
        }
        RefreshDownloads();
        var updateMessage = Ui.Hint(window.UpdateMessage);
        void ShowUpdate(string message) => updateMessage.Text = message;
        Loaded += (_, _) => { updateMessage.Text = window.UpdateMessage; window.UpdateMessageChanged += ShowUpdate; };
        Unloaded += (_, _) => window.UpdateMessageChanged -= ShowUpdate;
        var autoUpdates = new CheckBox { Content = "自动检查更新", IsChecked = services.Store.Network.AutomaticUpdates };
        var autoAccount = new CheckBox { Content = "自动刷新账号", IsChecked = services.Store.Network.AutomaticAccountRefresh };
        var networkNotice = Ui.Hint(NetworkPreferences.Notice); networkNotice.TextWrapping = TextWrapping.Wrap;
        var networkStatus = Ui.Hint("尚未保存联网选择，自动功能暂不开启。");
        networkStatus.Visibility = services.Store.Network.Configured ? Visibility.Collapsed : Visibility.Visible;
        Content = Ui.Stack(
            Ui.Card("软件更新",
                Ui.Text("MathGPT " + (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "1.7.0"), 17),
                updateMessage,
                Ui.Button("检查更新", () => _ = window.CheckForUpdates())),
            Ui.Card("联网选项",
                networkNotice, networkStatus, autoUpdates, autoAccount,
                Ui.Button("保存联网选项", () => _ = window.Run(ct =>
                {
                    services.Store.Network.Save(autoUpdates.IsChecked == true, autoAccount.IsChecked == true);
                    networkStatus.Visibility = Visibility.Collapsed;
                    return Task.CompletedTask;
                }, "联网选项已保存。"))),
            Ui.Card("界面风格",
                Ui.Hint("与 mathgpt.xyz 使用相同的轻紫、素白、纸感配色；右上角可切换明暗。"),
                Ui.Actions(ThemeManager.Themes.Select(theme =>
                {
                    var button = Ui.Button(theme.Name + "  →", () => _ = window.SetAppearance(theme.Id));
                    button.MinWidth = 150; button.Padding = new(18, 13, 18, 13);
                    return (UIElement)button;
                }).ToArray())),
            Ui.Card("本机数据",
                Ui.Text(services.Store.Root, 13), Ui.Hint("API Key 与网站会话由 Windows DPAPI 加密，只能由原 Windows 账号解密。账号密码与入口密码不保存。"),
                Ui.Actions(Ui.Button("打开数据目录", () => _ = window.Run(ct => { MainWindow.OpenFolder(services.Store.Root); return Task.CompletedTask; })),
                    Ui.Button("打开工作目录", () => _ = window.Run(ct => { MainWindow.OpenFolder(state.WorkDirectory); return Task.CompletedTask; }))),
                Ui.Hint("升级与卸载保留此目录及任务文件。备份时请同时保留数据目录与独立任务目录；凭据不能直接跨 Windows 账号迁移。")),
            Ui.Card("下载管理", records,
                Ui.Actions(Ui.Button("刷新列表", RefreshDownloads),
                    Ui.Button("删除所选包与下载断点", () =>
                    {
                        if (records.SelectedItem is not DownloadItem item) return;
                        if (!Ui.Confirm(window, "删除这个 MathGPT 下载记录及其缓存包？已安装客户端和用户数据保持保留。", "删除缓存")) return;
                        _ = window.Run(async ct => { await services.Downloads.Remove(item.Record); RefreshDownloads(); }, "下载缓存已删除。");
                    })),
                Ui.Hint("暂停后，在客户端页再次点“下载并安装”即可恢复。已经进入官方安装器的操作会等待安全结束。")),
            Ui.Card("兼容与连接",
                Ui.Text($"当前系统：{Environment.OSVersion.VersionString} · {RuntimeInformation.OSArchitecture}", 13),
                Ui.Hint("MathGPT：Windows 10 22H2（19045）及 Windows 11，x64 / ARM64。x64 本机验证；ARM64 需在对应设备复核。"),
                Ui.Hint("主要支持 Harness、Codex 与 Claude 官方桌面应用；命令行工具位于高级选项。Claude Cowork 另需系统虚拟化支持。"),
                Ui.Hint("Codex 与 Claude 使用本站提供的原厂完整包，安装前校验文件和厂商签名。"),
                Ui.Hint("连接成功只表示认证或目录接口可读。模型、工具执行与费用由实际请求和网站账单确认。")),
            Ui.Card("隐私与安全",
                Ui.Text("密钥在本机加密保存。桌面应用通过凭据助手读取；Harness 与命令行通过子进程环境读取。", 13),
                Ui.Hint("桌面配置修改前加密备份，可在客户端更多菜单恢复。检测到后续手工改动时会停止覆盖。运行中的应用正常退出后再应用设置。")),
            Ui.Card("帮助与支持",
                Ui.Text("MathGPT", 17),
                Ui.Text("微信：t222oo\n邮箱：ndls11235@gmail.com", 14),
                Ui.Actions(Ui.Button("注册 ↗", () => MainWindow.OpenUrl("https://mathgpt.xyz/sign-up")),
                    Ui.Button("充值 ↗", () => MainWindow.OpenUrl("https://mathgpt.xyz/wallet")),
                    Ui.Button("文档 ↗", () => MainWindow.OpenUrl("https://mathgpt.xyz/docs")),
                    Ui.Button("邮件反馈", () => MainWindow.OpenUrl("mailto:ndls11235@gmail.com"))),
                Ui.Hint("最近诊断：" + window.LastDiagnostic),
                Ui.Hint("使用 Windows 自带 BITS 与 DPAPI；界面使用 Microsoft .NET / WPF。官方客户端分别遵守其自身许可证与服务条款。")));
    }
    private sealed record DownloadItem(DownloadRecord Record)
    {
        public override string ToString() => $"{Path.GetFileName(Record.Destination)} · {Record.Status}";
    }
}
