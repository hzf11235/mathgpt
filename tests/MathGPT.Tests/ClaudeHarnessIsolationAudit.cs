using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class ClaudeHarnessIsolationAudit
{
    internal const string ClaudeSupportMarkers = "configLibrary deploymentMode process.env.LOCALAPPDATA user-data-dir requestSingleInstanceLock setPath(\"userData\" -3p";
    public static Task Run(string root)
    {
        var checks = 0;
        void Require(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException(text);
            checks++;
        }
        void Reject(Action action, string text)
        {
            try { action(); } catch (UserException) { checks++; return; }
            throw new InvalidOperationException(text);
        }
        var store = new StateStore(Path.Combine(root, "state 中文 空格"));
        var profile = new ConnectionProfile { Name = "隔离 API", Model = "claude-opus-5-5", Group = "claude-pending" };
        store.SaveProfile(profile, "fixture-isolation-private-only");
        var layout = ClaudeHarnessIsolation.ClaudeProfile(store, profile);
        var otherProfile = new ConnectionProfile(); store.SaveProfile(otherProfile, "fixture-second-isolated-key");
        var other = ClaudeHarnessIsolation.ClaudeProfile(store, otherProfile);
        Require(layout.Root != other.Root && layout.UserData != other.UserData, "Independent API connections shared a cookie root.");
        Require(layout.UserData == Path.Combine(layout.LocalData, "Claude-3p") &&
            layout.Library == Path.Combine(layout.UserData, "configLibrary"), "Signed Claude Windows 3p paths diverged.");
        var mac = ClaudeHarnessIsolation.ClaudeProfile(store, profile, false);
        Require(mac.UserData.EndsWith("Claude-3p", StringComparison.Ordinal) && mac.Library.StartsWith(mac.UserData + Path.DirectorySeparatorChar),
            "Mac 3p loader would select a different profile directory.");
        var work = Path.Combine(root, "工作目录");
        var install = new ClientInstallation { Kind = ClientKind.ClaudeDesktop,
            Executable = Environment.ProcessPath!, ConfigDirectory = layout.Library, ProfileId = profile.Id, WorkDirectory = work,
            PackageFamily = "Claude_fixture", ApplicationId = "Claude" };
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var oldLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var launch = ClaudeHarnessIsolation.ClaudeLaunch(store, install, profile);
        Require(!launch.UseShellExecute && launch.FileName == install.Executable && !launch.FileName.EndsWith("explorer.exe"),
            "MSIX activation dropped the private environment.");
        Require(launch.ArgumentList.Count == 1 && launch.ArgumentList[0] == "--user-data-dir=" + layout.UserData,
            "Electron profile argument was lost or shell-quoted into several arguments.");
        Require(launch.Environment["LOCALAPPDATA"] == layout.LocalData && launch.Environment["APPDATA"] == layout.RoamingData &&
            launch.Environment["CLAUDE_CONFIG_DIR"] == layout.CodeConfig, "Desktop and Code state were not both isolated.");
        Require(!launch.Environment.ContainsKey("CLAUDE_USER_DATA_DIR"), "Relied on a signed-build-ignored Claude environment override.");
        Require(Environment.GetEnvironmentVariable("APPDATA") == oldAppData && Environment.GetEnvironmentVariable("LOCALAPPDATA") == oldLocal,
            "Launch changed the host process or persistent user environment.");
        Require(!File.Exists(Path.Combine(layout.UserData, "Cookies")) && !File.Exists(Path.Combine(layout.CodeConfig, ".credentials.json")),
            "Original account credentials were copied into the isolated API profile.");
        install.ConfigDirectory = Path.Combine(root, "original-Claude", "configLibrary");
        Reject(() => ClaudeHarnessIsolation.ClaudeLaunch(store, install, profile), "Legacy global Claude configuration was accepted.");
        install.ConfigDirectory = layout.Library; install.ConfigPending = true;
        Reject(() => ClaudeHarnessIsolation.ClaudeLaunch(store, install, profile), "Pending configuration launched the wrong account.");
        install.ConfigPending = false; install.Executable = "relative-claude.exe";
        Reject(() => ClaudeHarnessIsolation.ClaudeLaunch(store, install, profile), "Relative executable allowed application substitution.");
        Reject(() => ClaudeHarnessIsolation.ProfileRoot(store, ClientKind.CodexDesktop, profile), "Codex accidentally entered a different vendor's profile implementation.");
        var invalid = new ConnectionProfile { Id = "../../outside" };
        Reject(() => ClaudeHarnessIsolation.ClaudeProfile(store, invalid), "Profile path traversal was accepted.");
        var secrets = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var name in new[] { "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_MODEL", "CLAUDE_CODE_OAUTH_TOKEN", "OPENAI_API_KEY", "DSH_HOME", "DISABLE_AUTOUPDATER" })
            secrets.Environment[name] = "fixture-inherited";
        secrets.Environment["PATH"] = "fixture-path-kept";
        ClaudeHarnessIsolation.ClearProviderEnvironment(secrets);
        Require(secrets.Environment["PATH"] == "fixture-path-kept" && !secrets.Environment.ContainsKey("ANTHROPIC_MODEL") &&
            !secrets.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN") && !secrets.Environment.ContainsKey("OPENAI_API_KEY") &&
            !secrets.Environment.ContainsKey("DISABLE_AUTOUPDATER"), "An inherited subscription/API identity or updater policy leaked.");
        var harness = new ClientInstallation { Kind = ClientKind.Harness, ProfileId = profile.Id, Executable = Environment.ProcessPath!,
            ConfigDirectory = ClaudeHarnessIsolation.ProfileRoot(store, ClientKind.Harness, profile) };
        var hstart = new ProcessStartInfo(harness.Executable) { UseShellExecute = false };
        hstart.Environment["MATHGPT_API_KEY"] = "fixture-selected-key";
        ClaudeHarnessIsolation.IsolateHarness(store, harness, profile, hstart);
        Require(hstart.Environment["DSH_HOME"] == harness.ConfigDirectory && hstart.ArgumentList.Single() ==
            "--user-data-dir=" + Path.Combine(harness.ConfigDirectory, "electron") && hstart.Environment["MATHGPT_API_KEY"] == "fixture-selected-key",
            "Harness backend and Electron state were not scoped together, or its selected secret was lost.");
        Reject(() => ClaudeHarnessIsolation.IsolateHarness(store, harness, profile, hstart), "Duplicate Electron profile arguments were accepted.");
        var raw = new ProcessStartInfo(harness.Executable) { UseShellExecute = false, Arguments = "--user-data-dir=old" };
        Reject(() => ClaudeHarnessIsolation.IsolateHarness(store, harness, profile, raw), "A conflicting raw launch argument was accepted.");
        harness.ConfigDirectory = Path.Combine(root, "original-harness");
        Reject(() => ClaudeHarnessIsolation.IsolateHarness(store, harness, profile, new(harness.Executable) { UseShellExecute = false }),
            "Original Harness account directory was reused.");

        install.Executable = Environment.ProcessPath!; install.ConfigDirectory = layout.Library;
        Directory.CreateDirectory(layout.Library);
        var sessionPath = Path.Combine(layout.Library, "mathgpt-launch-session.json");
        using var current = Process.GetCurrentProcess();
        void Session(int pid, long ticks, string executable) => SafeFiles.AtomicWrite(sessionPath, JsonSerializer.Serialize(new
        { ProcessId = pid, StartedUtcTicks = ticks, Executable = executable, ConfigurationDirectory = layout.Library }, JsonData.Options));
        Require(!ClaudeHarnessIsolation.Running(store, install), "An unstarted profile was marked running.");
        Session(current.Id, current.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        Require(ClaudeHarnessIsolation.Running(store, install), "A matching PID/start/executable/profile lease was ignored.");
        var sameLease = File.ReadAllBytes(sessionPath);
        Require(!ClaudeHarnessIsolation.Launch(store, install, profile) && File.ReadAllBytes(sessionPath).SequenceEqual(sameLease),
            "Opening an active profile replaced its owner lease or started a second process.");
        var separate = new ClientInstallation { Kind = ClientKind.ClaudeDesktop, ProfileId = otherProfile.Id, ConfigDirectory = other.Library };
        Require(!ClaudeHarnessIsolation.Running(store, separate), "Another profile or the user's original subscription was incorrectly blocked.");
        Session(current.Id, current.StartTime.ToUniversalTime().Ticks + 1, Environment.ProcessPath!);
        Require(!ClaudeHarnessIsolation.Running(store, install), "A reused PID with a different start time was considered active.");
        Session(current.Id, current.StartTime.ToUniversalTime().Ticks, Path.Combine(root, "other-client.exe"));
        Require(!ClaudeHarnessIsolation.Running(store, install), "A different executable adopted this profile's lease.");
        Session(int.MaxValue, DateTime.UtcNow.Ticks, Environment.ProcessPath!);
        Require(!ClaudeHarnessIsolation.Running(store, install), "An exited PID blocked the profile permanently.");
        SafeFiles.AtomicWrite(sessionPath, "{broken");
        Require(ClaudeHarnessIsolation.Running(store, install), "A corrupt known lease allowed a potentially running configuration to be overwritten.");
        File.Delete(sessionPath);
        using (var gate = new FileStream(Path.Combine(layout.Library, "mathgpt-launch.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
            Require(ClaudeHarnessIsolation.Running(store, install), "A concurrently starting profile allowed its configuration to be rewritten.");
        Require(!ClaudeHarnessIsolation.Running(store, install), "A released launch lock looked like a permanent running session.");

        var asar = Path.Combine(root, "capability-fixture.asar");
        WriteAsar(asar, ".vite/build/index.pre.js", ClaudeSupportMarkers);
        VendorProfileSupport.RequireAsar(ClientKind.ClaudeDesktop, asar); checks++;
        Reject(() => VendorProfileSupport.RequireAsar(ClientKind.Harness, asar), "Claude capability fixture was accepted as Harness.");
        WriteAsar(asar, ".vite/build/index.pre.js", ClaudeSupportMarkers.Replace("process.env.LOCALAPPDATA", "ignored"));
        Reject(() => VendorProfileSupport.RequireAsar(ClientKind.ClaudeDesktop, asar), "A changed vendor 3p loader silently reused the original account.");
        WriteAsar(asar, "lib/main.js", "DSH_HOME userData requestSingleInstanceLock profiles desktop");
        VendorProfileSupport.RequireAsar(ClientKind.Harness, asar); checks++;
        WriteAsar(asar, "lib/main.js", "DSH_HOME userData requestSingleInstanceLock profiles desktop", true);
        Reject(() => VendorProfileSupport.RequireAsar(ClientKind.Harness, asar), "An unpacked/symlinked source bypassed the audited ASAR entry.");
        WriteAsar(asar, "lib/main.js", "DSH_HOME userData requestSingleInstanceLock profiles desktop", offset: "9999999999");
        Reject(() => VendorProfileSupport.RequireAsar(ClientKind.Harness, asar), "Out-of-bounds ASAR reads were accepted.");
        File.WriteAllBytes(asar, [0, 1, 2]);
        Reject(() => VendorProfileSupport.RequireAsar(ClientKind.ClaudeDesktop, asar), "A truncated ASAR was accepted.");
        Console.WriteLine($"Claude/Harness isolation contracts: {checks} passed. Fixture launches only; no vendor app, sign-in or inference started.");
        return Task.CompletedTask;
    }

    internal static void WriteAsar(string path, string entry, string text, bool unpacked = false, string offset = "0")
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var node = new JsonObject(); var current = node;
        var parts = entry.Split('/');
        foreach (var part in parts[..^1])
        {
            var next = new JsonObject(); current["files"] = new JsonObject { [part] = next }; current = next;
        }
        current["files"] = new JsonObject { [parts[^1]] = new JsonObject { ["size"] = payload.Length, ["offset"] = offset, ["unpacked"] = unpacked } };
        var header = Encoding.UTF8.GetBytes(node.ToJsonString());
        var padding = (4 - header.Length % 4) % 4;
        Span<byte> prefix = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[4..], (uint)(8 + header.Length + padding));
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[8..], (uint)(4 + header.Length + padding));
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[12..], (uint)header.Length);
        using var file = File.Create(path); file.Write(prefix); file.Write(header); file.Write(new byte[padding]); file.Write(payload);
    }
}
