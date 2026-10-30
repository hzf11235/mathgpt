using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MathGPT.Core;

namespace MathGPT.Tests;

internal static class NetworkPreferencesAudit
{
    public static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        void Require(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException(reason);
            passed.Add(reason); Console.WriteLine("PASS " + reason);
        }
        var store = new StateStore(root);
        using var handler = new CountHandler();
        using var http = new HttpClient(handler);
        async Task Send() { using var response = await http.GetAsync("https://network-fixture.invalid/check"); }
        Require(!store.Network.Configured && !File.Exists(Path.Combine(root, NetworkPreferences.FileName)), "new and headless profiles have no implicit choice");
        await store.Network.RunAutomatic(AutomaticNetworkOperation.Updates, Send);
        await store.Network.RunAutomatic(AutomaticNetworkOperation.AccountRefresh, Send);
        Require(handler.Requests == 0, "unconfirmed automatic updates and account refresh send zero HTTP requests");
        Require(!NetworkPreferences.ApplyCommand([], root), "ordinary startup never persists a synthetic consent");

        Require(NetworkPreferences.ApplyCommand(["--set-network-options", "1", "1"], root), "installer can persist an explicit pair of choices without user credentials");
        var restored = new StateStore(root);
        await restored.Network.RunAutomatic(AutomaticNetworkOperation.Updates, Send);
        await restored.Network.RunAutomatic(AutomaticNetworkOperation.AccountRefresh, Send);
        Require(restored.Network.Configured && handler.Requests == 2, "confirmed choices survive restart and allow their respective operations");

        restored.State.WorkDirectory = Path.Combine(root, "existing-workspace"); restored.Save();
        var statePath = Path.Combine(root, "settings.json");
        var before = SHA256.HashData(File.ReadAllBytes(statePath));
        restored.Network.Save(false, true);
        var upgraded = new StateStore(root);
        Require(!upgraded.Network.AutomaticUpdates && upgraded.Network.AutomaticAccountRefresh, "upgrade preserves separately chosen update and account preferences");
        Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(statePath))), "saving privacy choices never overwrites account, key or workspace state");
        await upgraded.Network.RunAutomatic(AutomaticNetworkOperation.Updates, Send);
        await upgraded.Network.RunAutomatic(AutomaticNetworkOperation.AccountRefresh, Send);
        Require(handler.Requests == 3, "disabled updates cannot prevent a permitted account refresh");

        upgraded.Network.Save(false, false);
        await store.Network.RunAutomatic(AutomaticNetworkOperation.Updates, Send);
        await store.Network.RunAutomatic(AutomaticNetworkOperation.AccountRefresh, Send);
        Require(handler.Requests == 3, "existing instances observe disabled choices and send no further automatic HTTP");
        await Send();
        Require(handler.Requests == 4, "manual operations still execute with both automatic operations disabled");

        var preferencePath = Path.Combine(root, NetworkPreferences.FileName);
        var original = File.ReadAllBytes(preferencePath);
        try { NetworkPreferences.ApplyCommand(["--set-network-options", "yes", "1"], root); throw new InvalidOperationException("Invalid options accepted"); }
        catch (UserException) { }
        Require(original.SequenceEqual(File.ReadAllBytes(preferencePath)), "invalid headless arguments do not change saved choices");
        foreach (var damaged in new[] {
            "[Network]\nVersion=2\nAutoUpdates=1\nAutoAccountRefresh=1\n",
            "[Network]\nVersion=1\nAutoUpdates=1\nAutoUpdates=0\nAutoAccountRefresh=1\n",
            "[Network]\nVersion=1\nAutoUpdates=true\nAutoAccountRefresh=1\n" })
        {
            File.WriteAllText(preferencePath, damaged);
            await store.Network.RunAutomatic(AutomaticNetworkOperation.Updates, Send);
            await store.Network.RunAutomatic(AutomaticNetworkOperation.AccountRefresh, Send);
            Require(!store.Network.Configured && handler.Requests == 4, "unknown, duplicate or malformed preferences fail closed");
        }
        File.WriteAllBytes(preferencePath, original);
        Require(new StateStore(root).Network.Configured, "valid saved choices can be restored without touching other user data");
        File.WriteAllText(Path.Combine(root, "network-preferences-result.json"), JsonSerializer.Serialize(new
        {
            passed = passed.Count, checks = passed, automaticHttpRequests = 3, manualHttpRequests = 1,
            externalNetworkUsed = false, realProfileUsed = false, realInstallPerformed = false
        }, JsonData.Options));
    }

    private sealed class CountHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
