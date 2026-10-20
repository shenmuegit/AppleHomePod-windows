<p align="center">
  <img src="desktop/AirFlash.App/Assets/app.svg" width="72" height="72" alt="AppleHomePod for Windows 图标">
</p>

<h1 align="center">AppleHomePod for Windows</h1>

<p align="center">让 Windows 的声音在 HomePod 上播放。</p>

<p align="center"><a href="README.md">English</a> · <strong>简体中文</strong></p>

<p align="center">
  <a href="https://github.com/shenmuegit/AppleHomePod-windows/releases/download/v0.2.0-preview.2/AppleHome-0.2.0-ec4a0e797fed.zip"><strong>下载 Windows 预览版</strong></a>
  &nbsp;·&nbsp; <a href="#开始使用">开始使用</a>
  &nbsp;·&nbsp; <a href="#兼容范围">兼容范围</a>
  &nbsp;·&nbsp; <a href="https://github.com/shenmuegit/AppleHomePod-windows/releases/tag/v0.2.0-preview.2">版本说明</a>
</p>

<p align="center">
  <a href="https://github.com/shenmuegit/AppleHomePod-windows/releases/tag/v0.2.0-preview.2"><img src="https://img.shields.io/badge/release-0.2.0_preview_2-2563eb?style=flat-square" alt="版本：0.2.0 Preview 2"></a>
  <a href="LICENSE-GPLv3"><img src="https://img.shields.io/badge/license-GPLv3%2B-555?style=flat-square" alt="许可：GPLv3 或更新版本"></a>
</p>

AppleHomePod 通过局域网，把电脑上的音乐、视频和系统声音发送到 HomePod。完成设置后，继续使用原来的播放器，通过 Windows 输出选择、音量和静音控件操作。

> **预览版：**已验证 Windows 10 Pro 22H2（19045，x64）与单只 HomePod mini 27.0。其他 Windows 版本、HomePod 型号和立体声对尚未实机验证。

<p align="center">
  <img src="docs/images/setup-confirmation.png" width="546" alt="AppleHome 中文安装向导：居中的 HomePod mini、再播放一次按钮，以及没听到和听到了两个确认按钮。">
</p>

<p align="center"><sub>中文声音确认界面。当前发布的 Preview 2 使用简体中文向导。</sub></p>

## 使用 Windows 原生控件

安装向导会自动发现 HomePod、配置输出设备并处理连接参数。连接后，用 Windows 调整音量和静音；需要使用耳机或电脑扬声器时，在同一个输出菜单中切换即可。

后台随用户登录启动，日常无需额外打开混音器、控制面板或托盘菜单。无需填写 IP、选择网卡或输入缓冲毫秒数。

## 开始使用

