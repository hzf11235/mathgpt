# Mac 签名与公证

默认 Build-Mac.ps1 -Mode Preview 保留 Windows 交叉编译，文件名固定含 -preview，
清单始终为 signed_notarized: false。它不会成为正式签名包。

正式路径只使用现有 macOS 主机、Apple Developer ID Application 身份及其私钥、
默认钥匙串中已经配置的 notarytool profile。脚本不会注册账号、申请证书、导入私钥、
创建远端仓库或启用收费 runner。没有这些条件时，正式构建在预检阶段停止。
Apple 的 Developer ID 正式分发条件无法通过匿名、自签名或预览 ZIP 替代。

## 已具备凭据的 Mac

安装已有项目所需的 .NET SDK、Go 1.27.3+、Python 3.11+、PowerShell 7 和 Xcode 命令行工具。
macOS 图标使用自带 sips / iconutil，正式流程只有 Python 标准库与 Apple 工具。

用钥匙串访问管理已有 Developer ID Application 证书和私钥。
可用 security find-identity -v -p codesigning 读取其公开 SHA1 指纹及 Team ID。
notarytool 凭据应由持有人按 Apple 文档在本机交互配置 store-credentials，
提供已有 Apple ID / Team ID 引用，在安全提示中输入密码；
不要把密码写入命令参数、脚本、JSON、环境变量或构建日志。
脚本仅引用这个 profile 名称，不读取密码。

将 MacSigning.example.json 复制到自己的本机配置目录并填写三个非秘密引用。
不得把账户密码或证书内容加入此文件；未列出的字段会被拒绝。
也可使用 MATHGPT_MAC_CERTIFICATE_SHA1、MATHGPT_MAC_TEAM_ID、
MATHGPT_MAC_NOTARY_PROFILE 环境变量覆盖对应引用。

在源码根运行：

    pwsh -File scripts/Build-Mac.ps1 -Mode Release -PreflightOnly -SigningConfig /local/config/MacSigning.json
    pwsh -File scripts/Build-Mac.ps1 -Mode Release -Runtime osx-arm64 -SigningConfig /local/config/MacSigning.json
    pwsh -File scripts/Build-Mac.ps1 -Mode Release -Runtime osx-x64 -SigningConfig /local/config/MacSigning.json

工具可通过 -DotNetPath、-GoPath、-PythonPath 指定。默认包在 build/packages，
证据在 build/verification/mac-signing/<runtime>。正式同名包已存在时拒绝覆盖，
可通过 -OutputDirectory 指定新的本机构建目录。

## 执行顺序与验收边界

1. 在构建前验证 macOS、原生工具、启用的 Gatekeeper、指定团队的有效 Developer ID 身份及
   notarytool profile；不接受 ad-hoc、自签名或 Development 身份。
2. 发布 .NET 与 Go，完成图标、Info.plist、许可证、对应源码及使用说明。
   bundle ID 固定为 xyz.mathgpt.desktop；更新应保留原团队，以维持钥匙串身份。
3. 对每个实际 Mach-O（含 .NET runtime、dylib、createdump、内置适配器）逐个从内到外签名，
   再签外层 app，均要求安全时间戳与 hardened runtime。只有 .NET 主 apphost 使用 allow-jit；
   不加入调试、获取任务、禁用库校验或关闭内存保护的 entitlement。
4. 严格递归验证、逐个检查团队/Developer ID/时间戳/runtime/entitlements，并运行 Gatekeeper。
   公证前只允许明确的 Unnotarized Developer ID 作为预期拒绝。
5. 用 ditto 创建临时 ZIP，notarytool submit --keychain-profile ... --wait，
   要求 Accepted，并核对 Apple log 的 job UUID；日志若提供团队或 SHA256 也必须匹配，
   签名团队始终通过 codesign 要求验证。Apple 文档示例不保证这些可选日志字段存在。
6. stapler staple / validate 后再次验签、要求 Gatekeeper 接受 Notarized Developer ID；
   最后用 ditto 重新打包并展开，核对完整文件哈希、权限、签名、公证票据及 Gatekeeper。
   全部成功后才写不含 -preview 的 ZIP 与 signed_notarized: true 清单。

任何签名、公证、staple 或验证失败都会停止，保留本机失败证据，不产生正式清单。
不关闭 Gatekeeper/SIP，不移除 quarantine，不改个人应用或钥匙串内容。
当前 layout 没有嵌套 framework/app 或符号链接；将来引入这类原生依赖时会拒绝并要求明确扩展签名布局。
最终正式 ZIP 只含已公证 app；使用说明封装于签名前的 Resources，诊断从应用内“运行本机自检”进入。

本机 Windows 的单元测试、负面预检和预览打包只能验证流程合同。
real_mac_tested 与 runtime_functional_tested 在签名清单中仍为 false；
真实 Mac 启动、钥匙串、网络及用户配置流程需要另做功能验收。
本轮无 Apple Developer 账户、真实 Mac 或费用授权，未执行真实签名、公证或发布。

来源：[Apple 手动签名与嵌套代码](https://developer.apple.com/library/archive/technotes/tn2206/_index.html)、
[Apple 自定义公证流程](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow)、
[.NET macOS 发布与 JIT entitlement](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos)。
已比较 Avalonia / Velopack 的公开签名方案；沿用本项目 ZIP 分发和 Apple 原生工具，
无需引入新的安装器、自动更新器或第三方签名依赖。
