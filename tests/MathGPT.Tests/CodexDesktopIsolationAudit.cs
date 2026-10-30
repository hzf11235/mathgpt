using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class CodexDesktopIsolationAudit
{
    private static int checks;
    private static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action, string message)
    {
        checks++;
        try { action(); } catch (UserException) { return; }
        throw new InvalidOperationException(message);
    }

    public static Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var store = new StateStore(Path.Combine(root, "state"));
        var profile = new ConnectionProfile { Model = "gpt-6.1-sol", Group = "gpt-pro" };
        store.State.Profiles.Add(profile);
        var executable = Path.Combine(root, "vendor", "ChatGPT.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "not executed: synthetic signature-independent launch fixture");
        var asar = Path.Combine(Path.GetDirectoryName(executable)!, "resources", "app.asar");
        MakeAsar(asar, "process.env.CODEX_HOME; app.setPath('userData',process.env.CODEX_ELECTRON_USER_DATA_PATH); app.requestSingleInstanceLock();");
        Require(CodexDesktopIsolation.SupportsIsolation(asar), "Supported startup source was rejected.");
        var install = new ClientInstallation { Kind = ClientKind.CodexDesktop, ProfileId = profile.Id,
            Executable = executable, ConfigDirectory = CodexDesktopIsolation.ConfigurationDirectory(store, profile), WorkDirectory = root };
        Directory.CreateDirectory(install.ConfigDirectory);
        Require(install.ConfigDirectory.StartsWith(store.Root + Path.DirectorySeparatorChar), "Codex config escaped owned data root.");
        Require(CodexDesktopIsolation.IsConfigured(store, install, profile), "Independent configuration was not recognized.");
        var priorEnvironment = new Dictionary<string, string?>();
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_ACCESS_TOKEN", "CODEX_HOME", "CODEX_ELECTRON_USER_DATA_PATH", "CODEX_APP_SERVER_BIN", "ELECTRON_RUN_AS_NODE", "NODE_OPTIONS" })
        {
            priorEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, "fixture-inherited-private-value");
        }
        try
        {
            var launch = CodexDesktopIsolation.LaunchInfo(store, install, profile);
            Require(!launch.UseShellExecute && launch.FileName == executable, "Explorer activation would discard isolated environment.");
            Require(launch.Environment["CODEX_HOME"] == install.ConfigDirectory, "Backend home was not isolated.");
            Require(launch.Environment["CODEX_ELECTRON_USER_DATA_PATH"] == CodexDesktopIsolation.ElectronDirectory(install.ConfigDirectory), "Electron account and cookie data was not isolated.");
            Require(launch.ArgumentList.Single() == "--user-data-dir=" + CodexDesktopIsolation.ElectronDirectory(install.ConfigDirectory), "Electron single-instance argument was not isolated.");
            foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_ACCESS_TOKEN", "CODEX_APP_SERVER_BIN", "ELECTRON_RUN_AS_NODE", "NODE_OPTIONS" })
                Require(!launch.Environment.ContainsKey(name), "Inherited account or runtime override was retained: " + name);
            Require(Environment.GetEnvironmentVariable("CODEX_HOME") == "fixture-inherited-private-value", "Launcher mutated the parent's environment.");
        }
        finally { foreach (var pair in priorEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        Require(!CodexDesktopIsolation.Running(store, install), "A user's existing unrelated Codex process was treated as this profile.");
        var before = install.ConfigDirectory;
        install.ConfigDirectory = Path.Combine(root, "original-user", ".codex");
        Reject(() => CodexDesktopIsolation.LaunchInfo(store, install, profile), "Global subscription configuration could be launched as an API profile.");
        install.ConfigDirectory = before;
        var other = new ConnectionProfile();
        Reject(() => CodexDesktopIsolation.LaunchInfo(store, install, other), "Another API profile was confused with this profile.");
        MakeAsar(asar, "process.env.CODEX_HOME;");
        Require(!CodexDesktopIsolation.SupportsIsolation(asar), "CODEX_HOME alone was mistaken for full desktop isolation.");
        Reject(() => CodexDesktopIsolation.LaunchInfo(store, install, profile), "Unknown vendor startup layout fell back to global data.");
        MakeAsar(asar, "CODEX_HOME CODEX_ELECTRON_USER_DATA_PATH userData requestSingleInstanceLock", long.MaxValue.ToString());
        Require(!CodexDesktopIsolation.SupportsIsolation(asar), "Out-of-bounds ASAR offset was accepted.");
        File.WriteAllBytes(asar, [4, 0, 0, 0, 255, 255, 255, 255]);
        Require(!CodexDesktopIsolation.SupportsIsolation(asar), "Truncated ASAR was accepted.");
        Console.WriteLine($"Codex desktop isolation audit passed: {checks} checks; synthetic launch only, original account untouched.");
        return Task.CompletedTask;
    }

    internal static void MakeAsar(string path, string source, string offset = "0")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = Encoding.UTF8.GetBytes(source);
        var header = JsonSerializer.SerializeToUtf8Bytes(new { files = new Dictionary<string, object> {
            [".vite"] = new { files = new Dictionary<string, object> { ["build"] = new { files = new Dictionary<string, object> {
                ["main.js"] = new { size = content.Length, offset }
            } } } }
        } });
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((uint)4); writer.Write((uint)header.Length + 8); writer.Write((uint)header.Length + 4); writer.Write((uint)header.Length);
        writer.Write(header); writer.Write(content);
    }
}
