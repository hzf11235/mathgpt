using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using MathGPT.Mac;

internal static class DesktopMirrorUiAudit
{
    internal static void Run(MainWindow window, string output)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(MainWindow);
        var pause = (Button)type.GetField("pauseDownload", flags)!.GetValue(window)!;
        var status = (TextBlock)type.GetField("status", flags)!.GetValue(window)!;
        if (pause.IsVisible) throw new Exception("Download pause action must be hidden at rest.");
        using var cancellation = new CancellationTokenSource();
        type.GetField("clientDownload", flags)!.SetValue(window, cancellation);
        type.GetMethod("SetBusy", flags)!.Invoke(window, [true]);
        pause.IsVisible = true; status.Text = "下载本站完整安装包 · 1800 / 2200 MB";
        foreach (var width in new[] { 740, 1040 })
        {
            window.Width = width; window.Height = 780; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            status.Text = "下载本站完整安装包 · 1800 / 2200 MB";
            if (!pause.IsVisible || !pause.IsEnabled || pause.Bounds.Width <= 0 || pause.Bounds.Height <= 0)
                throw new Exception("A busy download must retain its usable pause action.");
            using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No pause preview frame.");
            bitmap.Save(Path.Combine(output, $"mac-{width}-download-pause.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!cancellation.IsCancellationRequested) throw new Exception("Pause must cancel the active download token.");
        type.GetField("clientDownload", flags)!.SetValue(window, null);
        pause.IsVisible = false; type.GetMethod("SetBusy", flags)!.Invoke(window, [false]);
        Console.WriteLine("Passed Mac download pause visibility, busy availability and cancellation contracts.");
    }
}
