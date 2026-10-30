# 安装包签名

截至 2026-10-30，公开发布版本为 **1.8.2**：Windows 未签名，Mac 为未签名、未公证预览版。ZIP 容器和更新清单的 Ed25519 签名都不能代替 Windows Authenticode。配置脚本和测试通过不等于获得签名，不等于杀毒软件或 SmartScreen 不会提示。

用户当前要求：不付费、不提供个人实名信息。不得因此借用证书、生成自签证书冒充受信发布者、关闭系统安全功能，或覆盖已发布文件。

## 免费 Windows 路径

SignPath Foundation 的官网明确提供免费的开源项目签名，且不要求个人身份证明。它以基金会的身份背书项目，需要验证公开源码、项目控制权、维护和发布记录、构建来源及项目声誉，并由它决定是否接受申请。普通 GitHub 账号和 MFA 不等于获批。

- 官方介绍：https://signpath.org/
- 资格与行为规则：https://signpath.org/terms.html
- 申请入口：https://signpath.org/apply.html
- GitHub 构建来源验证：https://docs.signpath.io/trusted-build-systems/github

MathGPT 主程序使用 MIT，独立适配器及所用 relaykit 保留 AGPL 声明和源码。安装包不包含原厂 harness，客户端下载和接入由用户发起。客户端支持用户配置 API 服务。开源客户端关联商业 API 服务是否被基金会接受，应由其审核，不能自行断言符合资格。

公开仓库为 `https://github.com/hzf11235/mathgpt`，以单独审核的源码快照建立，不包含本机原有私有 Git 历史。`v1.8.2-source` 对应已发布源码，main 包含未发布的1.8.3候选。公开1.8.2源码下载：`https://mathgpt.xyz/downloads/MathGPT-1.8.2-source.zip`。完整源码可独立使用vendored Go依赖编译，不需要服务器源码、账号或支付配置。申请必须说明relaykit修改和许可证、与收费API服务的关系；基金会对第三方fork的签名规则仍需其确认。

未发布的 1.8.3 候选已添加安装联网说明和独立自动更新/账号刷新开关，并使用同一策略约束 Windows 与 Mac。未选择时不执行自动联网，手动操作保留，升级保留偏好；候选的定向验证不等于正式发布或基金会接受。`docs/PRIVACY.md` 对应候选行为。公开 1.8.2 仍保留其原有行为。

申请前需要：

1. 用户同意公开**桌面端干净源码**和构建流程到本人控制的 GitHub 仓库；不公开原仓库 Git 历史、私有交接、账号数据、证书或密钥。
2. 维护账号启用 MFA，指定代码维护者、审查者和每次发布的批准者。
3. 提交已核查的项目、许可、功能、隐私、已有发布和源码链接，等待基金会审核。
4. 获批后填入其实际提供的项目标识和受限 API 凭据；凭据只放 GitHub Secrets，不放源码、安装包或日志。
5. 先用测试策略验证全部签名步骤，再批准正式签名；不把测试证书或 CI 结构检查当成正式签名。

`.github/workflows/windows-signpath.yml` 保持手动触发和只读 `contents` / `actions` 权限。版本从当前检出的 `Directory.Build.props` 读取，传给两个 artifact configuration 的 `version` 参数；安装器同时传入选定的 `runtime`。服务端仍固定产品名称、具体目标文件和元数据版本，不能把任意上传文件送去签名。缺少获批设置或在非公共仓库/非受信 runner 上运行，会在构建前失败。

在用户明确同意公开新 GitHub 仓库、向基金会提交申请之前，不执行这些外部操作。准备好的审核源码包和配置并不表示已经提交或获批。

## Windows 发布顺序

本地正式构建使用证书存储中的受信代码签名证书；没有合格证书时，正式模式应在编译和修改安装包前停止。显式 Preview 模式仅用于开发、检查和候选包，不得进入正式下载。

完整顺序为：生成主程序和含正确版本资源的适配器 → 分别签名、时间戳和校验 → 生成并签名卸载器 → 打包并签名安装器 → 核验全部自有程序的签名和发布者 → 生成最终哈希、清单 → 实际安装和卸载检查。不能只签外层 Setup.exe。

免费服务返回签名产物时，同样必须独立核验；持有本地私钥不是验证外部签名的前提。Inno Setup 支持外部两遍卸载器签名；基金会的项目、策略和实际构建尚未获批时，不能把这条 CI 路径称为实签成功。

## Mac

正式 Mac 构建只能在 macOS 上，使用已存在的 Developer ID Application 身份及 `notarytool` 钥匙串配置。先完成应用内容，逐层签名并核验，再提交公证；只接受 Accepted，装订 ticket 并再次核验后生成正式 ZIP。

目前没有 Apple 开发者账号、证书或真实 Mac 验收。SignPath 的 Windows 签名不能代替 Apple Developer ID。现有“零成本且不办开发者身份”的条件下，不能承诺获得 Mac 正式签名公证；预览包必须保留真实状态。

## 签名提示与实际报毒

“未知发布者/不常下载/无法验证开发者”与“检测到某个木马”是不同情况。签名证明发布者和完整性，不是无病毒保证。若出现明确的恶意软件名称，应核对具体文件、来源、SHA256 和检测报告；不能一概引导用户忽略。

即使有效签名，Windows SmartScreen 也可能需要积累信誉。不要宣传“保证任何电脑都零提示”。

- Microsoft：https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation
- Apple：https://developer.apple.com/documentation/security/customizing-the-notarization-workflow

旧版本文件保持不可变。签名、重新打包或修改资源后必须使用新版本、新文件名及新清单。
