using System.Windows;
using MathGPT.Core;

namespace MathGPT.App;

public partial class App : Application
{
    private AppInstance? instance;
    public static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MathGPT.exe");
    public static string DefaultDataRoot()
    {
        var deployed = Path.GetFullPath(@"D:\Codex\Apps\mathgpt-desktop\current").TrimEnd('\\');
        if (Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').Equals(deployed, StringComparison.OrdinalIgnoreCase))
            return @"D:\Codex\Data\mathgpt-desktop";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MathGPT", "Data");
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (await SelfUpdateService.TryRunApplyWorker(e.Args)) { Shutdown(Environment.ExitCode); return; }
            var dataRoot = DefaultDataRoot();
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length) dataRoot = e.Args[dataIndex + 1];
            if (NetworkPreferences.ApplyCommand(e.Args, dataRoot)) { Shutdown(); return; }
            if (e.Args.Contains("--check-update-and-exit"))
            {
                var code = await SelfUpdateEntry.CheckAndExit(AppContext.BaseDirectory, dataRoot,
                    typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.7.0", "windows",
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
                Shutdown(code); return;
            }
            if (e.Args.Contains("--credential"))
            {
                // Command-backed provider authentication. No window, IPC, logs or credentials in argv.
                var at = Array.IndexOf(e.Args, "--credential") + 1;
                if (at >= e.Args.Length) throw new UserException("凭据参数无效。");
                var store = new StateStore(dataRoot);
                var profile = store.State.Profiles.FirstOrDefault(p => p.Id == UrlPolicy.SafeId(e.Args[at]))
                    ?? throw new UserException("连接不存在。");
                var secret = e.Args.Contains("--builtin") ? await new BuiltinGateway(store).Ensure(profile) : store.Key(profile) ?? throw new UserException("凭据不存在。");
                await using var output = Console.OpenStandardOutput();
                await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(secret + "\n"));
                await output.FlushAsync(); Shutdown(); return;
            }
            if (Environment.OSVersion.Version.Build < 19045)
                throw new UserException("MathGPT 需要 Windows 10 22H2（19045）或更高版本。");
            if (e.Args.Contains("--create-shortcuts"))
            {
                new ClientService(new StateStore(dataRoot)).CreateAvailableShortcuts(Executable);
                Shutdown(); return;
            }
            AppCommand command = new("show");
            if (e.Args.Contains("--launch"))
            {
                var kindIndex = Array.IndexOf(e.Args, "--launch") + 1;
                var profileIndex = Array.IndexOf(e.Args, "--profile") + 1;
                if (kindIndex >= e.Args.Length || profileIndex <= 0 || profileIndex >= e.Args.Length ||
                    !Enum.TryParse<ClientKind>(e.Args[kindIndex], out var kind)) throw new UserException("快捷方式参数无效，请重新创建。");
                command = new("launch", kind, UrlPolicy.SafeId(e.Args[profileIndex]));
            }
            instance = new(dataRoot);
            if (!instance.Primary)
            {
                await instance.Send(command); Shutdown(); return;
            }
            var services = new AppServices(dataRoot);
            if (command.Action == "launch")
            {
                var installation = services.Store.State.Clients.FirstOrDefault(c => c.Kind == command.Kind) ?? throw new UserException("客户端未安装。");
                if (command.Kind == ClientKind.ClaudeDesktop && !(await ClaudeDesktopReadiness.Check(installation)).Ready)
                {
                    var repairWindow = new MainWindow(services); MainWindow = repairWindow;
                    repairWindow.Show(); instance.Listen(repairWindow);
                    await repairWindow.LaunchClient(command.Kind, command.Profile); return;
                }
                var profile = services.Store.State.Profiles.FirstOrDefault(p => p.Id == command.Profile) ?? throw new UserException("连接已被移除。");
                var routes = await services.Catalog.Load(profile);
                var route = routes.FirstOrDefault(r => r.Id == profile.Model && r.Available) ?? throw new UserException("所选模型已不可用。");
                if (!installation.LocalRouting) services.Clients.Configure(installation, profile, route, routes, Executable);
                services.Clients.Launch(installation, profile);
                Shutdown(); return;
            }
            var window = new MainWindow(services);
            if (e.Args.Contains("--ui-smoke"))
            {
                window.ShowActivated = false; window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = 0;
            }
            MainWindow = window; window.Show();
            instance.Listen(window);
            if (!e.Args.Contains("--ui-smoke"))
            {
                _ = window.StartUpdateChecks();
                try { services.Clients.CreateAvailableShortcuts(Executable); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or UserException)
                { window.Status("桌面快捷方式未能创建，可在「客户端」更多操作中重试。"); }
            }
            if (e.Args.Contains("--ui-smoke"))
            {
                var index = Array.IndexOf(e.Args, "--ui-smoke");
                var output = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(dataRoot, "ui-smoke");
                await UiSmoke.Run(window, output);
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--credential")) { Shutdown(1); return; }
            if (e.Args.Contains("--ui-smoke"))
            {
                var at = Array.IndexOf(e.Args, "--ui-smoke");
                if (at + 1 < e.Args.Length)
                {
                    try { SafeFiles.AtomicWrite(SafeFiles.Child(e.Args[at + 1], "ui-failure.json"), Privacy.Clean(ex.GetType().Name + ": " + ex.Message)); }
                    catch (IOException) { }
                    catch (UserException) { }
                }
                Shutdown(1); return;
            }
            MessageBox.Show(Privacy.Friendly(ex), "MathGPT", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose(); SelfUpdateService.CommitNormalExit(e.ApplicationExitCode); base.OnExit(e);
    }
}
