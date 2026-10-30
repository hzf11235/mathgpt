using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MathGPT.Core;

namespace MathGPT.App.Pages;

public sealed class HomePage : UserControl
{
    public HomePage(MainWindow window)
    {
        var services = window.Services;
        var state = services.Store.State;
        var connected = state.Profiles.FirstOrDefault(p => p.Id == state.ActiveProfileId);
        var eyebrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 22) };
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new(4), Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, "Accent");
        eyebrow.Children.Add(dot); eyebrow.Children.Add(Ui.Text("你的模型工作区", 12, "#788399"));
        var first = Ui.Text("一个密钥", 44); first.FontWeight = FontWeights.Bold; first.LineHeight = 56;
        var second = Ui.Text("更多可能", 44); second.FontWeight = FontWeights.Bold; second.LineHeight = 56;
        second.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var subtitle = Ui.Text("连接工具，自由选模型，用量一目了然。", 14, "#788399");
        subtitle.Margin = new(0, 22, 0, 27);
        var start = Ui.Button(connected is null ? "继续网页配置  →" : "打开客户端  →", () => window.Navigate(connected is null ? 4 : 2), true, "home-connect");
        start.Padding = new(21, 11, 21, 11);
        var work = Ui.Button("新手接入指引", () => MainWindow.OpenUrl("https://mathgpt.xyz/docs/index.html#desktop")); work.Padding = new(20, 11, 20, 11);
        var active = Ui.Hint(connected is null ? "支持网站账号，也可直接连接兼容 API。" :
            connected.Name + " · " + (connected.Model.Length > 0 ? connected.Model : "待选择模型"));
        active.Margin = new(0, 15, 0, 0);
        var protocols = Ui.Text("OpenAI    ·    Anthropic    ·    Gemini", 12, "#788399"); protocols.Margin = new(0, 36, 0, 0);
        var left = Ui.Stack(eyebrow, first, second, subtitle, Ui.Actions(start, work), active, protocols);
        left.Margin = new(0, 27, 0, 0);

        var tools = new StackPanel();
        var top = new Grid { Margin = new(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(Ui.Text("常用客户端", 12, "#788399"));
        var manage = Ui.Button("管理  →", () => window.Navigate(2), id: "home-clients");
        manage.Style = (Style)Application.Current.FindResource("Ghost"); manage.FontSize = 12; manage.Margin = new(0, -9, -9, 0);
        Grid.SetColumn(manage, 1); top.Children.Add(manage); tools.Children.Add(top);
        var toolTitle = Ui.Text("为下一次灵感准备就绪", 18); toolTitle.FontWeight = FontWeights.SemiBold;
        toolTitle.Margin = new(0, 0, 0, 20); tools.Children.Add(toolTitle);
        foreach (var kind in new[] { ClientKind.Harness, ClientKind.CodexDesktop, ClientKind.ClaudeDesktop })
        {
            var installed = state.Clients.FirstOrDefault(c => c.Kind == kind);
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new(35) });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new(19) });
            var icon = Ui.Icon(kind == ClientKind.Harness ? "nodes" : kind == ClientKind.CodexDesktop ? "code" : "monitor", 19, "Accent");
            icon.VerticalAlignment = VerticalAlignment.Center; icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);
            var label = Ui.Text(ClientService.DisplayName(kind), 14); label.FontWeight = FontWeights.Medium;
            var status = Ui.Text(installed is null ? "点击安装，或接入已有客户端" : installed.ProfileId.Length == 0 ? "已安装 · 等待连接 API" :
                installed.ConfigPending ? "配置待生效 · 正常退出客户端后打开" : "已配置 · 可以开始工作", 12, "#788399");
            var description = Ui.Stack(label, status); Grid.SetColumn(description, 1); row.Children.Add(description);
            var arrow = Ui.Icon("arrow", 15, "Muted"); Grid.SetColumn(arrow, 2); arrow.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(arrow);
            var button = Ui.Button("", () => window.Navigate(2)); button.Content = row;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new(17, 14, 17, 14);
            button.Margin = new(0, 0, 0, 12); button.SetResourceReference(Control.BackgroundProperty, "Field");
            tools.Children.Add(button);
        }
        var right = Ui.Card("", tools); right.Margin = new(0); right.Padding = new(24);
        var hero = Ui.ResponsiveColumns(left, right, 0.97);

        var endpoint = connected?.BaseUrl ?? "https://mathgpt.xyz/v1";
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new() { Width = new(52) }); bar.ColumnDefinitions.Add(new() { Width = new(150) });
        bar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new(10), Child = Ui.Icon("code", 21, "Accent"), HorizontalAlignment = HorizontalAlignment.Left };
        tile.SetResourceReference(Border.BackgroundProperty, "Tint"); bar.Children.Add(tile);
        var apiLabel = Ui.Text("API 接入地址", 13); apiLabel.FontWeight = FontWeights.SemiBold;
        var desc = Ui.Stack(apiLabel, Ui.Text("连接你正在使用的工具。", 11, "#788399")); Grid.SetColumn(desc, 1); bar.Children.Add(desc);
        var address = Ui.Input("home-api-address", endpoint); address.IsReadOnly = true; address.MinHeight = 38;
        address.FontFamily = new("Cascadia Code, Consolas"); address.FontSize = 11;
        address.Margin = new(0, 1, 16, 0); Grid.SetColumn(address, 2); bar.Children.Add(address);
        var copy = Ui.Button("复制地址", () => Clipboard.SetText(endpoint)); copy.Style = (Style)Application.Current.FindResource("Ghost");
        copy.Margin = new(0); Grid.SetColumn(copy, 3); bar.Children.Add(copy);
        var api = Ui.Card("", bar); api.Padding = new(22, 18, 22, 18); api.Margin = new(0, 30, 0, 20);

        var facts = new Grid { Margin = new(2, 0, 0, 0) };
        for (int i = 0; i < 2; i++) facts.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var values = new[] { ($"{state.Profiles.Count}", "个 API 连接"), ($"{state.Clients.Count(c => ClientService.AutomaticShortcutKind(c.Kind))}", "个已接入客户端") };
        for (int i = 0; i < values.Length; i++)
        {
            var number = Ui.Text(values[i].Item1, 22); number.FontWeight = FontWeights.SemiBold;
            var cell = Ui.Stack(number, Ui.Text(values[i].Item2, 12, "#788399"));
            cell.Margin = new(18, 4, 0, 4); Grid.SetColumn(cell, i); facts.Children.Add(cell);
        }
        var support = Ui.Actions(Ui.Button("复制微信 · t222oo", () => Clipboard.SetText("t222oo")),
            Ui.Button("复制邮箱", () => Clipboard.SetText("ndls11235@gmail.com")));
        support.HorizontalAlignment = HorizontalAlignment.Right;
        support.Margin = new(0, 18, 0, 0);
        Content = connected is null ? Ui.Stack(hero, api, facts, support) : Ui.Stack(new QuickConnectionPanel(window), hero, api, facts, support);
    }
}
