using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MathGPT.Core;

namespace MathGPT.App;

internal static class QuickSwitchUiAudit
{
    public static async Task Run(string directory)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var serving = Task.Run(async () =>
        {
            for (var number = 0; number < 4; number++)
            {
                var request = await listener.GetContextAsync();
                var expected = number % 2 == 0 ? "fixture-quick-one" : "fixture-quick-two";
                if (request.Request.Url?.AbsolutePath != "/v1/models" || request.Request.Headers["Authorization"] != "Bearer " + expected)
                    throw new InvalidOperationException("Quick switching used the wrong key or sent an unexpected request.");
                var ids = number % 2 == 0 ? new[] { "gpt-6.1-sol", "gpt-6-astra" } : new[] { "gemini-4-argon" };
                request.Response.ContentType = "application/json";
                if (number == 3) request.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(number == 3 ? "{\"error\":\"fixture_forbidden\"}" :
                    JsonSerializer.Serialize(new { data = ids.Select(id => new { id }) })));
                request.Response.Close();
            }
        });
        var services = new AppServices(SafeFiles.Child(directory, "quick-data-" + Guid.NewGuid().ToString("N")));
        var a = new ConnectionProfile { Name = "GPT 测试连接", Model = "gpt-6.1-sol", BaseUrl = $"http://127.0.0.1:{port}/v1" };
        var b = new ConnectionProfile { Name = "Gemini 测试连接", Model = "gemini-4-argon", BaseUrl = a.BaseUrl, Protocol = WireProtocol.ChatCompletions };
        services.Store.SaveProfile(b, "fixture-quick-two"); services.Store.SaveProfile(a, "fixture-quick-one");
        var window = new MainWindow(services) { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = 0, Width = 800, Height = 820 };
        window.Show(); window.Navigate(5); window.UpdateLayout();
        try
        {
            Find<Button>(window, "quick-refresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var models = Find<ComboBox>(window, "quick-model");
            await Until(() => models.Items.Count == 2 && models.IsEnabled);
            models.SelectedItem = models.Items.OfType<ModelRoute>().Single(r => r.Id == "gpt-6-astra");
            if (!Find<ComboBox>(window, "quick-effort").Items.Contains("high"))
                throw new InvalidOperationException("The selected model lost its reasoning controls.");
            Find<ComboBox>(window, "quick-connection").SelectedItem = b;
            await Until(() => models.Items.Count == 1 && models.IsEnabled);
            if (models.Items[0] is not ModelRoute { Id: "gemini-4-argon" } || services.Store.State.ActiveProfileId != b.Id || services.Store.State.Clients.Count != 0)
                throw new InvalidOperationException("Changing keys retained another key's catalog or installed an app.");
            window.UpdateLayout();
            var view = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(SafeFiles.Child(directory, "quick-switch-800.png"))) encoder.Save(output);
            Find<ComboBox>(window, "quick-connection").SelectedItem = a;
            await Until(() => models.Items.Count == 2 && models.IsEnabled);
            var quick = Find<Pages.QuickConnectionPanel>(window, "");
            var busy = typeof(Pages.QuickConnectionPanel).GetField("loading", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Find<ComboBox>(window, "quick-connection").SelectedItem = b;
            if (models.Items.Count != 0 || models.IsEnabled || Find<ComboBox>(window, "quick-effort").Items.Count != 0 ||
                Find<Button>(window, "quick-apply").IsEnabled)
                throw new InvalidOperationException("Changing keys retained the old model/effort choices while loading.");
            await Until(() => busy.GetValue(quick) is null);
            if (models.Items.Count != 0 || models.SelectedItem is not null || models.IsEnabled ||
                Find<ComboBox>(window, "quick-effort").Items.Count != 0 || Find<ComboBox>(window, "quick-effort").IsEnabled ||
                Find<Button>(window, "quick-apply").IsEnabled || services.Store.State.ActiveProfileId != a.Id || services.Store.Key(b) != "fixture-quick-two")
                throw new InvalidOperationException("A failed catalog exposed another key's models/efforts or allowed Apply.");
            // Even a programmatic button event must not persist a stale route or begin installation.
            Find<Button>(window, "quick-apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (b.Model != "gemini-4-argon" || services.Store.State.Clients.Count != 0)
                throw new InvalidOperationException("A failed catalog allowed a stale key/model Apply.");
            await serving;
            SafeFiles.AtomicWrite(SafeFiles.Child(directory, "quick-switch-result.json"), JsonSerializer.Serialize(new
            { passed = true, keys = 2, catalogRequests = 4, fullCatalog = true, reasoningControls = true, failedCatalogCleared = true,
                staleApplyRejected = true, existingStatePreserved = true, inferenceRequests = 0, applicationsInstalled = 0 }, JsonData.Options));
        }
        finally { listener.Stop(); window.Close(); }
    }
    private static async Task Until(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 500; attempt++) { if (ready()) return; await Task.Delay(10); }
        throw new InvalidOperationException("The quick model picker did not finish loading.");
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T found && AutomationProperties.GetAutomationId(root) == id) return found;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try { return Find<T>(VisualTreeHelper.GetChild(root, index), id); }
            catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(id);
    }
}
