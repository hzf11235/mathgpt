using System.Windows;
using System.Windows.Controls;
using MathGPT.Core;

namespace MathGPT.App;

internal static class RoutingGuide
{
    public static void Show(MainWindow owner, ClientInstallation install, ConnectionProfile profile, ModelRoute route)
        => Create(owner, install, profile, route).ShowDialog();
    internal static Window Create(MainWindow owner, ClientInstallation install, ConnectionProfile profile, ModelRoute route, string? previewKey = null)
    {
        var key = previewKey ?? owner.Services.Store.Key(profile) ?? throw new UserException("请先保存 API Key。");
        var dialog = new Window { Owner = owner, Title = "跨厂商模型接入", Width = 590, Height = 585,
            MinWidth = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)Application.Current.FindResource(typeof(Window)) };
        var target = ClientService.DisplayName(install.Kind);
        var endpoint = profile.Protocol == WireProtocol.Anthropic ? ClientService.AnthropicOrigin(profile.BaseUrl) : profile.BaseUrl;
        var body = Ui.Stack(
            Ui.Text(target + "  →  " + profile.Model, 21),
            Ui.Hint("由 CC Switch 在本机转换协议，型号保持 " + profile.Model + "。工作时需要保持 CC Switch 运行。"),
            Ui.Card("1  安装 CC Switch",
                Ui.Text("下载官方 Windows 安装器，按向导安装后打开一次。", 13),
                Ui.Button("下载 CC Switch " + SwitchIntegration.Version, () => MainWindow.OpenUrl(SwitchIntegration.InstallerUrl))),
            Ui.Card("2  导入模型",
                Ui.Text(install.Kind == ClientKind.CodexDesktop ? "点击导入，在 CC Switch 确认添加到 Codex。不会自动切换当前配置。" :
                    "在 CC Switch 的 Claude Desktop 页手动添加提供商，填写以下地址、Key、真实型号和 API 格式，启用路由/模型映射。", 13),
                Ui.Text(endpoint + "\n" + profile.Model + " · " + SwitchIntegration.Format(profile.Protocol), 12),
                Ui.Actions(Ui.Button("复制地址", () => Clipboard.SetText(endpoint)),
                    Ui.Button("复制型号", () => Clipboard.SetText(profile.Model)),
                    Ui.Button("复制 Key", () => Clipboard.SetText(key)))),
            Ui.Card("3  启动本机路由",
                Ui.Text("在 CC Switch 的目标应用页选择「路由」并启动，选择刚导入的 MathGPT 模型。保存工作后正常退出并重新打开桌面应用。", 13)),
            Ui.Hint("工具、图片与推理能力以模型和路线支持为准。切回直连时，重新应用对应的原生连接。"));
        if (install.Kind == ClientKind.CodexDesktop)
        {
            var import = Ui.Button("导入 CC Switch", () =>
            {
                try { SwitchIntegration.Import(install.Kind, profile, route, key); }
                catch (Exception ex) { MessageBox.Show(dialog, Privacy.Friendly(ex), "接入提示"); }
            }, true);
            ((StackPanel)((Border)body.Children[3]).Child).Children.Add(import);
        }
        var done = Ui.Button("已完成路由设置，保存接入", () =>
        {
            install.ProfileId = profile.Id; install.LocalRouting = true;
            owner.Services.Store.Save(); dialog.DialogResult = true;
        }, true);
        body.Children.Add(Ui.Actions(Ui.Button("稍后设置", () => dialog.Close()), done));
        dialog.Content = new ScrollViewer { Content = body, Margin = new(24), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog;
    }
}
