using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MathGPT.Core;

namespace MathGPT.Mac;

internal static class Program
{
    internal static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MathGPT");
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (SelfUpdateService.TryRunApplyWorker(args).GetAwaiter().GetResult()) return Environment.ExitCode;
            var index = Array.IndexOf(args, "--data-dir");
            if (index >= 0 && index + 1 < args.Length) Root = SafeFiles.DirectoryPath(args[index + 1]);
            if (args.Contains("--check-update-and-exit"))
            {
                var bundle = Directory.GetParent(Environment.ProcessPath ?? "")?.Parent?.Parent?.FullName
                    ?? throw new UserException("请从完整的 MathGPT.app 打开。");
                var code = SelfUpdateEntry.CheckAndExit(bundle, Root, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.7.0", "mac",
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64").GetAwaiter().GetResult();
                SelfUpdateService.CommitNormalExit(code); return code;
            }
            if (args.Contains("--self-check"))
            {
                var report = MacSelfCheck.Run(Root).GetAwaiter().GetResult();
                Console.Out.WriteLine(report.Summary); Console.Out.WriteLine(report.Path); return report.Passed ? 0 : 2;
            }
            if (args.Length >= 2 && args[0] == "--credential")
            {
                var store = new StateStore(Root); var profile = store.State.Profiles.Single(x => x.Id == UrlPolicy.SafeId(args[1]));
                var secret = args.Contains("--builtin") ? new BuiltinGateway(store).Ensure(profile).GetAwaiter().GetResult() : store.Key(profile);
                if (string.IsNullOrEmpty(secret)) return 2;
                Console.Out.Write(secret); return 0;
            }
            if (args.Length >= 4 && args[0] == "--launch" && args[2] == "--profile")
            {
                var store = new StateStore(Root); var kind = Enum.Parse<ClientKind>(args[1]); var profile = store.State.Profiles.Single(x => x.Id == UrlPolicy.SafeId(args[3]));
                var clients = new MacClients(store);
                var installation = clients.Detect(kind).GetAwaiter().GetResult() ?? throw new UserException("客户端未安装。");
                var routes = new CatalogService(store, new WebsiteClient(store)).Load(profile).GetAwaiter().GetResult();
                clients.Configure(installation, profile, routes).GetAwaiter().GetResult();
                clients.Launch(kind, profile).GetAwaiter().GetResult(); return 0;
            }
            if (!OperatingSystem.IsMacOS()) return 3;
            Build().StartWithClassicDesktopLifetime(args); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(Privacy.Friendly(error)); return 1; }
    }
    public static AppBuilder Build() => AppBuilder.Configure<MacApplication>().UsePlatformDetect();
}

public sealed class MacApplication : Application
{
    public override void Initialize() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; Styles.Add(new FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, e) => SelfUpdateService.CommitNormalExit(e.ApplicationExitCode);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
