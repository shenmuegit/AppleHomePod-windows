# AppleHome

**让 HomePod 成为 Windows 的音频输出。**

AppleHome 将 Windows 的音乐、视频和系统声音发送到 HomePod。首次安装由向导完成设备发现、输出配置和连接确认；日常直接使用 Windows 的输出选择、音量和静音控件。无需填写 IP、选择网卡或设置缓冲毫秒数。

**当前为 0.2.0 Preview 1。已验证组合：Windows 10 Pro 22H2（19045，x64）与单只 HomePod mini 27.0。** Windows 11、其他型号、固件和立体声对尚未完成实机验证。

[下载预发布版本](https://github.com/shenmuegit/AppleHome/releases/tag/v0.2.0-preview.1) · [安装指南](docs/first-run.md) · [兼容范围](docs/COMPATIBILITY.md) · [问题反馈](https://github.com/shenmuegit/AppleHome/issues)

## 首次安装

1. 从 Release 下载 `AppleHome-0.2.0-567533684ccd.zip`，解压并打开 `AppleHome.exe`。无需另装 .NET 或 Rust。
2. 确保 HomePod 已在 Apple“家庭”App 中初始化，并与电脑连接同一局域网；电脑使用有线网络也可以。
3. 按向导安装所需驱动。Windows 要求管理员许可或重启时，按提示完成；重启后重新打开 AppleHome 继续。
4. 等待自动连接并确认是否听到声音。需要重听时点击“再播放一次”，确认后完成设置。

只有一只合格设备时自动连接，多只时选择目标。设备名称、Windows 播放输出与连接参数由程序配置。本包已在已有合格驱动和设备记录上验证播放；全新自动驱动安装、重启到确认的完整流程仍待独立实机验收。

## 日常使用

直接播放常用播放器或浏览器中的音乐、视频，使用 Windows 控件调节音量和静音。需要切回耳机或电脑扬声器时，在 Windows 输出列表切换即可。

后台随当前用户登录启动，日常无需独立控制面板或托盘操作。缓冲按设备记录、接收端边界与匹配的设备档案自动处理，不要求用户填写参数，也不将单台设备的记录作为统一默认值。

## 当前兼容范围

| 项目 | 0.2.0 Preview 1 |
|---|---|
| Windows 10 Pro 22H2 / 19045 / x64 + 单只 HomePod mini 27.0 | 已完成本包实机验证 |
| 正常音乐连续播放、Windows 音量调节 | 已确认正常 |
| 静音至少 15 秒后取消静音 | 已确认恢复正常 |
| Windows 11、其他 HomePod 型号或固件、立体声对 | 尚未验证；向导仅开放上述单只 mini 档案 |
| 断网恢复、过夜运行、精确音画延迟 | 尚未取得本包实机验证结果 |

同一设备的早期构建曾完成 30 分钟音乐和约 5 分钟视频的主观同步验证；本包未重跑相同长测。完整的版本归属和使用边界见[兼容矩阵](docs/COMPATIBILITY.md)。

EXE 当前未签名，没有自动更新渠道。重新打开向导会复用已安装程序，不会自动替换后台二进制；已有预览安装的维护方式见[安装指南](docs/first-run.md#已有预览安装)。

## 下载与校验

[Release 页面](https://github.com/shenmuegit/AppleHome/releases/tag/v0.2.0-preview.1) 提供完整安装包、对应源码包、独立安装说明、版本说明和 `SHA256SUMS.txt`。下载后可用 PowerShell 核对：

```powershell
Get-FileHash .\AppleHome-0.2.0-567533684ccd.zip -Algorithm SHA256
```

将结果与同一 Release 的校验文件比较。本包对应源码提交 [`5675336`](https://github.com/shenmuegit/AppleHome/commit/567533684ccd04a2cd53783debd83925fda3d2c6)；包内保留原始来源与封包记录，封包后的实机结果见[本版说明](docs/releases/v0.2.0-preview.1.md)。

## 开发与构建

应用由 WPF/.NET 后台、Rust 音频引擎和 WASAPI 系统音频采集组成。开发需要 Windows x64、.NET 10 SDK、Rust MSVC 工具链、Visual Studio C++ Build Tools、Python 3.12+、uv 与 PowerShell 7。SDK 版本由 `desktop/global.json` 指定。

```powershell
uv sync --locked --group dev
pwsh -NoProfile -File scripts/build-native.ps1 -Check
pwsh -NoProfile -File scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh -NoProfile -File scripts/dotnet.ps1 test AirFlash.sln --no-restore
uv run pytest -q
uv run ruff check .
```

| 文档 | 内容 |
|---|---|
| [架构说明](docs/architecture.md) | 模块职责、后台入口与数据归属 |
| [构建与打包](docs/packaging.md) | 发布输入、源码归档、校验与不可变交付 |
| [验证记录](docs/VALIDATION.md) | 当前版本结果及历史证据 |

内部程序集名保留 `AirFlash`，最终应用文件名为 `AppleHome.exe`。历史诊断面板通过 `--legacy-panel` 打开，不属于日常使用流程。

## 许可与致谢

AppleHome 基于 Ding-Kyoma 的 [AirFlash](https://github.com/Ding-Kyoma/AirFlash)，修改采用 **GPLv3 或更新版本**。上游版权、许可和第三方声明均予保留，详见 [UPSTREAM.md](UPSTREAM.md)、[GPLv3 全文](LICENSE-GPLv3) 和[第三方声明](desktop/THIRD-PARTY-NOTICES.md)。上游商业许可选项不代表 AppleHome 修改提供商业许可。

音频驱动 **VB-CABLE** 来自 [VB-Audio Software](https://vb-cable.com/)，采用 **Donationware** 模式，欢迎向原作者捐助或支付许可。包内保留未修改的原厂 Pack45 与原始说明，应用“关于”提供来源与许可入口；驱动遵循[厂商独立条款](https://vb-audio.com/Services/licensing.htm)，不属于 AppleHome 的 GPL 源码。
