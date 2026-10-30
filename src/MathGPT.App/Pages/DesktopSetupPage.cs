using System.Windows;
using System.Windows.Controls;
using MathGPT.Core;

namespace MathGPT.App.Pages;

public sealed class DesktopSetupPage : UserControl
{
    private readonly MainWindow window;
    private readonly TextBlock heading = Ui.Text("接着网页里的选择，继续安装", 22);
    private readonly TextBlock detail = Ui.Hint("不用重新填写密钥、模型或分组。");
    private readonly TextBlock approvalCode = Ui.Text("", 28);
    private readonly Button primary;
    private ImportedDesktopSetup? imported;
    public DesktopSetupPage(MainWindow window)
    {
        this.window = window;
        heading.FontWeight = FontWeights.SemiBold;
        approvalCode.FontFamily = new("Consolas");
        approvalCode.Margin = new(0, 12, 0, 12);
        approvalCode.Visibility = Visibility.Collapsed;
        primary = Ui.Button("继续网页配置", () => _ = Continue(), true, "continue-web-setup");
        var saved = window.Services.Store.State.Profiles.FirstOrDefault(p => p.Id == window.Services.Store.State.ActiveProfileId && p.SetupPlanId.Length > 0);
        if (saved is not null)
        {
            imported = new(saved.Id, window.Services.Store.State.PreferredClient, saved.Model, saved.Group, 0);
            ShowReady();
        }
        var help = new Expander { Header = "需要帮助？", Content = Ui.Stack(
            Ui.Hint("点击继续后，浏览器会打开 MathGPT。核对电脑与网页的代码一致，再点连接这台电脑。若浏览器尚未登录，使用刚注册的 MathGPT 账号即可。"),
            Ui.Button("接入已有密钥", () => QuickKeyDialog.Show(window, false)),
            Ui.Button("重新承接网页选择", () => { imported = null; heading.Text = "接着网页里的选择，继续安装"; primary.Content = "继续网页配置"; approvalCode.Text = ""; })) };
        Content = Ui.Stack(Ui.Card("", heading, detail, approvalCode, Ui.Actions(primary),
            Ui.Hint("系统如提示安装确认，请按提示操作；已有客户端会复用。")), help);
    }
    private async Task Continue()
    {
        if (imported is { } ready)
        {
            window.Navigate(0);
            return;
        }
        await window.Run(async ct =>
        {
            using var client = new DesktopSetupClient();
            var session = await client.Start(ct);
            ct.ThrowIfCancellationRequested();
            approvalCode.Text = session.UserCode;
            approvalCode.Visibility = Visibility.Visible;
            detail.Text = "请在浏览器核对这个代码，并点击连接这台电脑。";
            MainWindow.OpenUrl(session.VerificationUri.AbsoluteUri);
            var grant = await client.Wait(session, ct);
            ct.ThrowIfCancellationRequested();
            var result = await DesktopSetupImporter.Import(window.Services.Store, window.Services.Catalog, grant, ct);
            ct.ThrowIfCancellationRequested();
            imported = result;
            approvalCode.Text = "";
            approvalCode.Visibility = Visibility.Collapsed;
            ShowReady();
            window.Status("网页选择已接上，点击安装并开始即可。");
        }, cancellable: true);
        if (imported is not null) window.Navigate(0);
    }
    private void ShowReady()
    {
        if (imported is null) return;
        heading.Text = ClientService.DisplayName(imported.Client) + " · 已准备好";
        detail.Text = imported.Model + (imported.Group == "universal" ? " · 万能密钥，可切换三家模型" : " · 可切换此密钥支持的其他模型");
        detail.TextWrapping = TextWrapping.Wrap;
        primary.Content = "选择位置并开始";
    }
}
