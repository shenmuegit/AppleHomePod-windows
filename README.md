# AppleHome

面向 Windows 10/11 x64 的原生 HomePod 音频发送项目，采用 WPF 界面、Rust 音频引擎和 GPLv3+ 开源许可，基于 Ding-Kyoma 的 [AirFlash](https://github.com/Ding-Kyoma/AirFlash)。

**当前为可用测试版：Windows 10 Pro 22H2（19045）到单只 HomePod mini 27.0 的系统音乐播放、手动停止和本机声音恢复已由用户实机确认。** 当前使用 2 秒目标缓冲；声学延迟与视频同步未测量。Windows 11、其他型号、其他固件和立体声对均待实机验证。

详细结果见 [兼容矩阵](docs/COMPATIBILITY.md)、[验证记录](docs/VALIDATION.md)。调研、已审阅规格和实施计划保存在 `docs/research/` 与 `docs/superpowers/`。

用户已选择下一版的 Windows 原生输出设备方向：首次独立安装虚拟音频驱动并在 Windows 设备属性中命名，日常通过 Windows 选择 HomePod、调节音量与静音，后台自动发送。[设计规格](docs/superpowers/specs/2026-10-14-windows-native-homepod-output-design.md)及经共同工程审阅的[实施计划](docs/superpowers/plans/2026-10-14-windows-native-homepod-output-implementation.md)已进入执行；驱动已独立安装且身份核验通过，系统命名、重启、实机校准及无面板后台仍待完成，见[新路径验收记录](docs/validation/windows-native-homepod-output.md)。下面的便携包使用方式仍对应已交付的旧版面板。

## 使用测试版

便携文件为 `dist/AppleHome.exe`。它包含 .NET 运行时和 Rust 引擎，使用 x64 Windows，无需另外安装开发工具。当前包未签名，也没有自动更新渠道。

1. 使用已经在 Apple 家庭 App 中初始化的 HomePod，让 Windows PC 与它位于可互通的同一局域网。先确认 iPhone AirPlay 到同一设备能正常出声。
2. 启动 AppleHome。启动保持停止；已有的旧自动连接设置不会启动音频采集。
3. 等待发现设备。发现失败时可选择网卡或添加 IPv4/端口，默认 AirPlay 2 端口为 7000。已验证 mini 配置需要自动发现提供的型号、固件与分组信息；只有手动地址时仍待验证。
4. 在设置中选择音频源：默认播放端点，或指定播放端点。只有播放到该端点的共享模式声音才进入采集。
5. 手动点击设备的开始按钮，再播放 Windows 音乐或浏览器声音。状态“正在发送音频”表示发送端已运行，实际出声需要听音确认。
6. 点击停止结束会话。关闭面板会留在托盘；从设置或托盘选择退出才关闭应用。
7. 如需断线后重试，在设置中勾选“连接中断后自动重连”并应用。该选项默认关闭；真实断网验收本轮由用户选择暂缓。

接收端音量与本机增益独立。当前预览不把 HomePod 安装为 Windows 输出设备，也不默认安装虚拟音频驱动。独占音频、受保护内容、麦克风、跨网段和视频口型同步没有可用性保证。缓冲值为目标值，端到端声学延迟尚未测量。

自动发现的单只 HomePod mini（AudioAccessory5,1、27.0、无立体声或 Apple TV 分组）当前采用已出声的 NTP/ALAC 和 2 秒目标缓冲；200 毫秒目标在本机实测无声。界面显示实际使用的目标值，较低缓冲设置不能覆盖该已验证配置。手动地址缺少所需型号、固件和分组信息，不能据此自动启用该配置。其他型号和固件仍保留原路径，需逐项验证。

家庭 App 的自定义密码、PIN 持久配对和仅家庭成员访问需要分别验收。认证失败时应核对访问设置与设备，不关闭服务端证明或身份验证。

## 本机声音与恢复

应用在收到有声样本指标后才尝试静音本机输出；原状态先原子写入 `%APPDATA%\AppleHome\local-mute.json`。连续两次有声样本指标未推进时，会恢复本机播放并提示选择其他输出设备；自然静音也可能触发该保守回退。

停止、失败、退出和切换采集源时恢复仍由应用拥有的静音改动。用户在 Windows 中解除静音会立即撤销运行中的恢复所有权，自动重连不会重新静音；应用不会改写音量标量。异常退出后的恢复只在正常启动中显式执行。若恢复文件损坏或无法写入，会显示错误；不要为此重置整个系统声音设置。清除文件失败时会保留用户当前选择并重试，但在清除成功前崩溃仍可能留下旧记录，应解决写入错误并重试后再退出。

崩溃期间的同值外部静音写入无法辨认。需要完全独立的本机与 HomePod 路由时，应另行评估虚拟输出端点。

## 数据与诊断

- 设置和日志：`%APPDATA%\AppleHome\`。
- 当前用户保护的配对资料：`%APPDATA%\AppleHome\native-credentials\`。
- 按哈希提取的引擎：`%LOCALAPPDATA%\AppleHome\engine\`。
- 应用不读取或覆盖 AirFlash 的设置与配对目录。
- 日志脱敏标记的 PIN、密码、令牌、密钥与用户路径；导出另处理设备地址和标识。分享前仍请查看诊断文本。
- `--compat-check`、`--audio-check`、`--discovery-check` 为显式诊断入口；音频枚举不会采集或恢复声音。输出使用 `--output <绝对路径>`。

本地原始报告、SDK、缓存、音频、凭据和便携包位于忽略目录，不进入 Git。

## 从源码构建

已验证的开发环境：Windows 10 x64 build 19045；.NET SDK 10.0.402；Rust/Cargo 1.99.0 MSVC 与 Clippy；Visual Studio C++ Build Tools 与 Windows SDK；Python 3.12+、uv；PowerShell 7；Git。

将这些开发工具放入当前进程的 PATH，或使用机器已有安装。仓库的 `scripts/dotnet.ps1` 优先查找仓库本地 `artifacts/dotnet` SDK，再查找已有 SDK。不要把本机工作区中被忽略的 `.runtime` 当作分发依赖。

在仓库根目录执行：

```powershell
uv sync --locked --group dev
pwsh -NoProfile -File scripts/build-native.ps1 -Check
pwsh -NoProfile -File scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh -NoProfile -File scripts/dotnet.ps1 test AirFlash.sln --no-restore
uv run pytest -q
uv run ruff check .
```

界面回归使用模拟音频、发现与引擎服务，不发送到真实扬声器：

```powershell
pwsh -NoProfile -File scripts/dotnet.ps1 run --project AirFlash.App -- --ui-smoke --output ../artifacts/validation/ui-smoke.json
```

发布便携诊断包：

```powershell
$CheckoutRoot = (Get-Location).Path
pwsh -NoProfile -File scripts/dotnet.ps1 publish AirFlash.App -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$CheckoutRoot=/_/" -p:Version=0.1.0 -p:InformationalVersion=0.1.0-rc.1 -o ../build/wpf-publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$Audit = Get-Content artifacts/compatibility/bundle-audit.json -Raw | ConvertFrom-Json
if (!$Audit.ok) { throw 'Compatibility audit failed' }
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item build/wpf-publish/AirFlash.exe dist/AppleHome.exe
Get-FileHash dist/AppleHome.exe -Algorithm SHA256
```

内部程序集名保留 `AirFlash`；最终可执行文件名为 `AppleHome.exe`。完整 MSI/版本预留/外部发布流水线不属于本轮构建。

历史 `scripts/e2e_stream.py` 探测入口没有携带新发现的 mini 配置信息，不能代替最终 WPF 包的实机验收。它的旧 Python `--source loopback` 分支也未纳入产品验证。系统声音应通过用户主动开始的 WPF 路径检查。

## 来源与许可

AppleHome 的修改采用 GPLv3 或更新版本。固定上游提交、源码归档哈希、依赖与基线检查见 [UPSTREAM.md](UPSTREAM.md)。保留 [上游许可说明](LICENSE)、[GPLv3 全文](LICENSE-GPLv3) 和 [第三方声明](desktop/THIRD-PARTY-NOTICES.md)。上游另有商业许可选项，不代表 AppleHome 修改获得或提供该许可。

与本地包对应的完整可构建源码在当前 Git checkout；交付目录另提供源码归档。此项目没有对外发布仓库或二进制。
