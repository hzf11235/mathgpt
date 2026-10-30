using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MathGPT.Core;

namespace MathGPT.App.Pages;

public sealed class ConnectionsPage : UserControl
{
    private sealed record GroupChoice(string Value, string Label) { public override string ToString() => Label; }
    private readonly MainWindow window;
    private readonly AppServices services;
    private readonly ComboBox profiles = Ui.Select("connection-profiles");
    private readonly TextBox name = Ui.Input("connection-name", "MathGPT");
    private readonly TextBox baseUrl = Ui.Input("base-url", "https://mathgpt.xyz/v1");
    private readonly PasswordBox key = Ui.Secret("api-key");
    private readonly ComboBox group = Ui.Select("connection-group");
    private readonly ComboBox model = Ui.Select("connection-model");
    private readonly ComboBox protocol = Ui.Select("connection-protocol");
    private readonly ComboBox reasoning = Ui.Select("connection-reasoning");
    private readonly TextBox inputPrice = Ui.Input("custom-input-price");
    private readonly TextBox outputPrice = Ui.Input("custom-output-price");
    private readonly TextBox username = Ui.Input("website-username");
    private readonly PasswordBox password = Ui.Secret("website-password");
    private readonly PasswordBox entryPassword = Ui.Secret("website-entry-password");
    private readonly TextBox otp = Ui.Input("website-otp");
    private readonly TextBlock account = Ui.Text("登录后自动同步余额、可用分组与密钥。", 13, "#687384");
    private readonly TextBlock connectionHint = Ui.Hint("密钥仅在本机使用 Windows DPAPI 加密保存。");
    private readonly ComboBox keys = Ui.Select("website-keys");
    private readonly ComboBox savedAccounts = Ui.Select("saved-website-accounts");
    private readonly TextBox keyName = Ui.Input("new-key-name", "MathGPT 桌面");
    private readonly TextBox keyBudget = Ui.Input("new-key-budget", "10");
    private readonly ComboBox keyGroup = Ui.Select("new-key-group");
    private readonly DataGrid catalogGrid = new() { Height = 280 };
    private readonly TextBox search = Ui.Input("catalog-search");
    private ConnectionProfile? editing;
    private List<ModelRoute> routes = [];
    private List<ModelRoute> publicRoutes = [];
    private string flowToken = "";
    private bool populating;
    public ConnectionsPage(MainWindow window)
    {
        this.window = window; services = window.Services;
        group.IsEditable = true; group.Text = "default";
        Ui.ProtocolNames(protocol);
        protocol.ItemsSource = Enum.GetValues<WireProtocol>(); protocol.SelectedItem = WireProtocol.Responses;
        reasoning.ItemsSource = new[] { "服务默认" }; reasoning.SelectedIndex = 0;
        profiles.ItemsSource = services.Store.State.Profiles.ToList();
        void InvalidateModels()
        {
            if (populating) return;
            model.ItemsSource = null; routes = [];
            connectionHint.Text = "连接设置已变化，请重新保存并读取模型。";
        }
        baseUrl.TextChanged += (_, _) => InvalidateModels();
        key.PasswordChanged += (_, _) => InvalidateModels();
        group.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => InvalidateModels()));
        profiles.SelectionChanged += (_, _) => { if (!populating && profiles.SelectedItem is ConnectionProfile p) Fill(p); };
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedItem is ModelRoute route)
            {
                protocol.ItemsSource = route.Protocols;
                protocol.SelectedItem = route.Protocols.Contains(editing?.Protocol ?? WireProtocol.Responses)
                    ? editing?.Protocol ?? WireProtocol.Responses : route.Protocols.FirstOrDefault();
                SetReasoning(route.Id);
                connectionHint.Text = route.PriceText + "\n" + (editing?.GroupVerified == true
                    ? "分组来自网站本人密钥。" : "手动分组仅用于筛选报价，实际调用由 API Key 的绑定分组决定。");
            }
        };
        group.SelectionChanged += (_, _) =>
        {
            if (!populating) { model.ItemsSource = null; routes = []; }
        };
        protocol.SelectionChanged += (_, _) => { if (model.SelectedItem is ModelRoute r) SetReasoning(r.Id); };
        catalogGrid.Columns.Add(new DataGridTextColumn { Header = "模型", Binding = new Binding("Id"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 170 });
        catalogGrid.Columns.Add(new DataGridTextColumn { Header = "分组", Binding = new Binding("GroupName"), Width = 110 });
        catalogGrid.Columns.Add(new DataGridTextColumn { Header = "最终人民币价 / 状态", Binding = new Binding("PriceText"), Width = 340 });
        catalogGrid.Columns.Add(new DataGridTextColumn { Header = "协议", Binding = new Binding("ProtocolText"), Width = 155 });
        search.TextChanged += (_, _) => FilterCatalog();
        savedAccounts.ItemsSource = services.Site.SavedAccounts;
        var website = Ui.Card("MathGPT 账号", account,
            Ui.Field("已登录账号", savedAccounts),
            Ui.Actions(Ui.Button("切换账号", () => _ = window.Run(async ct =>
            {
                if (savedAccounts.SelectedItem is not SavedWebsiteAccount selected) throw new UserException("请先选择账号。");
                await services.Site.SwitchAccount(selected.Id, ct);
                await RefreshAccount(ct);
            }, "已切换账号"), id: "switch-website-account"),
                Ui.Button("添加账号", () => { flowToken = ""; services.Site.CancelAccountLogin(); username.Clear(); password.Clear(); username.Focus(); }),
                Ui.Button("移除所选", () => _ = window.Run(async ct =>
                {
                    if (savedAccounts.SelectedItem is not SavedWebsiteAccount selected) throw new UserException("请先选择账号。");
                    if (!Ui.Confirm(window, "从这台电脑移除该账号的登录？网站账号和余额保持不变。", "移除账号")) return;
                    if (services.Site.Account?.Id == selected.Id) await services.Site.Logout(ct);
                    else services.Site.ForgetSavedAccount(selected.Id);
                    savedAccounts.ItemsSource = services.Site.SavedAccounts;
                    UpdateAccountLabel();
                }))),
            Ui.Field("账号", username), Ui.Field("账号密码（不保存）", password),
            new Expander { Header = "网站另有入口密码？", Content = Ui.Stack(
                Ui.Hint("入口密码只用于访问网站，与账号密码、API Key 不同。"),
                Ui.Field("网站入口密码（不保存）", entryPassword),
                Ui.Button("通过网站入口", () => _ = window.Run(async ct =>
                {
                    var value = entryPassword.Password; entryPassword.Clear();
                    await services.Site.UnlockEntry(value, ct);
                }, "网站入口已通过。"))) },
            Ui.Field("两步验证码（网站要求时填写）", otp),
            Ui.Actions(Ui.Button("登录", () => _ = Login(), true, "website-login"),
                Ui.Button("刷新账号", () => _ = window.Run(RefreshAccount, "账号已同步")),
                Ui.Button("重置验证", () => { flowToken = ""; services.Site.CancelAccountLogin(); otp.Clear(); account.Text = "请重新填写账号密码并登录。"; }),
                Ui.Button("退出", () => _ = window.Run(async ct =>
                {
                    await services.Site.Logout(ct); account.Text = "已退出账号。API 连接独立保留。"; keys.ItemsSource = null;
                }, "已退出网站账号。"))),
            Ui.Field("本人 API 密钥", keys),
            Ui.Button("将所选密钥接入桌面", () => _ = ImportKey(), id: "import-website-key"),
            new Expander { Header = "新建 / 删除网站密钥", Content = Ui.Stack(
                Ui.Field("密钥名称", keyName), Ui.Field("绑定分组", keyGroup), Ui.Field("额度上限（元，30 天到期）", keyBudget),
                Ui.Hint("创建只设置密钥额度，不会充值或扣款。删除会立即使密钥失效。"),
                Ui.Actions(Ui.Button("新建有限额密钥", () => _ = window.Run(async ct =>
                {
                    if (!decimal.TryParse(keyBudget.Text, out var budget)) throw new UserException("请输入有效的人民币额度。");
                    var g = (keyGroup.SelectedItem as GroupChoice)?.Value ?? throw new UserException("请先刷新站内目录并选择分组。");
                    await services.Site.CreateKey(keyName.Text.Trim(), g, budget, ct);
                    await RefreshAccount(ct);
                }, "网站密钥已创建，可在上方选择接入。")),
                Ui.Button("删除所选密钥", () =>
                {
                    if (keys.SelectedItem is not WebsiteKey selected) { window.Status("请先选择本人密钥。", true); return; }
                    if (!Ui.Confirm(window, $"立即删除网站密钥“{selected.Name}”？使用它的客户端将无法继续调用。", "删除密钥")) return;
                    _ = window.Run(async ct => { await services.Site.RevokeKey(selected.Id, ct); await RefreshAccount(ct); }, "网站密钥已删除。");
                }))) });
        var connection = Ui.Card("直接连接 API",
            Ui.Field("已保存的连接", profiles),
            Ui.Actions(Ui.Button("新建连接", () => Fill(null)), Ui.Button("移除此连接", RemoveProfile)),
            Ui.Field("连接名称", name), Ui.Field("Base URL（通常以 /v1 结尾）", baseUrl),
            Ui.Field("API Key（留空可保留已存密钥）", key), Ui.Field("Key 绑定分组", group),
            Ui.Actions(Ui.Button("保存并读取模型", () => _ = SaveAndLoad(), true, "save-load-connection")),
            Ui.Field("模型", model), Ui.Field("API 协议", protocol), Ui.Field("推理强度", reasoning),
            connectionHint,
            new Expander { Header = "其他服务的价格（可选）", Content = Ui.Stack(
                Ui.Hint("单位为人民币 / 1M tokens；MathGPT 自动读取网站价格。"),
                Ui.Field("输入价", inputPrice), Ui.Field("输出价", outputPrice)) },
            Ui.Button("应用模型与协议", () => _ = window.Run(ct => { SaveSelection(); return Task.CompletedTask; }, "连接设置已保存。客户端下次打开时生效。"), true, "apply-model"));
        var catalog = Ui.Card("站内模型目录",
            Ui.Actions(Ui.Button("刷新目录与报价", () => _ = LoadPublicCatalog(), id: "refresh-public-catalog"),
                Ui.Button("价格说明 ↗", () => MainWindow.OpenUrl("https://mathgpt.xyz/pricing"))),
            Ui.Field("查找模型或分组", search), catalogGrid,
            Ui.Hint("报价来自网站，已含零售倍率。目录能力不等同于逐型号实测；暂停线路会显示不可用。"));
        Content = Ui.Stack(website,
            new Expander { Header = "使用已有 API 密钥", Content = connection },
            new Expander { Header = "模型价格", Content = catalog });
        if (services.Store.State.Profiles.FirstOrDefault(p => p.Id == services.Store.State.ActiveProfileId) is { } active)
            profiles.SelectedItem = active;
        if (services.Site.Account is { } current) account.Text = $"{current.Name} · 余额 ¥{current.Balance:0.####}";
        Loaded += (_, _) =>
        {
            if (services.Store.Network.Allows(AutomaticNetworkOperation.AccountRefresh) && services.Site.SignedIn)
                _ = window.Run(RefreshAccount, "账号、余额与密钥已自动同步。", true);
        };
    }
    public void UpdateAccountLabel()
    {
        if (services.Site.Account is { } current)
            account.Text = $"{current.Name} · 余额 ¥{current.Balance:0.####} · 已用 ¥{current.Used:0.####}";
        savedAccounts.ItemsSource = services.Site.SavedAccounts;
    }
    private void Fill(ConnectionProfile? profile)
    {
        populating = true;
        editing = profile;
        name.Text = profile?.Name ?? "MathGPT";
        baseUrl.Text = profile?.BaseUrl ?? "https://mathgpt.xyz/v1";
        key.Clear(); group.Text = profile?.Group ?? "default";
        model.ItemsSource = null; routes = [];
        protocol.ItemsSource = Enum.GetValues<WireProtocol>(); protocol.SelectedItem = profile?.Protocol ?? WireProtocol.Responses;
        inputPrice.Text = profile?.CustomInputPrice?.ToString() ?? "";
        outputPrice.Text = profile?.CustomOutputPrice?.ToString() ?? "";
        connectionHint.Text = profile is null ? "填写服务地址与 API Key 后读取实际模型。" :
            $"{profile.Model} · {profile.Group}\n{profile.LastSync}\n密钥已加密保存；更换模型前请先读取模型。";
        populating = false;
    }
    private ConnectionProfile ReadProfile()
    {
        var p = editing is null ? new ConnectionProfile() :
            JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(editing, JsonData.Options), JsonData.Options)!;
        p.Name = name.Text.Trim(); p.BaseUrl = UrlPolicy.Base(baseUrl.Text).AbsoluteUri.TrimEnd('/');
        if (p.BaseUrl != editing?.BaseUrl || group.Text != editing?.Group || key.Password.Length > 0)
        { p.GroupVerified = false; p.WebsiteKeyId = null; }
        p.Group = group.Text.Trim();
        if (string.IsNullOrEmpty(p.Group)) p.Group = "default";
        p.Protocol = protocol.SelectedItem is WireProtocol wire ? wire : WireProtocol.ChatCompletions;
        if (!p.IsMathGpt)
        {
            p.CustomInputPrice = ParsePrice(inputPrice.Text); p.CustomOutputPrice = ParsePrice(outputPrice.Text);
        }
        return p;
    }
    private static decimal? ParsePrice(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!decimal.TryParse(value, out var p) || p is < 0 or > 1_000_000) throw new UserException("请输入有效的非负人民币单价。");
        return p;
    }
    private Task SaveAndLoad() => window.Run(async ct =>
    {
        var p = ReadProfile();
        var secret = key.Password; key.Clear();
        services.Store.SaveProfile(p, secret.Length == 0 ? null : secret);
        editing = p;
        var loaded = await services.Catalog.Load(p, ct);
        populating = true;
        profiles.ItemsSource = services.Store.State.Profiles.ToList(); profiles.SelectedItem = p;
        populating = false;
        // Profile selection populates fields; restore fetched routes afterwards.
        SetLoadedModels(p, loaded);
    }, "", true);
    private void SetLoadedModels(ConnectionProfile p, List<ModelRoute> loaded)
    {
        routes = loaded;
        model.ItemsSource = routes.Where(r => r.Available).ToList();
        var firstConnection = p.Model.Length == 0;
        var selected = routes.FirstOrDefault(r => r.Id == p.Model && r.Available) ??
            (firstConnection ? routes.FirstOrDefault(r => r.Available) : null);
        model.SelectedItem = selected;
        populating = true; group.Text = p.Group; populating = false;
        if (firstConnection && selected is not null)
        {
            p.Model = selected.Id; p.Protocol = protocol.SelectedItem is WireProtocol wire ? wire : selected.Protocols[0];
            services.Store.SaveProfile(p, null);
        }
        window.Status(selected is null ? "原模型当前不可用，请选择其他模型并应用。" : "连接已就绪，可从客户端页打开应用；也可在这里更换模型。");
    }
    private void SaveSelection()
    {
        var p = ReadProfile();
        if (model.SelectedItem is not ModelRoute route || !route.Available) throw new UserException("请先读取目录并选择可用模型。");
        p.Model = route.Id; p.Protocol = protocol.SelectedItem is WireProtocol wire ? wire : route.Protocols[0];
        p.Reasoning = reasoning.SelectedItem as string is { } effort && effort != "服务默认" ? effort : "";
        var secret = key.Password; key.Clear();
        services.Store.SaveProfile(p, secret.Length == 0 ? null : secret);
        editing = p;
    }
    private void SetReasoning(string id)
    {
        var available = ReasoningPolicy.Allowed(id);
        var old = reasoning.SelectedItem as string;
        reasoning.ItemsSource = new[] { "服务默认" }.Concat(available).ToArray();
        reasoning.SelectedItem = available.Contains(editing?.Reasoning ?? old ?? "") ? editing?.Reasoning ?? old : "服务默认";
    }
    private Task Login() => window.Run(async ct =>
    {
        if (flowToken.Length > 0)
        {
            await services.Site.VerifyLogin(flowToken, otp.Text.Trim(), ct); flowToken = ""; otp.Clear();
        }
        else
        {
            var value = password.Password; password.Clear();
            var challenge = await services.Site.Login(username.Text.Trim(), value, ct);
            if (challenge is not null)
            {
                if (!challenge.Methods.Contains("2fa")) throw new UserException("此账号要求通行密钥验证。请在网站完成账号操作；桌面端可直接使用 API Key。");
                flowToken = challenge.FlowToken;
                account.Text = "请填写两步验证码，再点登录。"; return;
            }
        }
        await RefreshAccount(ct);
    }, "");
    private async Task RefreshAccount(CancellationToken ct)
    {
        await services.Site.Status(ct);
        var user = await services.Site.RefreshAccount(ct);
        account.Text = $"{user.Name} · 余额 ¥{user.Balance:0.####} · 已用 ¥{user.Used:0.####}";
        savedAccounts.ItemsSource = services.Site.SavedAccounts;
        keys.ItemsSource = await services.Site.Keys(ct);
        publicRoutes = CatalogService.ParsePricing(await services.Site.Pricing(ct));
        var groups = publicRoutes.Where(r => r.Available).Select(r => r.Group).Distinct().OrderBy(x => x).ToList();
        var current = group.Text;
        populating = true; group.ItemsSource = groups; group.Text = current; populating = false;
        SetKeyGroups(groups);
        FilterCatalog(); window.Status("账号、余额与密钥已同步。");
    }
    private Task ImportKey() => window.Run(async ct =>
    {
        var selected = keys.SelectedItem as WebsiteKey ?? throw new UserException("请先登录并选择本人密钥。");
        if (!selected.Usable) throw new UserException("所选密钥已停用、过期或额度耗尽。请更换或在网站调整。");
        var secret = await services.Site.KeySecret(selected.Id, ct);
        var p = services.Store.State.Profiles.FirstOrDefault(x => x.WebsiteKeyId == selected.Id) ??
            new ConnectionProfile { Name = selected.Name };
        p.Group = selected.Group; p.GroupVerified = true; p.WebsiteKeyId = selected.Id;
        services.Store.SaveProfile(p, secret);
        Fill(p);
        var loaded = await services.Catalog.Load(p, ct);
        profiles.ItemsSource = services.Store.State.Profiles.ToList(); profiles.SelectedItem = p;
        SetLoadedModels(p, loaded);
    }, "", true);
    private Task LoadPublicCatalog() => window.Run(async ct =>
    {
        await services.Site.Status(ct);
        publicRoutes = CatalogService.ParsePricing(await services.Site.Pricing(ct));
        var current = group.Text;
        populating = true; group.ItemsSource = publicRoutes.Where(r => r.Available).Select(r => r.Group).Distinct().OrderBy(x => x).ToList();
        group.Text = current; populating = false; FilterCatalog();
        SetKeyGroups(publicRoutes.Where(r => r.Available).Select(r => r.Group).Distinct().OrderBy(x => x));
    }, "目录与最终人民币报价已更新。", true);
    private void FilterCatalog() => catalogGrid.ItemsSource = publicRoutes.Where(r =>
        r.Id.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
        r.GroupName.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    private void SetKeyGroups(IEnumerable<string> groups)
    {
        keyGroup.ItemsSource = new[] { "universal" }.Concat(groups).Distinct().Select(g =>
            new GroupChoice(g, g == "universal" ? "万能密钥 · 所有可用模型" : publicRoutes.FirstOrDefault(r => r.Group == g)?.GroupName ?? g)).ToList();
        keyGroup.SelectedIndex = 0;
    }
    private void RemoveProfile()
    {
        if (editing is null) return;
        if (!Ui.Confirm(window, $"移除桌面连接“{editing.Name}”？网站密钥保持有效。", "移除连接")) return;
        _ = window.Run(ct =>
        {
            services.Store.RemoveProfile(editing);
            profiles.ItemsSource = services.Store.State.Profiles.ToList(); Fill(null);
            return Task.CompletedTask;
        }, "桌面连接已移除。");
    }
}
