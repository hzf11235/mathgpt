using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MathGPT.Core;
using MathGPT.Mac;

internal static class MacContinuityAudit
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    public static int Run(MainWindow window)
    {
        var type = typeof(MainWindow);
        var panel = type.GetField("setup", Fields)!.GetValue(window)!;
        var panelType = panel.GetType();
        Require(window.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "继续网页配置")), "No website-continuation action is reachable.");
        panelType.GetMethod("ShowReady", Fields)!.Invoke(panel, [ClientKind.ClaudeDesktop, "claude-opus-5-5"]);
        var install = (Button)panelType.GetField("install", Fields)!.GetValue(panel)!;
        Require(install.IsVisible && Equals(install.Content, "安装并开始"), "A received website plan has no one-click continuation.");
        var detail = (TextBlock)panelType.GetField("detail", Fields)!.GetValue(panel)!;
        Require(detail.Text!.Contains("claude-opus-5-5") && detail.Text.Contains("复用已有应用"), "The plan summary lost the selected model or existing-install behavior.");
        type.GetMethod("SetBusy", Fields)!.Invoke(window, [true]);
        foreach (var name in new[] { "profiles", "models", "reasoning", "preferredClient", "name", "address", "secret" })
            Require(!((Control)type.GetField(name, Fields)!.GetValue(window)!).IsEnabled, "A pending authorization allowed configuration changes: " + name);
        Require(!install.IsEnabled, "A pending operation allowed repeated installation.");
        type.GetMethod("SetBusy", Fields)!.Invoke(window, [false]);
        Require(install.IsEnabled, "The ready plan did not recover after an operation.");
        var store = (StateStore)type.GetField("store", Fields)!.GetValue(window)!;
        var fresh = new ConnectionProfile { Name = "No key yet", Group = "universal" };
        store.State.Profiles.Add(fresh);
        var profiles = (ComboBox)type.GetField("profiles", Fields)!.GetValue(window)!;
        type.GetMethod("Fill", Fields)!.Invoke(window, [true]);
        type.GetField("loadingCatalog", Fields)!.SetValue(window, true);
        profiles.SelectedItem = fresh;
        Require(!((bool)type.GetField("loadingCatalog", Fields)!.GetValue(window)!) && install.IsEnabled,
            "Changing to a keyless connection left the previous catalog's busy state stuck.");
        Require(((ComboBox)type.GetField("models", Fields)!.GetValue(window)!).Items.Count == 0,
            "A new connection displayed the preceding key's models.");
        return 5;
    }
}
