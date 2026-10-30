# MathGPT 桌面端

## Code signing policy

1.8.2 采用 Windows 安装向导 ZIP 和 Mac 预览 ZIP 分发，便携 ZIP 保留用于更新。MathGPT 自有程序尚未取得受信代码签名或 Mac 公证；厂商客户端保留其原始签名。
签名状态与申请条件见 [签名说明](docs/SIGNING.md) 和 [Code signing policy](CODE_SIGNING_POLICY.md)。
尚未获得 SignPath Foundation 许可或签名，不代表已能消除系统风险提示。

安装后自动创建桌面快捷方式。下载的客户端使用 Codex、Claude、DeepSeek Harness 原名；已有个人快捷方式保留，CLI 不自动创建。已接入的入口使用本机加密连接，不包含明文密钥。

Windows 原生 API 工作台。安装包内自带运行时，安装后直接打开 MathGPT，无需终端或手建配置。

1.7.1 提供 macOS Apple Silicon / Intel 预览包：解压后打开 MathGPT.app，首次打开会安装到个人 Applications 并创建桌面入口；密钥保存在系统钥匙串。可下载、检测和配置官方 Codex、Claude 与 DeepSeek Harness。无需另装运行时或切换工具。Mac 包未签名、公证，尚未在真实 Mac 上验收；厂商应用各有系统版本要求。

模型采用官方 API ID，推理强度单独选择。Claude Opus 5.5 为 claude-opus-5-5，支持 low / medium / high / xhigh / max；Gemini 4 Argon 为 gemini-4-argon，支持 low / medium / high。旧 MathGPT 连接在刷新目录时迁移已知别名，保留原档位。第三方服务必须提供兼容的协议和对应模型；托管工具等专有功能不会因协议转换而自动获得支持。

界面沿用 mathgpt.xyz 的顶部导航、轻紫渐变、细边卡片与留白比例，提供轻紫、素白、纸感三种风格，
均可切换深色模式。右上角即可切换，也可在“设置”中选择；切换不会清空正在填写的连接。

## 开始使用

1. 在网站向导选择设备、应用和密钥，保存后下载 MathGPT。万能密钥可以不选初始模型。
2. 安装后打开 MathGPT，点击“继续网页配置”。浏览器中核对同一确认代码，再点“连接这台电脑”。应用、模型、分组和密钥会一起接上，不用重新填写。
3. 点击“安装并开始”，自动检测已有安装，通过 MathGPT 下载源取得完整原厂包，校验后配置并打开。系统要求安装确认时按提示处理。
4. 后续在首页或客户端页的“快速切换”选择已保存连接、模型与推理强度，再点“切换并打开”；复用现有安装。原客户端正在运行时请先正常保存并退出，不强制关闭。

网站授权计划24小时内有效，电脑确认代码10分钟有效且只能使用一次；真实密钥不在确认网址中。授权后密钥由 Windows DPAPI/macOS 钥匙串保存。手工粘贴和账号导入仍保留在连接页。

初始模型只是默认值，不限制此密钥的模型目录。单厂商密钥展示当前分组全部已授权模型；万能密钥展示三家可用模型。跨协议使用内置适配；不需要另外安装 CC Switch。第三方应用自己的专有功能与模型控件仍按其实际支持范围工作。

网站入口密码、账号密码、模型 API Key 用途不同。入口密码和账号密码不保存。
API Key 与网站会话经 Windows DPAPI 加密。不要把数据目录发送给他人。
网站账号启用 TOTP 时在桌面填写两步验证码；仅通行密钥或人机验证的账号请在网站完成账号操作，
桌面端仍可直接使用 API Key。桌面没有虚构浏览器登录回调或索取厂商账号。

## 客户端与模型

- DeepSeek Harness：官方 Windows x64 预览版，支持已开放的 Chat Completions / Responses / Messages。
- Codex 桌面版：官方 x64 / ARM64，使用 Responses，自动合并配置并保留原账号、历史、MCP 与插件，通过本机凭据助手读取 Key。
- Claude 桌面版：官方 3P Gateway 模式，在桌面中使用 Code。完整离线包已含运行组件；不需要登录原厂账号。安装服务需要 Windows UAC，Cowork 另需虚拟化。
- Codex CLI、Claude Code CLI：保留在高级选项，仍使用独立配置。

