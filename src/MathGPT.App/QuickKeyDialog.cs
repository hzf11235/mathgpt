using System.Windows;
using System.Windows.Controls;
using MathGPT.Core;

namespace MathGPT.App;

internal sealed class QuickKeyDialog : Window
{
    private sealed record Group(string Id, string Name) { public override string ToString() => Name; }
    private readonly MainWindow owner;
    private readonly TextBlock status = Ui.Hint("");
    private readonly StackPanel form = Ui.Stack();
    private readonly ComboBox groups = Ui.Select("quick-key-group");
    private readonly PasswordBox secret = Ui.Secret("quick-key-value");
    private readonly TextBox name = Ui.Input("quick-key-name", "MathGPT");
    private int createdId;
    private bool busy;
    private bool creationUnknown;
    private Button saveButton = null!;
    private QuickKeyDialog(MainWindow owner, bool create)
    {
        this.owner = owner; Owner = owner;
        Title = create ? "创建密钥" : "接入已有密钥"; Width = 490; SizeToContent = SizeToContent.Height;
        MinHeight = 220; MaxHeight = Math.Min(650, SystemParameters.WorkArea.Height - 40);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        form.Margin = new(24);
        var title = Ui.Text(Title, 22); title.FontWeight = FontWeights.SemiBold; form.Children.Add(title);
        if (create)
        {
            form.Children.Add(Ui.Field("名称", name)); form.Children.Add(Ui.Field("分组", groups));
            form.Children.Add(Ui.Hint("可使用分组内的全部模型，费用从账户余额扣除。"));
        }
        else form.Children.Add(Ui.Field("API Key", secret));
        var apply = saveButton = Ui.Button(create ? "创建并使用" : "保存并使用", async () =>
        {
            if (busy || creationUnknown) return;
            busy = true; form.IsEnabled = false; status.Text = "正在接入…";
            try
            {
                var site = owner.Services.Site;
                string value;
                if (create)
                {
                    var group = groups.SelectedItem as Group ?? throw new UserException("请选择可用分组。");
                    if (createdId == 0)
                    {
                        creationUnknown = true;
                        createdId = await site.CreateQuickKey(name.Text.Trim().Length > 0 ? name.Text.Trim() : "MathGPT", group.Id);
                        if (createdId > 0) creationUnknown = false;
                    }
                    if (createdId <= 0) throw new UserException("网站未确认创建结果，请先到密钥列表核对。");
                    value = await site.KeySecret(createdId);
                }
                else value = secret.Password;
                await QuickConnection.Save(owner.Services.Store, owner.Services.Catalog, value,
                    owner.Services.Store.State.PreferredClient, create && name.Text.Trim().Length > 0 ? name.Text.Trim() : "MathGPT", websiteKeyId: create ? createdId : null);
                secret.Clear(); busy = false; DialogResult = true;
            }
            catch (Exception error) { status.Text = creationUnknown ? "创建结果尚未确认，请到密钥列表核对，再接入已有密钥。" : Privacy.Friendly(error); }
            finally { busy = false; form.IsEnabled = true; groups.IsEnabled = createdId == 0 && !creationUnknown; name.IsEnabled = groups.IsEnabled; saveButton.IsEnabled = !creationUnknown; }
        }, true, "quick-key-save");
        form.Children.Add(Ui.Actions(apply, Ui.Button("取消", Close)));
        if (create) form.Children.Add(Ui.Button("查看密钥列表", () => MainWindow.OpenUrl("https://mathgpt.xyz/keys")));
        form.Children.Add(status); Content = form;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        if (create) Loaded += async (_, _) =>
        {
            form.IsEnabled = false;
            try
            {
                var routes = CatalogService.ParsePricing(await owner.Services.Site.Pricing());
                var available = routes.Where(r => r.Available).GroupBy(r => r.Group)
                    .Select(g => new Group(g.Key, g.First().GroupName)).ToList();
                available.Insert(0, new("universal", "万能密钥"));
                groups.ItemsSource = available; groups.SelectedIndex = 0;
            }
            catch (Exception error) { status.Text = Privacy.Friendly(error); }
            finally { form.IsEnabled = true; }
        };
    }
    public static void Show(MainWindow owner, bool create)
    {
        if (create && !owner.Services.Site.SignedIn)
        {
            owner.Navigate(1); owner.Status("先添加 MathGPT 账号，即可在这里创建密钥。"); return;
        }
        if (new QuickKeyDialog(owner, create).ShowDialog() == true)
        { owner.Navigate(5); owner.Status("密钥已接入，选择应用后点“切换并打开”。"); }
    }
}
