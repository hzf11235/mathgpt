using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MathGPT.Core;

namespace MathGPT.App;

internal static class UiSmoke
{
    public static async Task Run(MainWindow window, string directory)
    {
        SafeFiles.Writable(directory);
        var checks = new List<object>();
        var navigation = (System.Windows.Controls.Panel)window.FindName("Navigation");
        var names = navigation.Children.OfType<System.Windows.Controls.Button>()
            .Select(System.Windows.Automation.AutomationProperties.GetName).ToArray();
        if (!names.SequenceEqual(new[] { "安装配置", "快速切换", "账号", "帮助" }))
            throw new InvalidOperationException("Unexpected desktop navigation.");
        foreach (var width in new[] { 1180, 800 })
        {
            window.Width = width; window.Height = 820;
            for (var page = 0; page < 6; page++)
            {
                window.Navigate(page);
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(70);
                var elements = Walk(window).ToList();
                var invalid = elements.OfType<FrameworkElement>().Where(e => !double.IsFinite(e.ActualWidth) ||
                    !double.IsFinite(e.ActualHeight)).Count();
                var errors = elements.OfType<TextBlock>().Count(t => t.Text.Contains('\uFFFD'));
                foreach (var dpi in new[] { 96, 144, 192 })
                {
                    var scale = dpi / 96d;
                    var clientView = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)(clientView.ActualWidth * scale), (int)(clientView.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(clientView);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var file = SafeFiles.Child(directory, $"page-{page}-{width}-dpi{dpi}.png");
                    using (var stream = File.Create(file)) encoder.Save(stream);
                    checks.Add(new { page, width, dpi, result = invalid == 0 && errors == 0 ? "passed" : "failed",
                        controls = elements.Count, invalidLayout = invalid, replacementCharacters = errors, screenshot = file });
                }
            }
        }
        SafeFiles.AtomicWrite(SafeFiles.Child(directory, "ui-result.json"), JsonSerializer.Serialize(checks, JsonData.Options));
        var originalTheme = window.Services.Store.State.Appearance;
        var originalDark = window.Services.Store.State.DarkMode;
        window.Width = 1180;
        foreach (var appearance in new[] { "aurora", "orbit", "paper", "dark" })
        {
            window.PreviewAppearance(appearance == "dark" ? "aurora" : appearance, appearance == "dark");
            foreach (var page in new[] { 0, 2 })
            {
                window.Navigate(page);
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var clientView = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)clientView.ActualWidth, (int)clientView.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(clientView);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(SafeFiles.Child(directory, $"theme-{appearance}-page-{page}.png"));
                encoder.Save(stream);
            }
        }
        window.PreviewAppearance(originalTheme, originalDark);
        var previewProfile = new ConnectionProfile { Name = "UI fixture", Model = "claude-opus-5-5", Group = "claude-pending", Protocol = WireProtocol.Anthropic };
        var routing = RoutingGuide.Create(window, new() { Kind = ClientKind.CodexDesktop }, previewProfile,
            new() { Id = previewProfile.Model, Group = previewProfile.Group, Available = true, Protocols = [WireProtocol.Anthropic] }, "sk-ui-fixture-only");
        routing.ShowActivated = false; routing.ShowInTaskbar = false; routing.WindowStartupLocation = WindowStartupLocation.Manual; routing.Left = -10000;
        routing.Show(); routing.UpdateLayout();
        var routingBitmap = new RenderTargetBitmap((int)routing.ActualWidth, (int)routing.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        routingBitmap.Render(routing);
        var routingEncoder = new PngBitmapEncoder(); routingEncoder.Frames.Add(BitmapFrame.Create(routingBitmap));
        using (var stream = File.Create(SafeFiles.Child(directory, "routing-guide.png"))) routingEncoder.Save(stream);
        var routingControls = Walk(routing).OfType<Button>().Count();
        SafeFiles.AtomicWrite(SafeFiles.Child(directory, "routing-preview-result.json"),
            JsonSerializer.Serialize(new { result = routingControls >= 6 ? "passed" : "failed", buttons = routingControls, networkRequests = 0 }, JsonData.Options));
        routing.Close();
        await UiFlowAudit.Run(directory);
        await QuickSwitchUiAudit.Run(directory);
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
