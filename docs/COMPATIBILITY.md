# 兼容表与实际验证边界

| 项目 | 支持边界 | 本次证据 |
|---|---|---|
| MathGPT x64 | Windows 10 22H2 19045+、Windows 11 | 本机 Windows 11、标准用户安装/启动/重装/卸载通过 |
| MathGPT ARM64 | 同上，原生 ARM64 自包含包 | 跨架构构建；无 ARM64 实机 |
| 32 位 Windows | 不支持 | 安装器和客户端来源限制 |
| Codex 桌面版 | 官方 Windows MSIX、Responses | 本机 Store 包 26.1021.3705.0 检测，配置与凭据助手夹具通过；未运行付费 GUI 任务 |
| Claude 桌面版 | 官方 3P Gateway、Claude Messages，MSIX 服务安装需 UAC，Cowork 需虚拟化 | 普通官方包 3.6556.2 已下载/验签/安装，配置合并/恢复夹具通过；真实 GUI 任务与运行组件冷启动未验收 |
| 仅 API Key | /v1/models?mathgpt_catalog=1 自动识别分组、协议、报价 | 单次 HTTP 夹具通过，无账号、入口密码或收费推理 |
| 数据升级 | schema 1 读入 2、首次保存才落盘 | 旧 CLI 类型、DPAPI Key 与磁盘原数据保留检查通过 |
| Codex CLI | 官方 Windows x64/ARM64，Responses；Windows 沙箱另受官方限制 | x64 0.164.0 官方下载/签名/安装、原生程序夹具请求通过 |
| Claude Code | 官方 Windows 10 1809+、x64/ARM64；本产品要求 19045+；仅 Claude Messages | x64 2.1.308 官方原生文件托管、程序夹具请求通过 |
| DeepSeek Harness | 官方 Windows x64 开发者预览；尚无官方 ARM64 桌面包 | rc.3 安装包 SHA512/签名核对，接管已有 rc.2，内置运行时夹具通过 |
| 中文/空格路径 | 下载、程序、配置和工作目录分别可选 | 实际程序安装与接口夹具均经过中文空格路径 |
| 系统缩放/窄窗 | PerMonitorV2、自动限制到可用工作区、紧凑顶部导航、滚动与换行 | 800/1180 逻辑宽、96/144/192 DPI 渲染；非真实多显示器 DPI 切换 |
| 网站同系风格 | 轻紫、素白、纸感及深色，切换保留输入 | 实际 WPF 渲染与保存/切换夹具检查 |
| 已有安装 | 先检测 PATH、常见路径和 Harness 安装注册表，独立配置 | 实际 Harness 接管和原配置指纹检查通过 |
| 国内网络 | 仅官方 GitHub、DeepSeek、Anthropic 源，支持 BITS/离线包核验 | 当前本机网络成功；不等于大陆关闭代理直连 |
| API 服务 | HTTPS；仅 IP 回环地址可用 HTTP；不跟随凭据请求重定向 | 真实本机 HTTP 夹具、服务错误/失效检查通过 |
| 网站账号 | 现有 RSA/v2、TOTP、Bearer/refresh cookie | 夹具通过；本人通行密钥/人机验证需网站操作 |
| 人民币价格 | 服务端最终 CNY 值、Token 单位和积分独立显示；缺价显式提示 | 真实公开目录读取与夹具倍率/缺价断言通过；缺价不进入预算队列 |
| 协作预算 | Token 成本可估算路线；有费用预留、次数/并发上限 | 夹具通过；不是服务端硬预算或真实账单验收 |
| 升级/卸载 | schema 1/2；保留数据/凭据/工作产物 | 旧 schema Key/CLI 迁移检查、同版本重装及卸载通过；无其他真实电脑升级验收 |

官方参考：

- [Codex Windows](https://developers.openai.com/codex/windows)
- [Claude Code 系统与安装](https://code.claude.com/docs/en/setup)
- [Harness 桌面说明](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/desktop/README.md)

客户端实际请求由本机夹具返回静态回答，没有消耗生产模型额度。
没有用单元测试、截图、隔离夹具或接口可读冒充真实付费模型、实体其他电脑或网络实测。