1. 先在 Apple「家庭」App 中设置 HomePod，并让它与电脑连接同一局域网。电脑可以使用 Wi-Fi 或有线网络。
2. [下载预览版 ZIP](https://github.com/shenmuegit/AppleHomePod-windows/releases/download/v0.2.0-preview.2/AppleHome-0.2.0-ec4a0e797fed.zip)，解压并打开 **`AppleHome.exe`**。应用为自包含版本，无需另装 .NET 或 Rust。
3. 按向导完成音频驱动安装。Windows 要求管理员许可时按提示操作；若需要重启，先保存工作，重启后重新打开应用。
4. 等待 HomePod 连接，然后确认是否听到声音。需要重听时，点击「再播放一次」。

只有一只合格 HomePod 时会自动连接；有多只时选择目标设备。Windows 输出名称、首次播放输出和连接参数由向导配置。听到声音后点击「听到了」完成设置；没有出声时点击「没听到」检查并重试。

本发布包的完整首次安装流程，包括全新自动驱动安装与必要重启，仍待实机验证。当前播放验收使用已有合格驱动和设备记录。

**更新已有预览版：**重新打开向导会复用已安装程序。请按[中文更新说明](docs/first-run.md#已有预览安装)替换后台，保留已有合格设备记录。目前没有自动更新功能。

## Preview 2 的改进

**开发版进展：**向导已可按 Windows 首选显示语言自动选择英文、简体中文或繁体中文。上方下载的 Preview 2 尚未包含此语言功能。

安装界面以 HomePod 为中心，精简了重播和声音确认按钮，并保留清晰的键盘焦点。最小窗口尺寸下，操作按钮仍保持可见。

启动修复避免设备发现过程提前填满采集队列，保留现有设备缓冲记录和队列上限。验证细节见[版本说明](docs/releases/v0.2.0-preview.2.md)（英文）。

## 兼容范围

| 环境 | 预览版状态 |
|---|---|
| Windows 10 Pro 22H2，19045，x64 + 单只 HomePod mini 27.0 | 已验证正常音乐、Windows 音量变化及至少 15 秒静音后的恢复 |
| Windows 10 其他构建；Windows 11 x64 | 尚未实机验证 |
| HomePod 第 1、2 代；mini 其他固件 | 尚未验证，当前向导未放行这些设备档案 |
| 立体声对、Apple TV 分组 | 尚未验证，当前向导未放行 |

Preview 2 的实际发布二进制在启动修复后完成了正常音乐、音量和静音恢复验收。Windows 端点读回及后台就绪状态已检查，声音连续性与恢复听感由旁听者确认。同一设备的早期构建曾完成 30 分钟音乐和约 5 分钟视频主观同步检查，这些长播与视频结果不作为本版重跑通过。

当前不承诺已测量的端到端延迟。断网重连、过夜播放、固定倍速的视频漂移，以及其他家庭访问或 PIN 策略仍待验证。EXE 尚未签名，未提供自动更新。逐版本证据见[详细兼容记录](docs/COMPATIBILITY.md)。

## 下载与完整性校验

| 文件 | 用途 |
|---|---|
| [Windows 预览版](https://github.com/shenmuegit/AppleHomePod-windows/releases/download/v0.2.0-preview.2/AppleHome-0.2.0-ec4a0e797fed.zip) | 应用、原始驱动包、许可和构建记录 |
| [对应源码包](https://github.com/shenmuegit/AppleHomePod-windows/releases/download/v0.2.0-preview.2/AppleHome-source-ec4a0e797fed.zip) | 与发布二进制对应的源码和依赖锁文件 |
| [中文安装指南](docs/first-run.md) | 安装与已有预览版的更新方式 |
| [SHA256SUMS.txt](https://github.com/shenmuegit/AppleHomePod-windows/releases/download/v0.2.0-preview.2/SHA256SUMS.txt) | 发布附件的校验值 |

在 PowerShell 中计算下载文件的 SHA256，与同一 Release 的 `SHA256SUMS.txt` 对照：

```powershell
Get-FileHash .\AppleHome-0.2.0-ec4a0e797fed.zip -Algorithm SHA256
```

发布标签 `v0.2.0-preview.2` 对应已验收源码提交 [`ec4a0e7`](https://github.com/shenmuegit/AppleHomePod-windows/commit/ec4a0e797fedcc7110f29902c1042aab98830feb)。已验收的 EXE 与源码归档保持原样，没有重新编译。包内构建记录保留原始内容；当前安装说明与打包后完成的听感验收见 Release 说明及仓库文档。

## 构建与参与

应用使用 WPF/.NET 集成 Windows，使用 WASAPI 采集系统声音，由 Rust 引擎负责音频传输。内部程序集仍使用 `AirFlash` 名称，分发程序为 `AppleHome.exe`。

<details>
<summary>从源码构建</summary>

需要 Windows x64、`desktop/global.json` 指定的 .NET SDK、Rust MSVC 工具链、Visual Studio C++ Build Tools、Python 3.12+、uv 和 PowerShell 7。

```powershell
git clone https://github.com/shenmuegit/AppleHomePod-windows.git
cd AppleHomePod-windows
uv sync --locked --group dev
pwsh -NoProfile -File scripts/build-native.ps1 -Check
pwsh -NoProfile -File scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh -NoProfile -File scripts/dotnet.ps1 test AirFlash.sln --no-restore
uv run pytest -q
uv run ruff check .
```

开发文档：[架构](docs/architecture.md) · [打包](docs/packaging.md) · [验证](docs/VALIDATION.md)。

</details>

反馈问题时，请提供 Windows 构建号、HomePod 型号与软件版本、单只或立体声对，以及复现步骤。分享日志前移除配对数据和设备标识。问题与建议请提交到 [GitHub Issues](https://github.com/shenmuegit/AppleHomePod-windows/issues)。

## 许可与致谢

基于 Ding-Kyoma 及其贡献者的 [AirFlash](https://github.com/Ding-Kyoma/AirFlash)。本项目改动采用 **GPLv3 或更新版本**，保留上游版权和第三方声明。详见[源码来源](UPSTREAM.md)、[GPL 全文](LICENSE-GPLv3)及[第三方声明](desktop/THIRD-PARTY-NOTICES.md)。上游商业许可不覆盖本项目改动。

随附的 **VB-CABLE** 是 [VB-Audio Software](https://vb-cable.com/) 的独立软件，遵循其 [Donationware 条款](https://vb-audio.com/Services/licensing.htm)。欢迎向作者捐助或支付许可。原厂 Pack45 和厂商说明保持未修改，应用「关于」页提供来源与条款入口。VB-CABLE 不属于本项目的 GPL 许可范围。