跨厂商接入由随程序分发的 MathGPT.Bridge 完成，不要求用户另装 CC Switch。
本机适配器仅监听回环地址，使用独立随机凭据与当前 Key 的模型清单；保留真实型号，
支持文字、普通函数工具和流式回复。Codex 的跨厂商目录采用本站保守的 32K 文本兼容配置，
不是厂商完整能力声明；不能转换的托管工具明确返回错误。

Harness/CLI 使用自己的 client-profiles。官方桌面版使用原厂配置路径，退出后合并，修改前加密备份，可在更多菜单恢复。
发现后续手工改动时停止覆盖。不修改全局 PATH、执行策略或系统代理。
Harness 是单实例窗口；切换连接前先正常退出旧窗口。它的 Electron 外壳偏好仍由厂商管理。

目录和最终人民币价来自 MathGPT 网站，按真实分组与协议过滤，零售倍率不会再乘一次。
MathGPT Key 可通过模型目录扩展自动读取实际分组，无需网站登录或手填；自动分组 Key 需先改为固定分组。
账号内导入的 Key 会从本人密钥列表取得真实分组。其他兼容服务需自行选择其协议，调用兼容性以实际结果为准。
网站目录若开放了模型但没有返回该分组的最终价，会显示“站内未提供最终价 · 以账单为准”；
只在已声明支持的客户端协议中开放。

## 安装、更新和数据

MathGPT 启动后自动检查并准备新版，正常关闭后完成替换，下次打开即用新版；不会自动重启或打断工作。设置页可手动检查。仅接受本站固定下载地址、Ed25519 签名清单及大小/SHA256 全部匹配的包；失败保留现有程序。账号、密钥和工作区与程序分开保存。

1.6.3 及更早版本没有更新器，需先手动升级一次至 1.7.1。FengCodex 管理的本机安装继续由发布器升级。便携目录中有个人文件时停止自动替换；Mac 预览需从已安装的 Applications/MathGPT.app 运行，旧应用包的回退副本保留。自动更新仅针对 MathGPT，原厂客户端由安装/修复流程管理。

MathGPT 支持 Windows 10 22H2（19045）与 Windows 11 的 x64 / ARM64，32 位 Windows 不支持。
x64 已在本机验证；ARM64 包需在对应设备复核。客户端自身另受各厂商最低版本和网络限制。
Codex 与 Claude 默认通过 mathgpt.xyz 下载官方完整包，核对大小、SHA256 和原厂签名。大包可暂停并从断点继续；校验失败时不会降级安装。Mac 还核对原厂 Team ID、应用身份与处理器架构。

普通安装及便携包的数据默认在当前 Windows 账号的 LocalAppData/MathGPT/Data，
工作产物默认在“文档/MathGPT”。FengCodex 正式本机入口使用 D:/Codex/Data/mathgpt-desktop。
升级、卸载、移除客户端接入记录均保留用户数据和独立客户端。
便携包指无需安装的程序，凭据仍绑定当前 Windows 账号，不是可跨账号携带的密钥包。

下载由 Windows BITS 管理，暂停后在客户端页再次“下载并安装”可续传。
哈希不一致时阻止执行，可在“设置与帮助”删除损坏缓存再重试。
官方 Harness 安装器已启动后将等待其安全结束，不强行中断。

本版本未购买代码签名证书。安装包与程序可能显示发布者未知；校验随包 SHA256。
不需要关闭安全软件、更改执行策略或以管理员启动。

客服微信：t222oo

邮箱：ndls11235@gmail.com
网站：https://mathgpt.xyz

## 源码与构建

唯一源码：D:/Codex/Projects/mathgpt-desktop。未修改网站工程、数据库或生产配置。
构建：PowerShell 7 执行 scripts/Build.ps1；需要 .NET SDK 8 和现有 Inno Setup。
开发与验收范围见 docs/DEVELOPMENT.md、docs/HANDOFF.md、docs/verification.json。
