using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MathGPT.Core;

namespace MathGPT.Mac;

// Browser approval carries a short public code only. The API key is delivered
// by the authenticated device poll and remains in the shared secure store.
internal sealed class MacSetupPanel : Border, IDisposable
{
    private readonly TextBlock detail = new() { Text = "已在官网选好应用和密钥？确认后即可继续，无需重新填写。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock code = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, IsVisible = false };
    private readonly Button connect = Button("继续网页配置");
    private readonly Button install = Button("安装并开始");
    private readonly Button cancel = Button("取消等待", false);
    private readonly Func<DesktopSetupGrant, CancellationToken, Task> accept;
    private readonly Func<Task> begin;
    private readonly Action<bool> busy;
    private readonly Action<string> open;
    private CancellationTokenSource? operation;
    private bool working;
    private bool applicationBusy;
    private bool disposed;

    internal MacSetupPanel(StateStore store, Func<DesktopSetupGrant, CancellationToken, Task> accept,
        Func<Task> begin, Action<bool> busy, Action<string> open)
    {
        this.accept = accept; this.begin = begin; this.busy = busy; this.open = open;
        Background = Brushes.White; BorderBrush = Brush.Parse("#DEE3F1"); BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(16); Padding = new Thickness(22); Margin = new Thickness(0, 0, 0, 14);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "继续官网中的接入", FontSize = 20, FontWeight = FontWeight.SemiBold });
        body.Children.Add(detail); body.Children.Add(code);
        var row = new WrapPanel(); row.Children.Add(connect); row.Children.Add(install); row.Children.Add(cancel); body.Children.Add(row);
        Child = body; install.IsVisible = false; cancel.IsVisible = false;
        connect.Click += async (_, _) => await Connect();
        install.Click += async (_, _) => await Install();
        cancel.Click += (_, _) => operation?.Cancel();
        var saved = store.State.Profiles.FirstOrDefault(p => p.Id == store.State.ActiveProfileId && p.SetupPlanId.Length > 0);
        if (saved is not null) ShowReady(store.State.PreferredClient, saved.Model);
    }

    internal void SetApplicationBusy(bool value)
    {
        applicationBusy = value;
        connect.IsEnabled = !disposed && !working && !value;
        install.IsEnabled = !disposed && !working && !value;
    }

    internal void ShowReady(ClientKind client, string model)
    {
        detail.Text = "已接收 " + ClientService.DisplayName(client) + " · " + model + "。点击后自动安装或复用已有应用，并配置打开。";
        connect.Content = "重新连接网页配置"; install.IsVisible = true; code.IsVisible = false;
    }

    private async Task Connect()
    {
        if (disposed || working || applicationBusy) return;
        operation?.Dispose(); operation = new CancellationTokenSource();
        var token = operation.Token;
        working = true; busy(true); connect.IsEnabled = false; install.IsEnabled = false; cancel.IsVisible = true;
        try
        {
            using var client = new DesktopSetupClient();
            var session = await client.Start(token);
            if (disposed) return;
            code.Text = "核对代码：" + session.UserCode; code.IsVisible = true;
            detail.Text = "请在刚打开的网页核对相同代码，再点击“连接这台电脑”。此窗口会自动继续。";
            open(session.VerificationUri.AbsoluteUri);
            var grant = await client.Wait(session, token);
            token.ThrowIfCancellationRequested();
            if (disposed) return;
            await accept(grant, token);
        }
        catch (OperationCanceledException) { if (!disposed) { detail.Text = "已取消等待。随时可以重新连接网页配置。"; code.IsVisible = false; } }
        catch (Exception error) { if (!disposed) { detail.Text = Privacy.Friendly(error); code.IsVisible = false; } }
        finally
        {
            working = false;
            if (!disposed) { cancel.IsVisible = false; busy(false); SetApplicationBusy(false); }
        }
    }

    private async Task Install()
    {
        if (disposed || working || applicationBusy) return;
        working = true; busy(true); connect.IsEnabled = false; install.IsEnabled = false;
        try { await begin(); }
        catch (Exception error) { if (!disposed) detail.Text = Privacy.Friendly(error); }
        finally { working = false; if (!disposed) { busy(false); SetApplicationBusy(false); } }
    }

    private static Button Button(string label, bool primary = true) => new()
    {
        Content = label, Padding = new Thickness(16, 9), CornerRadius = new CornerRadius(9),
        Margin = new Thickness(0, 0, 10, 8), Background = primary ? Brush.Parse("#3465D9") : Brushes.White,
        Foreground = primary ? Brushes.White : Brush.Parse("#254A9C"), BorderBrush = Brush.Parse("#DEE3F1"), BorderThickness = new Thickness(1)
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; operation?.Cancel(); operation?.Dispose(); operation = null;
    }
}
