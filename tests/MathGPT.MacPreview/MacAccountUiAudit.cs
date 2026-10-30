using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using MathGPT.Core;
using MathGPT.Mac;

internal static class MacAccountUiAudit
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public static async Task Run(string output)
    {
        var store = new StateStore(Path.Combine(output, "account-ui-fixture-" + Guid.NewGuid().ToString("N")));
        using var handler = new AccountHandler();
        var site = new WebsiteClient(store, "https://fixture.example", handler);
        await site.Login("alice", "fixture-password");
        var constructor = typeof(MainWindow).GetConstructor(Fields, null, [typeof(StateStore), typeof(WebsiteClient)], null)!;
        var window = (MainWindow)constructor.Invoke([store, site]);
        window.Show();
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Fields)!.GetValue(window)!;
        async Task Invoke(string name) => await (Task)typeof(MainWindow).GetMethod(name, Fields)!.Invoke(window, null)!;
        try
        {
            Require(window.Title == "MathGPT 安装助手", "Mac assistant title is inconsistent.");
            Require(Field<ComboBox>("savedAccounts").Items.Count == 1, "Saved account was not shown.");
            await Invoke("ShowAccountLogin");
            Require(site.Account?.Id == 1 && Field<StackPanel>("loginPanel").IsVisible, "Adding an account logged out the original account before authentication.");
            Field<TextBox>("username").Text = "challenge";
            Field<TextBox>("password").Text = "fixture-password";
            Field<CheckBox>("consent").IsChecked = true;
            await Invoke("Login");
            Require(Field<StackPanel>("verificationInputs").IsVisible && !Field<StackPanel>("loginInputs").IsVisible, "Pending MFA did not show a focused verification step.");
            await Invoke("CancelAccountLogin");
            Require(site.Account?.Id == 1 && site.SavedAccounts.Count == 1 && !Field<StackPanel>("verificationInputs").IsVisible,
                "Cancelling added-account verification did not restore the original session.");
            await Invoke("ShowAccountLogin");
            Field<TextBox>("username").Text = "bob";
            Field<TextBox>("password").Text = "fixture-password";
            await Invoke("Login");
            Require(site.Account?.Id == 2 && Field<ComboBox>("savedAccounts").Items.Count == 2 && !Field<StackPanel>("loginPanel").IsVisible,
                "Successful second login did not preserve two accounts and collapse the form.");
            Field<TabControl>("tabs").SelectedIndex = 2;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using (var bitmap = window.CaptureRenderedFrame())
                bitmap?.Save(Path.Combine(output, "mac-account-switcher.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Field<ComboBox>("savedAccounts").SelectedItem = site.SavedAccounts.Single(a => a.Id == 1);
            await Invoke("SwitchAccount");
            Require(site.Account?.Id == 1 && handler.LogoutCount == 0, "Switching an account invoked logout or selected the wrong identity.");
            typeof(MainWindow).GetMethod("SetBusy", Fields)!.Invoke(window, [true]);
            Require(!Field<ComboBox>("savedAccounts").IsEnabled, "An in-flight operation allowed another account switch.");
            typeof(MainWindow).GetMethod("SetBusy", Fields)!.Invoke(window, [false]);
            Require(Field<ComboBox>("savedAccounts").IsEnabled, "Saved-account selection did not recover after the operation.");
            await Invoke("LogoutAccount");
            Require(!site.SignedIn && site.SavedAccounts.Count == 1 && site.SavedAccounts[0].Id == 2,
                "Signing out removed the other saved account.");
            Console.WriteLine("Mac account UI: add, cancel MFA, switch and selected-account logout passed (Windows headless preview only).");
        }
        finally { window.Close(); }
    }

    private sealed class AccountHandler : HttpMessageHandler
    {
        public int LogoutCount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object data = new { };
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/status") data = new { quota_per_unit = 500000, turnstile_check = false };
            else if (path.EndsWith("encryption-key")) data = new { enabled = false };
            else if (path == "/api/user/login")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var name = body.RootElement.GetProperty("username").GetString();
                if (name == "challenge") data = new { require_verification = true, flow_token = "fixture-flow", methods = new[] { new { method = "2fa" } } };
                else data = new { access_token = "fixture-mac-account-" + (name == "alice" ? 1 : 2), access_expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
            }
            else if (path == "/api/user/self")
            {
                var id = request.Headers.Authorization?.Parameter?.EndsWith("1") == true ? 1 : 2;
                data = new { id, username = id == 1 ? "alice" : "bob", quota = 5000000, used_quota = 0 };
            }
            else if (path == "/api/token/") data = new { items = Array.Empty<object>() };
            else if (path == "/api/user/auth/logout") LogoutCount++;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { success = true, data }), Encoding.UTF8, "application/json") };
        }
    }
}
