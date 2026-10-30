# 实现依据与复用取舍

调查日期 2026-10-26。先核对本机 SDK、发布器、Inno Setup、既有 Harness 和 Codex，
再读取官方网站、仓库、分发清单和网站真实接口。采用原生 WPF，复用 Windows BITS/DPAPI/CNG
与 FengCodex 发布，不复制一套凭据库、下载服务或运行时管理器。

相似开源项目 [CC Switch](https://github.com/farion1231/cc-switch)（MIT）提供多客户端设置管理，
仍不能直接承担本站账号/最终人民币价、官方安装与有预算的共享产物任务。
它采用 Tauri/Rust；本机已有 .NET 8/WPF、成熟发布器和安装编译器，因此没有引入新基础框架或整体 fork。
没有复制该项目 UI、品牌或代码。

主要原始来源：

- [Codex 官方仓库与 Apache-2.0](https://github.com/openai/codex)
- [Codex 配置](https://developers.openai.com/codex/config-reference/)
- [Codex 官方发布清单](https://api.github.com/repos/openai/codex/releases/latest)
- [OpenAI GPT-6.1 Sol 推理能力](https://developers.openai.com/api/docs/models/gpt-6.1-sol)
- [OpenAI GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra)
- [GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra)
- [Claude Code 安装/系统要求](https://code.claude.com/docs/en/setup)
- [Claude 网关](https://code.claude.com/docs/en/llm-gateway)
- [Claude 环境设置](https://code.claude.com/docs/en/env-vars)
- [Claude 官方安装脚本](https://claude.ai/install.ps1)：只阅读，未管道执行下载脚本
- [Harness MIT 仓库](https://github.com/deepseek-ai/deepseek-harness)
- [Harness 桌面与安装](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/desktop/README.md)
- [Harness 发布源](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/desktop/publishing/README.md)
- [Harness 模型配置](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/user/guide/providers.md)
- [Harness 默认模型](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/core/agent-default-model/README.md)
- [Windows BITS](https://learn.microsoft.com/en-us/windows/win32/api/bits/nn-bits-ibackgroundcopyjob)
- [Inno Setup 许可](https://jrsoftware.org/files/is/license.txt)：保留声明；许可允许商业应用，网站另有商业支持请求

官方更新源观察：Codex 0.164.0、Claude stable 2.1.308、Harness Windows x64 nightly 0.2.1-rc.3。
清单会变化，软件运行时重新读取，不把本文件快照当作永久最新版本。
签名主体与官方哈希已核验，但不把签名、目录或返回型号标签当作真实模型身份鉴定。
网站公开价格快照为 mathgpt.9，49 个目录型号含暂停关系；实际可用路线在每次连接/调用前读取。

中文版 Inno 消息文件复用本机已有版本，保留 Zhenghan Yang 维护者及原仓库声明。
正式程序为自有 MathGPT 品牌，不复用其他中转站素材。
