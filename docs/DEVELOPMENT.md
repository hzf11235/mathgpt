# MathGPT 桌面端开发手册

## 1.8.3 候选：自动联网选择

`StateStore.Network` 统一 Windows/Mac 自动更新及账号刷新策略。未确认、未知格式或损坏的 `network-preferences.ini` 均禁止自动请求；手动操作独立保留。该文件只含两个布尔值和格式版本，存放在 StateStore 数据目录，不与账号/密钥的 settings.json 混写。帮助页显式保存；Inno 在同一安装向导内显示用途和两个选项，普通安装确认后通过 `--set-network-options 0|1 0|1` 本机入口保存，静默安装不写默认同意。更新不覆盖数据目录，因此选择保留。

定向测试：`dotnet run --project tests/MathGPT.Tests -c Release -- <独立临时目录> --network-preferences`。测试使用拦截 HTTP 处理器，不访问外网或真实账号。Inno 可用 `/O-` 仅做语法校验、不生成安装包。Mac 需使用共享 DotNet9 编译现有 Avalonia 源码，再以 .NET8 执行需要的无头入口；这不等于 Mac 实机验收。

## Windows 安装检查

WindowsAuthenticode.TrustedSigner 在只读文件锁内通过 WinVerifyTrust 验证签名，并从同一信任状态取出签名者，交给现有的厂商精确匹配逻辑。只接受返回值零，所有失败仍阻止安装；finally 关闭 WinTrust 状态并释放本机内存。它不启动 PowerShell。默认信任与撤销策略和旧 PowerShell WinVerifyTrust 路径保持一致，另设 WTD_DISABLE_MD2_MD4；不使用跳过策略、撤销检查或仅缓存网络的标志，不修改证书库、执行策略和安全软件设置。

查询已注册 MSIX 与执行官方包安装仍由 Windows PowerShell 命令完成；-Command 使用固定命令，路径通过环境变量或 PowerShell 单引号转义传入，不再使用 -EncodedCommand。Claude 包自带服务所需的 Windows UAC 不绕过。普通同协议、无需转换的连接不启动桥；需要转换时桥仍仅监听 127.0.0.1，使用随机令牌并拒绝浏览器 Origin。

