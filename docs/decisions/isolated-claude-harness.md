# Claude / DeepSeek Harness 独立 API 配置

本模块仅创建 MathGPT 数据目录下的独立配置，不写用户原有 Claude、DeepSeek Harness 账号目录。官方程序、系统安装与更新二进制仍共用；账号、Cookie、会话、API 配置分别保存。

## 启动与接线

- `ClaudeHarnessIsolation.ClaudeProfile(store, profile)` 给出独立路径。Claude 的第三方配置只写 `Library`，模式文件写其父目录 `UserData/claude_desktop_config.json`。不要再写真实 LocalApplicationData/Claude 或 Claude-3p。
- Windows 签名版 Claude 会主动删除 `CLAUDE_USER_DATA_DIR`。不能用这个变量宣称隔离。使用原厂 EXE 直接启动，传入 `--user-data-dir`，同时仅在子进程环境设置独立的 `LOCALAPPDATA`、`APPDATA`、`CLAUDE_CONFIG_DIR`。前两者必须与 `ClaudeProfile` 中的 `LocalData` / `RoamingData` 一致。
- Claude 的第三方配置位于私有 `Local/Claude-3p/configLibrary`。原厂 3p loader 读取它后，将 Electron userData 切到同一私有 `Claude-3p`，再获取该目录的单实例锁。Mac 采用私有 `Claude-3p` 和 Electron 参数；仍需真实 Mac 验收。
- `ClaudeLaunch` 返回 Claude 隔离启动参数。DeepSeek 继续通过现有流程提供本次选择的 `MATHGPT_API_KEY`，再调用 `IsolateHarness`，同时隔离 `DSH_HOME` 与 Electron userData。
- `RequireRuntimeSupport` 在配置前检查实际安装的 ASAR 能力。`Launch` 也会重查，并记录 PID、启动时间、EXE 与配置目录绑定的 `mathgpt-launch-session.json`。`Running` 只检查这一连接，不因原订阅窗口或另一连接正在运行而阻止配置。并发启动时的独占启动锁也保护配置写入。
- 安装来源、原厂签名与包校验仍走已有安装流程。ASAR 标志只证明当前包存在所需配置入口，不证明包来自官方，也不代表完整 GUI 或大陆网络验收。

不要通过 explorer.exe 的 AppsFolder 激活已配置的客户端：这个路径不能保证把选择的子进程环境传给应用。未知参数、旧的全局配置位置或发生变化的 ASAR 入口均拒绝启动，保留原账号。

## 调查证据

2026-10-30 对本地原厂 Claude 4.29.0 离线 MSIX 的 `app/resources/app.asar` 作只读检查：

- `.vite/build/index.pre.js` 明确删除签名发行版的 `CLAUDE_USER_DATA_DIR`，`LOCALAPPDATA` 不在该删除表。
- 3p loader 包含 `process.env.LOCALAPPDATA`、`configLibrary`、`deploymentMode` 与 `setPath("userData", ...)`。单实例锁在选择 userData 后取得。
- 禁止的调试 / 网络覆盖参数名单不包含 `--user-data-dir`；没有使用调试接口、注入或修改厂商程序。

原始公开源码：

- [Electron user-data-dir 在 JS 启动前设置 DIR_USER_DATA](https://github.com/electron/electron/blob/main/shell/app/electron_main_delegate.cc)
- [Electron 单实例锁使用 user_dir](https://github.com/electron/electron/blob/main/shell/browser/api/electron_api_app.cc)
- [DeepSeek Harness Desktop paths：DSH_HOME 下 profiles/desktop](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/desktop/src/paths.ts)
- [DeepSeek Harness Desktop 单实例](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/desktop/src/single-instance.ts)
- [Claude 官方 Desktop 网关配置说明](https://code.claude.com/docs/en/llm-gateway-connect.md#desktop-app)

DeepSeek 官方代码为 MIT 许可；本次没有引入新依赖或复制第三方实现。仅复用它公开的环境变量与 Electron 参数。

## 安全提示与更新范围

MathGPT 的普通界面 IPC 已使用当前用户命名管道，内置协议适配器只监听 127.0.0.1。用户看见的防火墙提示必须结合具体程序名判断；本机能查到 Claude / Cowork、Codex 原厂应用规则，没有证据证明所有提示均由适配器触发。没有关闭防火墙、SmartScreen 或添加全网放行规则。

既有 Claude 配置把 `disableAutoUpdates` 写为 true，这是应用自己施加的更新阻止，需由接线处移除或改为 false。即使解除阻止，也不能保证官方更新源在所有网络可达，或直接 EXE 启动仍具有 MSIX 包身份。MathGPT 自身签名更新清单与官方应用更新是两套机制。

## 本次验证

独立夹具运行 36 项检查，覆盖目录/账号不串用、继承凭据清除、旧全局配置拒绝、PID 重用、独立连接、在运行与启动中的配置保护、实际 ASAR 有界读取、能力变化及畸形 ASAR 拒绝。实际读取本地 Claude 4.29.0 原始 ASAR 与已安装 DeepSeek Harness 的 ASAR，能力检查通过。

另已真实启动验签通过的 Claude 4.29.0 原厂 EXE，使用合成凭据、`127.0.0.1:9` 推理地址与厂商 `--startup` 参数，无可见窗口、未抢焦点。私有 Claude-3p 内生成 Cookie / Local Storage / 会话资料，日志记录 Code 界面与 2.1.309 预置引擎初始化；320 份原账号文件前后字节一致。没有发送模型消息，没有收费请求。隐藏实例不响应普通退出尝试，随后按明确授权、核对 PID / 开始时间 / EXE / 私有参数后，仅终止该合成验收进程树，已无残留；不能将这次清理称作正常退出验证通过。

证据：`D:/codex/Tasks/2026-10-30/mathgpt-desktop-isolation/isolation-tests.log`、`vendor-runtime-support.log`、`vendor-startup.jsonl`。公共模块需由主任务接入配置、启动及测试入口后，随其余修改一起验证发布。

真实启动与清理证据：`D:/codex/Tasks/2026-10-30/mathgpt-desktop-isolation/live-vendor-start-1/RESULT.json`、`result.json`、`cleanup.json`。上述启动证据仍不等于付费 API 首条消息、原订阅同时运行或真实 Mac 验收。
