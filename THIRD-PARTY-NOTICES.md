# 第三方与官方客户端

MathGPT 桌面程序使用 Microsoft .NET 8 / WPF（MIT 及相关第三方声明）、
Windows BITS、DPAPI、CNG 与 Windows Shell。安装包由本机已安装的 Inno Setup 构建。

macOS 预览版使用 Avalonia 12.1.4（MIT）、.NET 8、macOS Keychain 和系统应用安装组件。
Avalonia：https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md 。原厂应用仍遵循各自许可。
跨平台登录信封加密使用 Bouncy Castle Cryptography 2.7.0（MIT）：https://github.com/bcgit/bc-csharp/blob/master/LICENSE.md 。

- .NET：https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- WPF：https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- .NET 第三方声明：https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- Inno Setup：https://jrsoftware.org/files/is/license.txt

客户端下载器获取原厂发布内容，不将其源码并入 MathGPT，也不修改客户端品牌或许可证。

- Codex CLI：OpenAI，Apache-2.0，https://github.com/openai/codex
- DeepSeek Harness：DeepSeek，MIT，开发者预览，https://github.com/deepseek-ai/deepseek-harness
- Claude Code：Anthropic，遵循其官方客户端条款，https://code.claude.com/docs/en/legal-and-compliance

调研过 CC Switch（MIT）作为相似产品；未复制其源码或素材，也未将其整体打包。
MathGPT 的独立本机协议适配器 MathGPT.Bridge 使用 QuantumNous/new-api 的 relaykit，
遵循 AGPL-3.0-or-later。完整对应源码、依赖源码与构建说明随程序以
MathGPT.Bridge-source.zip 分发；它通过受保护的本机 HTTP 接口与桌面助手通信。
适配器不包含服务器账号、数据库、支付或用户凭据。桌面助手不复制网站前端。

TOML 配置合并使用 Tomlyn 0.19.0（BSD-2-Clause，Alexandre Mutel），保留语法树之外的注释和配置。
官方桌面应用的配置字段参照厂商文档；Claude 本地配置库结构同时参考 CC Switch，未复制其实现代码。

Tomlyn — Copyright (c) Alexandre Mutel. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
