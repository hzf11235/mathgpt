using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MathGPT.Core;

namespace MathGPT.App;

// In-process fixture checks, run only by the explicit --ui-smoke diagnostic.
// It creates its own data directory and never touches the window's account or connections.
internal static class UiFlowAudit
{
    public static async Task Run(string directory)
    {
        var guard = new TcpListener(IPAddress.Loopback, 0); guard.Start();
        var port = ((IPEndPoint)guard.LocalEndpoint).Port; guard.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var serving = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            if (ctx.Request.Url!.AbsolutePath != "/v1/models" ||
                ctx.Request.Headers["Authorization"] != "Bearer fixture-ui-local-only")
                throw new InvalidOperationException("UI did not use its selected key or base URL.");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""{"data":[{"id":"fixture-ui-model"}]}"""));
            ctx.Response.Close();
        });
        var services = new AppServices(SafeFiles.Child(directory, "flow-data-" + Guid.NewGuid().ToString("N")));
        var target = new MainWindow(services) { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = 0 };
        target.Show(); target.Navigate(1); target.UpdateLayout();
        foreach (var child in LogicalChildren(target).OfType<Expander>())
            if (Equals(child.Header, "使用已有 API 密钥")) child.IsExpanded = true;
        target.UpdateLayout();
        try
        {
            Find<TextBox>(target, "base-url").Text = $"http://127.0.0.1:{port}/v1";
            Find<PasswordBox>(target, "api-key").Password = "fixture-ui-local-only";
            await target.SetAppearance("paper");
            if (Find<PasswordBox>(target, "api-key").Password != "fixture-ui-local-only")
                throw new InvalidOperationException("Appearance change discarded an unsaved connection.");
            await Click(target, "save-load-connection");
            var model = Find<ComboBox>(target, "connection-model");
            if (model.Items.Count != 1) throw new InvalidOperationException("Fetched models were lost during profile selection.");
            model.SelectedIndex = 0;
            await Click(target, "apply-model");
            if (services.Store.Active.Model != "fixture-ui-model" || services.Store.Key(services.Store.Active) != "fixture-ui-local-only")
                throw new InvalidOperationException("UI model or credential did not persist.");
            var secretCount = Directory.EnumerateFiles(Path.Combine(services.Store.Root, "credentials")).Count();
            await Click(target, "apply-model");
            if (secretCount != Directory.EnumerateFiles(Path.Combine(services.Store.Root, "credentials")).Count())
                throw new InvalidOperationException("Repeated apply duplicated credentials.");
            Find<TextBox>(target, "base-url").Text = "invalid-address";
            await Click(target, "save-load-connection");
            if (!target.OperationText.Text.Contains("HTTPS") || services.Store.Active.BaseUrl == "invalid-address")
                throw new InvalidOperationException("Invalid URL did not produce a recoverable UI error.");
            await serving;
            SafeFiles.AtomicWrite(SafeFiles.Child(directory, "ui-flow-result.json"), JsonSerializer.Serialize(
                new { result = "passed", fixtureOnly = true, savedCatalogSelection = true, modelApplied = true,
                    repeatConfigurationIdempotent = true, encryptedCredentials = true, invalidUrlRecovery = true }, JsonData.Options));
        }
        finally { listener.Stop(); target.Close(); }
    }
    private static async Task Click(MainWindow window, string id)
    {
        Find<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 500 && window.Busy; i++) await Task.Delay(10);
        if (window.Busy) throw new InvalidOperationException("UI operation timed out.");
        window.UpdateLayout();
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T expected && AutomationProperties.GetAutomationId(root) == id) return expected;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Find<T>(VisualTreeHelper.GetChild(root, i), id); }
            catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(id);
    }
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalChildren(child)) yield return descendant;
    }
}
