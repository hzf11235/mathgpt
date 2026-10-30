using System.Windows;
using System.Windows.Controls;
using MathGPT.Core;

namespace MathGPT.App.Pages;

public sealed class SetupWizardPage : UserControl
{
    public SetupWizardPage(MainWindow window)
    {
        MaxWidth = 760; HorizontalAlignment = HorizontalAlignment.Stretch;
        var store = window.Services.Store;
        var profile = store.State.Profiles.FirstOrDefault(p => p.Id == store.State.ActiveProfileId);
        if (profile is null)
        {
            Content = new DesktopSetupPage(window);
            return;
        }
        var app = Ui.Select("setup-client");
        foreach (var kind in new[] { ClientKind.CodexDesktop, ClientKind.ClaudeDesktop, ClientKind.Harness })
            app.Items.Add(new ComboBoxItem { Content = ClientService.DisplayName(kind), Tag = kind });
        app.SelectedItem = app.Items.Cast<ComboBoxItem>().First(x => Equals(x.Tag, store.State.PreferredClient));
        var folder = Ui.Input("setup-workspace", store.State.WorkDirectory);
        folder.IsReadOnly = true;
        var downloads = Ui.Input("setup-downloads", store.State.DownloadDirectory); downloads.IsReadOnly = true;
        var installs = Ui.Input("setup-install-directory", store.State.InstallDirectory); installs.IsReadOnly = true;
        var installation = new Expander { Header = "应用安装位置", Content = Ui.PathField("应用位置", installs, "选择应用安装位置") };
        void ShowInstallLocation() => installation.Visibility = app.SelectedItem is ComboBoxItem { Tag: ClientKind.Harness } ? Visibility.Visible : Visibility.Collapsed;
        app.SelectionChanged += (_, _) => ShowInstallLocation(); ShowInstallLocation();
        var primary = Ui.Button("安装配置并打开", async () =>
        {
            if (app.SelectedItem is not ComboBoxItem { Tag: ClientKind kind }) return;
            try
            {
                store.State.WorkDirectory = SafeFiles.DirectoryPath(folder.Text);
                store.State.DownloadDirectory = SafeFiles.DirectoryPath(downloads.Text);
                store.State.InstallDirectory = SafeFiles.DirectoryPath(installs.Text);
                store.State.PreferredClient = kind;
                store.Save();
                await new ClientsPage(window).StartClient(kind);
            }
            catch (Exception ex) { window.Status(Privacy.Friendly(ex), true); }
        }, true, "setup-install-launch");
        var first = Ui.Text("安装并开始", 24); first.FontWeight = FontWeights.SemiBold;
        var group = profile.Group == "universal" ? "万能密钥" : profile.Name;
        var help = new Expander { Header = "第一次打开怎么操作？", Content = Ui.Stack(
            Ui.Hint("工作区就是 AI 处理文件的文件夹。可以直接用上面选好的文件夹。"),
            Ui.Hint("选择 Code，然后选择文件夹，输入任务并发送。模型可在输入框附近切换。"),
            Ui.Text("Code  编程工作　·　Choose folder / Workspace  选择文件夹\nAllow / Approve  允许　·　Send  发送", 13),
            Ui.Button("打开中文图文指引", () => MainWindow.OpenUrl("https://mathgpt.xyz/docs/index.html#desktop"))) };
        Content = Ui.Stack(Ui.Card("", first, Ui.Hint(group + " · " + profile.Model),
                Ui.Field("应用", app), Ui.PathField("下载位置", downloads, "选择下载位置"),
                Ui.PathField("工作文件夹", folder, "选择工作文件夹"), installation,
                Ui.Actions(primary, Ui.Button("切换密钥或模型", () => window.Navigate(5)),
                    Ui.Button("创建密钥", () => QuickKeyDialog.Show(window, true)),
                    Ui.Button("接入已有密钥", () => QuickKeyDialog.Show(window, false)))),
            help, Ui.Actions(Ui.Button("管理安装", () => window.Navigate(2)),
                Ui.Button("接入另一份网页配置", () => window.Navigate(4))));
        MaxWidth = 760; HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}
