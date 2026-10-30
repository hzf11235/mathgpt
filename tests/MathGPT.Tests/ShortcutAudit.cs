using System.Runtime.InteropServices;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class ShortcutAudit
{
    public static Task Run(string root)
    {
        var desktop = Path.Combine(root, "隔离 桌面");
        var store = new StateStore(Path.Combine(root, "用户 数据"));
        var service = new ClientService(store);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test host.");
        var app = ClientService.ApplicationShortcut(executable, desktop);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            dynamic own = shell.CreateShortcut(app);
            try { Require(Same(own.TargetPath, executable) && own.Arguments == "", "Application shortcut target."); }
            finally { Marshal.ReleaseComObject(own); }
            foreach (var kind in new[] { ClientKind.Harness, ClientKind.CodexDesktop, ClientKind.ClaudeDesktop })
            {
                var installation = new ClientInstallation { Kind = kind, Executable = executable };
                store.State.Clients.Add(installation);
                if (DesktopConfiguration.IsDesktop(kind))
                { installation.PackageFamily = "Fixture.Desktop_123"; installation.ApplicationId = "Main"; }
                var path = service.Shortcut(installation, executable, desktop);
                Require(Path.GetFileName(path) == kind switch
                { ClientKind.Harness => "DeepSeek Harness.lnk", ClientKind.CodexDesktop => "Codex.lnk", _ => "Claude.lnk" }, "Original desktop application names.");
                dynamic bare = shell.CreateShortcut(path);
                try
                {
                    if (kind == ClientKind.Harness) Require(Same(bare.TargetPath, executable) && bare.Arguments == "", "Unconfigured Harness target.");
                    else Require(Path.GetFileName((string)bare.TargetPath).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                        ((string)bare.Arguments).Contains("shell:AppsFolder\\Fixture.Desktop_123!Main"), "MSIX application activation.");
                }
                finally { Marshal.ReleaseComObject(bare); }
                installation.ProfileId = "fixture-profile";
                installation.ConfigDirectory = Path.Combine(root, "配置");
                Require(service.Shortcut(installation, executable, desktop) == path, "Repeat uses one stable shortcut.");
                dynamic linked = shell.CreateShortcut(path);
                try
                {
                    Require(Same(linked.TargetPath, executable), "Configured target uses credential-aware MathGPT.");
                    var args = (string)linked.Arguments;
                    Require(args.Contains("\"--launch\" \"" + kind + "\"") && args.Contains("\"--profile\" \"fixture-profile\"") &&
                        args.Contains("\"--data-dir\" \"" + store.Root + "\""), "Quoted profile and isolated data arguments.");
                    Require(!args.Contains("api_key", StringComparison.OrdinalIgnoreCase) && !args.Contains("sk-"), "No API credentials in shortcut.");
                    Require(((string)linked.IconLocation).Contains(executable), "Client icon assigned.");
                }
                finally { Marshal.ReleaseComObject(linked); }
            }
            Require(Directory.GetFiles(desktop, "*.lnk").Length == 4, "No duplicate shortcuts.");
            var personalPath = ClientService.ShortcutPath(ClientKind.ClaudeDesktop, desktop);
            dynamic personal = shell.CreateShortcut(personalPath);
            try { personal.Description = "Personal desktop shortcut"; personal.Arguments = "--keep-my-arguments"; personal.Save(); }
            finally { Marshal.ReleaseComObject(personal); }
            var personalBytes = File.ReadAllBytes(personalPath);
            store.State.Clients.Add(new() { Kind = ClientKind.Claude, Executable = executable });
            service.CreateAvailableShortcuts(executable, desktop);
            Require(File.ReadAllBytes(personalPath).SequenceEqual(personalBytes), "Existing personal shortcut is preserved byte for byte.");
            Require(!Directory.GetFiles(desktop, "*.lnk").Any(p => Path.GetFileName(p).Contains("CLI") || Path.GetFileName(p).StartsWith("MathGPT · ")),
                "CLI and branded aliases are not created automatically.");
        }
        finally { Marshal.ReleaseComObject(shell); }
        return Task.CompletedTask;
    }
    private static bool Same(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