原生实现依据 [WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust)、[WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data) 与 [PowerShell 原实现](https://github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/security/Authenticode.cs)。曾核对 MIT 许可 Vanara 项目，当前实现未添加依赖或复制其源码。回归入口：dotnet run --project tests/MathGPT.Tests -c Release -- <证据目录> --authenticode <原厂Codex.msix> <原厂Claude.msix>。不要以验签通过推断 SmartScreen 信誉或杀软结果；二者需要分别验证。

## 产品、归属与入口

独立桌面软件，Windows 正式版与 macOS 预览版共用 Core，唯一源码 D:/Codex/Projects/mathgpt-desktop。
正式程序 D:/Codex/Apps/mathgpt-desktop/current/MathGPT.exe，经 FengCodex 发布器部署。
本机数据 D:/Codex/Data/mathgpt-desktop，工作产物 D:/Codex/Documents/MathGPT/Workspaces。
其他用户安装或解压后默认使用自己的 LocalAppData/MathGPT/Data 与文档/MathGPT。
没有自动启动服务或后台计划任务。

网站 D:/Codex/Projects/feng-relay 是独立发布单元，默认保持只读；本轮用户明确授权同时维护，两者分别验证发布；
桌面端只调用其 HTTP 契约，不复制其业务实现或私有运维凭据。独立适配器按 AGPL 复用通用 relaykit，
来源提交、许可证和完整 vendored 对应源码见 bridge/README.md 与 THIRD-PARTY-NOTICES.md。
接口现状及可选扩展见 INTERFACE-CONTRACT.md；协作边界不得因后续维护而扩大。

## 模块地图

- MathGPT.Core/Models.cs：非秘密设置、客户端安装记录、模型路线与价格；JsonData 为只读 DTO 解析工具。
- Security.cs：HTTPS/同源限制、路径边界、重解析点拒绝、错误脱敏、当前用户 DPAPI 与凭据目录 ACL。
- StateStore.cs：兼容 schema 1、保存 schema 2，原子写入与单份 previous；凭据使用独立随机引用。
- PasswordCipher.cs：RSA.Create 与 BouncyCastle 的带标签 OAEP/SHA256、AES-256-GCM，跨平台匹配网站 v1/v2 登录信封。
- MathGPT.Mac：Avalonia 预览界面与 MacClients 原厂应用安装/配置；MacKeychain 使用 Security.framework，不在命令参数写入密钥。Build-Mac.ps1 + pack-mac.py 生成双架构自包含 app ZIP，检查 Mach-O / Unix 权限 / ICNS 透明度。真实钥匙串、Gatekeeper、安装与应用启动需在 Mac 另验收，当前不作已通过声明。
- MacPackageSafety 在解压前验证 ZIP 路径和相对链接图；MacSelfCheck 以临时钥匙串项目和本机适配器进行无付费诊断，不读取用户已有凭据。客户端页面一键安装接入；随包「运行本机检查.command」调用 MathGPT --self-check，失败退出2并保留不含Key的 Diagnostics 报告。Mac版本从Mac csproj读取，可独立于Windows发预览版。
- ModelIdentity / ReasoningPolicy：只合并明确等价的连接别名，不任意剥离型号后缀；按官方模型分别列出档位。协议与分组可用性始终以目录收窄。
- WebsiteClient.cs：网站入口 cookie、账号密码/TOTP、Bearer 会话、自动刷新、退出、本人余额与 Key 管理。
- CatalogService.cs：API /models 与站内最终 CNY 目录求交，按分组协议和暂停状态收窄，不扩大总目录声明。
- BitsDownload.cs：Windows BITS 持久下载、暂停/恢复、任务归属核对、SHA256/SHA512 验证及缓存移除。
- DesktopClientDownloads.cs：固定本站清单、完整原包元数据、无凭据 HTTPS 下载、SHA256 绑定的断点、同一版本官方回退；网络错误与完整性拒绝严格区分。Windows 额外验 Authenticode 与 Appx 身份，Mac 额外验 Apple Developer ID、Team/Bundle 与架构。清单最多 64KB、包最多 8GB，禁止任意 URL、镜像重定向和普通 Claude 壳包。
- PackageService.cs：固定官方清单解析、原生包托管、官方 NSIS 安装、ZIP 安全解包、版本检测、注册表接管。
- ClientService.cs：独立 CODEX_HOME、CLAUDE_CONFIG_DIR、DSH_HOME；不在配置和参数中写密钥。
- DesktopPackages.cs：官方 MSIX、身份/签名、已有应用检测与 Windows 安装/启动；Claude 服务安装通过 UAC。
- DesktopConfiguration.cs：Tomlyn 语法树合并 Codex，Claude 3P 配置库；退出后生效、DPAPI 备份与外部改动保护。
- BuiltinGateway.cs / CompatibilityCatalog.cs：当前用户本地适配器生命周期、独立随机凭据和保守的真实型号目录。
- bridge/：独立 Go 进程；Responses、Messages、Chat 文本及普通函数工具互转，未支持的原生托管工具明确拒绝。
- InferenceService.cs / TaskModels.cs / TaskQueue.cs：三种文本协议、指定型号、持久队列、共享产物、保守预算预留与手动恢复。
- ReasoningPolicy.cs：明确核对过的 GPT 推理枚举；未知型号仅使用服务默认。
- MathGPT.App/Pages：连接、客户端、协作与帮助界面；Ui.cs 为轻量控件样式，不承担业务逻辑。
- MainWindow / ThemeManager / AppInstance：顶部导航、动态主题、状态和进度、同一数据根的单实例及当前用户命名管道。
- UiSmoke / UiFlowAudit：显式诊断入口；在独立测试数据中检查页面和真实 HTTP 夹具下的连接操作，不是模型验收。
- tests/MathGPT.Tests：零额外 NuGet 的断言程序、HTTP/账号/协议/任务夹具、真实客户端及安装包验收。

依赖方向：App → Core；Core 不引用 UI。网站是远程 HTTP 服务，官方客户端是独立进程。
无新数据库、WebView、Node 运行时或第三方 GUI 框架。WPF/.NET 8 自包含打包，复用本机 SDK 与 Inno Setup；
构建时使用共享 Go 工具链，随包提供适配器可执行文件及 MathGPT.Bridge-source.zip。

视觉参照 mathgpt.xyz 当前 workspace.css 的实际色板与浏览器渲染，独立实现 WPF 控件模板，
没有修改或复制网站前端源码。轻紫 aurora、素白 orbit、纸感 paper 配合独立 DarkMode，
保存于原 schema 1 的可选字段；旧设置默认轻紫浅色。主题更新原有 Brush，保留表单未保存输入。
字体、输入框、下拉框、菜单、按钮与滚动条使用统一模板；800 逻辑宽时顶部导航缩成带提示的图标。

## 安全和持久化约定

API Key、Bearer 会话、refresh/入口 cookie 只存 DPAPI 文件，凭据目录限制当前用户与 SYSTEM。
密码、验证码、入口密码不保存。普通设置、产物和命令行不含可用凭据。
凭据更新先写新随机对象，再提交设置；失败保存不能将新 Key 配给旧服务。
每连接保留一份旧加密引用，配合 settings.previous 恢复，不跨 Windows 账号导出明文。

Harness/CLI 写自己的 client-profiles，桌面版合并原厂全局配置，退出后生效，DPAPI 备份可恢复，原账号与历史不删除。
MathGPT.exe --credential <profile-id> --data-dir <root> 只向父进程 stdout 输出 Key，不显示窗口、不经过单实例 IPC。
凭据助手只使用当前 Windows 用户 DPAPI，不提供同一用户下恶意程序之间的保密保证。
Claude 的主型号、别名、辅助模型保持同一明确型号。Codex 和 Harness 的重试设置为零。
内置适配器只允许已配置 HTTPS 目标和 Key 目录中的真实型号，不把本地 token 传给远端。
本地接口拒绝浏览器 Origin、跨站请求、异常 Host 和未授权型号；请求取消传递、无自动重试。
不保留服务端会话，previous_response_id 明确拒绝，客户端须提交完整历史。流中断不得伪报 completed。
兼容目录为 MathGPT 32K 文本/函数工具配置；图片、原厂托管工具和厂商私有推理功能不作通用承诺。
Windows 凭据助手 --credential <id> --builtin 启动适配器后仅向父进程 stdout 返回独立本地凭据。
Harness 的自定义 GPT 需要 reasoningEfforts 声明，已通过实际运行时发现并修复；
GPT-6.1 Sol/Astra 在 Harness 的工具调用仅允许 Responses。

协作默认只产出任务目录内的 Markdown，不授予 shell 或既有项目文件修改权限。
调用前核对服务地址、分组、模型、协议与报价；变更不自动迁移在途任务。
并发只在同任务的两个候选之间发生，跨任务串行。默认预算 ¥1、并发 2、调用上限 6、输出 2048。
估算预留不等于服务端硬预算，积分模型缺少上界时不进入自动队列。
失败和取消不自动重试，不释放未知费用；已发送请求可能继续由服务端结算。

任务索引是 tasks.json，单任务同时保存记录.json、任务.md、各角色产物。
重启把运行/排队任务标为中断，须人工核对账单后恢复，已完成步骤复用。
实际结算以网站账单为准，不从返回型号标签推断上游真实身份。

## 构建与验证

PowerShell 7，在项目根：

    dotnet build src/MathGPT.App/MathGPT.App.csproj
    dotnet run --project tests/MathGPT.Tests/MathGPT.Tests.csproj -- work/tests/core-final
    dotnet run --project tests/MathGPT.Tests/MathGPT.Tests.csproj -- work/tests/package-audit --packages
    dotnet run --project tests/MathGPT.Tests/MathGPT.Tests.csproj -- work/tests/native-clients --native-clients
    pwsh -File scripts/Build.ps1 -Runtime win-x64 -Installer
    pwsh -File scripts/Build.ps1 -Runtime win-arm64 -Installer
    pwsh -File scripts/Test-Installer.ps1
    go test -mod=vendor ./...  # 在 bridge 目录

Mac预览构建：`pwsh -File scripts/Build-Mac.ps1 -Runtime osx-arm64` 与 `osx-x64`。Windows上的Mac界面/包合同检查先用共享SDK构建 tests/MathGPT.MacPreview，再用本机 .NET8 执行其dll（共享DotNet9仅有9.x运行时，不能直接dotnet run启动8.x）。这不是Mac真机验收。正常Mac分发必须使用Apple Developer ID及notarytool，当前没有这些凭据；预览使用Apple官方首次打开提示，不移除隔离属性或关闭系统检查。来源 https://support.apple.com/zh-cn/102445 及 https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution 。

packages 会下载官方发布物并安装到明确的本机客户端目录。已存在的 Harness 只接管，不替换注册。
native-clients 只访问本机 HttpListener 夹具：实际 Codex、Claude 与 Harness 内置 headless 运行时；
Harness 使用与桌面相同的 provider patch，不把 headless 结果说成桌面所有工具和 UI 已测。
该命令不调用生产模型。新真实模型测试必须另取独立账号/Key 与明确预算，不使用网站窗口旧授权。

程序支持 --data-dir PATH 隔离数据、--ui-smoke OUTPUT 诊断与 --launch Kind --profile ID 快捷方式。
UI 诊断输出四页、两种逻辑宽度及 96/144/192 DPI 的 24 项像素渲染，再输出四组主题预览；
窗口离屏运行以免干扰用户。该检查不等同于更改真实显示器缩放。
发布用自包含 single-file，固定已装运行时 8.0.30；升级 .NET 时重跑兼容和安装检查。

测试后填写 verification.json，通过 Project-Lifecycle Record，再调用 Publish-FengCodexApp。
发布器自带 Check；不单独重复 Check。源码留本地 Git main，不推送公开仓库。

1.3.0 将「安装并接入」作为客户端主路径；先保存 Key 并读取模型，之后自动安装、配置和打开。
旧 CC Switch 记录仍保留，新增连接优先内置适配。完整原生桌面付费工具任务、大陆冷启动、
ARM64 实机和 Mac 尚未验证。实际 Codex CLI 本地夹具测试只证明指定客户端版本能读取兼容目录并接收流式结果。

## 排障与扩展

1.3.2 移除协作页面入口，旧队列数据和文件保持。快捷方式使用原厂产品名，自动创建只针对三种桌面客户端；
已有个人入口按原字节保留，只有软件自身标记的入口可自动更新。用户明确要求保留真实桌面后，
安装验收必须传 -NoShortcutChanges，通过隔离目录 COM 测试验证名称与凭据引用。
Claude 使用 inferenceCredentialKind=helper-script；免账号连接的完整桌面付费任务和大陆无代理冷启动仍未验收。

1.3.1：MathGPT 安装器默认创建桌面快捷方式。ClientService 统一写入应用与客户端快捷方式，重复使用稳定文件名，
已配置的客户端带连接标识与数据路径，经 MathGPT 加载当前用户 DPAPI；未配置的 MSIX 使用 AppsFolder 原生启动。
正常启动补齐已有客户端入口，--create-shortcuts 可无窗口补齐；--ui-smoke 与凭据助手不会操作真实桌面。
安装测试临时核对真实桌面和开始菜单入口，结束时恢复原快捷方式，不覆盖测试期间发生的外部改动。

用户界面底部给可执行错误，帮助页记录脱敏异常类型，不输出上游响应正文或 HTTP 凭据。
BITS 故障保留 jobId，恢复复用同一任务；不修改系统策略或代理。
坏包禁止执行，用户在下载管理删除明确缓存后重试。安装器启动后等待原子结束。
新增客户端只扩展 PackageService、ClientService 和客户端卡片，不能让模型指定执行文件或脚本。
新增价格合同先定义币种、单位、最终倍率、可用组协议及上界；不能把积分当作 Token。
安装、同版本重装、卸载保留数据已有本机证据；未来 schema 迁移需增加真实旧格式恢复测试。

1.2.0：SwitchIntegration 使用 CC Switch 官方协议（https://github.com/farion1231/cc-switch ，MIT）构造用户确认的导入链接；不捆绑其程序。RoutingGuide 的 UI 预览 Key 仅存内存，不写入输入数据。安装验收按版本隔离目录。


## 1.6.0 网站连续接入与快捷切换

DesktopSetupClient 只调用固定官网的 device/start 与 device/poll。256bit device_code 只存内存、以 HTTPS POST 发送；核对网址只含公开 user_code。固定校验 HTTPS 官网、路径、短码、期限和交付字段，禁止任意重定向和命令。用户在网页登录后明确批准当前 plan_id，桌面一次性领取本人密钥；取消、过期和计划变更均需重新发起，不自动绕过授权。参考 RFC8628 与 OWASP Authentication/Session Management，未宣称实现通用 OAuth 服务。

DesktopSetupImporter 复用同一 WebsiteKeyId 的连接，沿用 StateStore/Vault 的原子写和凭据轮换。计划 ID 为非秘密恢复信息，AppState 新增 PreferredClient；schema2 兼容旧配置，原用户目录、工作区和已安装客户端保留。初始模型空时自动选择一个可用默认值，但不限制目录；手选型号失效不静默替换。完整范围由服务器密钥目录和 ModelCatalogScope 决定。

Windows DesktopSetupPage 负责配对引导，QuickConnectionPanel 提供连接/型号/强度/应用快捷操作，复用 ClientsPage.StartClient 的检测、安装、配置和启动。没有重复实现安装器，不因换连接重装。MacSetupPanel 和 Mac MainWindow 使用同一 Core 合同。运行中配置保留 pending/CAS/备份保护，不强关用户应用。

Core 的 CatalogSwitchAudit 与 DesktopSetupAudit、Windows 屏外 UiSmoke/QuickSwitchUiAudit、MacPreview 的 MacContinuityAudit 是分层回归；本机夹具或 Windows headless 不能代替真实 Mac、所有宿主版本或真实收费模型请求。网站后端持久计划24h，设备会话10min单次；网关重启设备会话会失效，计划保留。字段与源代码分别维护于网站仓库 feng-relay，独立部署验证。


## 1.6.1 签名候选构建

Windows正式构建改为scripts/Build.ps1默认Production，CertStore/HSM签名预检在编译前，Preview必须显式指定。WindowsSigning/WindowsSignFile/WindowsVerify分开证书策略、Inno回调与独立验包；Bridge版本资源经SDK rc/cvtres生成，临时小源码目录复用vendor。免费服务流程见.github/workflows/windows-signpath.yml和SignPathPackage/SignPathCommon及两个受版本限制的XML；仅manual dispatch、public GitHub-hosted构建，签三项内部PE后再签安装器，不直接自动公开Release。Mac由Build-Mac.ps1的Mode Release调用MacRelease.py，Preview保留Windows交叉编译。

完整操作及未满足条件见docs/SIGNING.md。用户要求零成本、不提供个人实名资料，因此仅准备免费Foundation路线，不注册、购买、上传签名、公开新仓库或更换生产包。当前Windows无代码签名证书；Mac无Developer ID/真实主机，Foundation无已批准项目/凭据。任何本地测试和未签预览都不得描述为实签或公证成功。

ClaudeDesktopReadiness 负责 Windows Claude Code 运行组件检查与离线升级策略，必须在自动打开之前完成；主程序存在不等于宿主引擎可用。同版 MSIX 不以卸载或 ForceUpdate 强行替换。Code-only 使用官方 desktopHome=off，保持 isClaudeCodeForDesktopEnabled=true，不修改 requireCoworkFullVmSandbox。

## 1.7.1 MathGPT 自更新

1.8.0的公开发行同时包含两份Setup EXE与四份更新ZIP，八文件（含源码、manifest）完整HTTPS回读后才签名feed。安装器应与Verified stage同批构建，并用Test-Installer.ps1 -ExpectedStage逐文件核对；不要混用同版本不同候选。正式本机安装遵从用户从头体验的要求保持未安装。

SelfUpdateService 只读取固定 https://mathgpt.xyz/downloads/mathgpt-updates.v1.json，内置 Ed25519 公钥验证原始 payload 字节。发布签名不同于 Windows Authenticode / Apple 公证。清单最多90天，发布每版必须更新，不能仅上传 ZIP。文件名由平台/架构/版本导出，不接受远端命令或任意URL。复用已有 BouncyCastle、HttpSafety、SafeFiles，不新增运行时下载框架。

SelfUpdateFiles 限制本机普通路径、ZIP成员、文件数和展开大小。程序与数据/工作区不能重叠，FengCodex current 跳过。SelfUpdateApply 独立复制自身作为退出工作程序，校验原进程PID、开始时间、可执行文件、任务摘要和正常退出标记；异常退出不应用。缓存位于 DataRoot/self-update，暂存和回退与目标同卷。只有验证过的任务才可写 receipt；未知或改动过的回退文件不删除，Mac 回退保留。总下载期限15分钟。

发布顺序：四平台构建与检查→完整 ZIP / 公开源码凭据扫描→发布不可变版本文件→六文件全量 HTTPS SHA256 回读→scripts/New-SelfUpdateFeed.ps1 -ReleaseManifest <manifest> -PublicReadbackReceipt <receipt> -OutputPath <fresh-feed>→scripts/Publish-SelfUpdateFeed.ps1 -FeedPath <fresh-feed> -ReceiptPath <fresh-receipt>→官网更新下载入口。私钥仅在 D:/Codex/Data/mathgpt-ops/mathgpt-update.key.dpapi，用户DPAPI保护，通过stdin解密给签名器；禁止源码、argv、环境或公开日志中携带。公钥元数据 mathgpt-update-public.json 不含私钥。

验收：dotnet run --project tests/MathGPT.Tests/MathGPT.Tests.csproj -c Release -- <fresh-output> --self-update。--check-update-and-exit 是实际便携入口的无GUI诊断，只允许自身安装目录和固定更新源，不接受任意包地址。Windows用私有1.7.0引导包从公开1.7.1真实升级并核对文件/数据；Mac夹具与交叉编译不替代实机。
