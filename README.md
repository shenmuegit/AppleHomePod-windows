# AppleHome

面向 Windows 10/11 x64 的原生 HomePod 音频发送项目，采用 .NET 后台、Rust 音频引擎和 GPLv3+ 开源许可，保留 WPF 诊断界面，基于 Ding-Kyoma 的 [AirFlash](https://github.com/Ding-Kyoma/AirFlash)。

**当前为可用测试版：Windows 10 Pro 22H2（19045）到单只 HomePod mini 27.0 的系统音乐播放、手动停止和本机声音恢复已由用户实机确认。** 当前使用 2 秒目标缓冲；声学延迟与视频同步未测量。Windows 11、其他型号、其他固件和立体声对均待实机验证。

详细结果见 [兼容矩阵](docs/COMPATIBILITY.md)、[验证记录](docs/VALIDATION.md)。调研、已审阅规格和实施计划保存在 `docs/research/` 与 `docs/superpowers/`。

Windows 原生输出候选的软件实现已完成：日常通过 Windows 选择 HomePod、调节音量与静音，后台自动发送，常态不显示窗口或托盘菜单。2026-10-16 已核验驱动重启、实际身份和 Windows 系统命名，PCM/应用混音器正向测量通过；可听参考待用户确认并保存。名称是在此次重启后修改，跨重启持久性、实际启用与听音验收仍待完成，候选尚未替换日常入口。详见[设计规格](docs/superpowers/specs/2026-10-14-windows-native-homepod-output-design.md)、[实施计划](docs/superpowers/plans/2026-10-14-windows-native-homepod-output-implementation.md)和[新路径验收记录](docs/validation/windows-native-homepod-output.md)。

## Windows 原生输出候选

最新校准修正候选放在 `dist/native-output-f462560/`，`dist/native-output/` 保留前一候选，原 `dist/AppleHome.exe` 仍是已出声的旧面板基线。候选需要一次设置；未完成资格检查时会拒绝采集、校准或启用后台。

1. 单独从 [VB-Audio 官方网站](https://vb-audio.com/Cable/)取得并安装已核验的普通 VB-CABLE Pack45，按厂商要求重启。AppleHome 不分发、嵌入或自动运行驱动安装器；固定版本/哈希见新路径验收记录。已有其他虚拟设备不会被猜测占用。
2. 首次在 Windows 的 `mmsys.cpl` →“播放”中，把普通 `CABLE Input (VB-Audio Virtual Cable)` 的属性名称设为 `HomePod – da`，核对重启后名称保留。不要选 16ch 或录音端点；之后的用户改名会保留。
3. 通过维护流程完成稳定接收端身份绑定、PCM 增益位置校准、应用混音器检查与可听非零参考确认。仅成功发包不构成听音确认，不自动把 HomePod 音量设置为 100%。
4. 资格通过并退出旧面板后，从候选运行 `AppleHome.exe --native-enable`。它仅安装本应用到 `%LOCALAPPDATA%\AppleHome\app\`，注册当前用户的限权登录任务，立即运行并等待采集就绪，无需再次登录。当前未通过门槛，不执行这一步。
5. 日常在 Windows 音频输出中选择 HomePod，用 Windows 音量、静音和应用混音器控制。切回其他输出后，在虚拟端点仍有播放需求的应用会继续使用它；单应用暂停保留 30 秒。后台不改物理输出静音或其他应用的路由。

首轮原生模式仅允许自动发现的单只 HomePod mini `AudioAccessory5,1 / 27.0`，采用 NTP/ALAC、44100 Hz 和 2000 ms 目标缓冲。首段暂存还会产生额外启动延迟；端到端延迟、视频同步、可听静音/切换尾音、真实 30 分钟稳定性、登录及错误通知仍待实测。Windows 11、其他型号/固件和立体声对不具备资格结论。断网测试已暂缓。

原生候选的无参数入口运行后台；旧面板仅以 `--legacy-panel` 或显式诊断进入。发生需要处理的错误时短暂使用系统通知，遵循系统通知限制，健康后台无日常托盘。当前用户任务名为 `AppleHome.NativeOutput.<SID>`，不保存密码、不创建系统服务，失败最多重启 3 次，间隔 1 分钟。用户修改过的任务或安装 EXE 会被保留。

只读检查和撤销：

```powershell
.\AppleHome.exe --native-check --output "$env:TEMP\applehome-native-check.json"
.\AppleHome.exe --native-disable
.\AppleHome.exe --native-unbind --output "$env:TEMP\applehome-unbind.json"
```

`disable` 先移除仍属本应用的登录任务，再优雅停止后台。`unbind` 再移除本应用绑定；保留用户设备名称、应用文件和文件所有权证明，不卸载第三方驱动。驱动卸载使用厂商工具，留在用户明确安排的撤销窗口。诊断检查不自动长播、录音或启用任务。

## 使用旧版面板基线

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

## 旧版面板的本机声音与恢复

应用在收到有声样本指标后才尝试静音本机输出；原状态先原子写入 `%APPDATA%\AppleHome\local-mute.json`。连续两次有声样本指标未推进时，会恢复本机播放并提示选择其他输出设备；自然静音也可能触发该保守回退。

停止、失败、退出和切换采集源时恢复仍由应用拥有的静音改动。用户在 Windows 中解除静音会立即撤销运行中的恢复所有权，自动重连不会重新静音；应用不会改写音量标量。异常退出后的恢复只在正常启动中显式执行。若恢复文件损坏或无法写入，会显示错误；不要为此重置整个系统声音设置。清除文件失败时会保留用户当前选择并重试，但在清除成功前崩溃仍可能留下旧记录，应解决写入错误并重试后再退出。

崩溃期间的同值外部静音写入无法辨认。原生输出候选使用独立虚拟端点，正常后台不调用这条物理静音/恢复路径；旧 journal 仅在首次显式启用、资格通过且旧面板退出后按所有权规则迁移一次。

## 数据与诊断

- 设置和日志：`%APPDATA%\AppleHome\`。
- 当前用户保护的配对资料：`%APPDATA%\AppleHome\native-credentials\`。
- 按哈希提取的引擎：`%LOCALAPPDATA%\AppleHome\engine\`。
- 原生绑定和校准：`%APPDATA%\AppleHome\native-output.json`；安装前证明和启动所有权证明在同一私有目录。
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

构建独立原生候选，保留旧便携包：

```powershell
$CheckoutRoot = (Get-Location).Path
pwsh -NoProfile -File scripts/dotnet.ps1 publish AirFlash.App -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$CheckoutRoot=/_/" -o ../artifacts/native-output-publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$Audit = Get-Content artifacts/compatibility/bundle-audit.json -Raw | ConvertFrom-Json
if (!$Audit.ok) { throw 'Compatibility audit failed' }
New-Item -ItemType Directory -Force dist/native-output | Out-Null
Copy-Item artifacts/native-output-publish/AirFlash.exe dist/native-output/AppleHome.exe
Get-FileHash dist/native-output/AppleHome.exe -Algorithm SHA256
```

内部程序集名保留 `AirFlash`；最终可执行文件名为 `AppleHome.exe`。完整 MSI/版本预留/外部发布流水线不属于本轮构建。

候选应附代码提交、文档提交、EXE/引擎 SHA256、对应源码和资格状态 manifest。内嵌引擎必须来自本次构建；若实机验收后改了代码，需要新包和受影响验收。历史 `scripts/e2e_stream.py` 和模拟回归均不能代替当前候选的 Windows 原生选择及听音验收。

## 来源与许可

AppleHome 的修改采用 GPLv3 或更新版本。固定上游提交、源码归档哈希、依赖与基线检查见 [UPSTREAM.md](UPSTREAM.md)。保留 [上游许可说明](LICENSE)、[GPLv3 全文](LICENSE-GPLv3) 和 [第三方声明](desktop/THIRD-PARTY-NOTICES.md)。上游另有商业许可选项，不代表 AppleHome 修改获得或提供该许可。

与本地包对应的完整可构建源码在当前 Git checkout；交付目录另提供源码归档。此项目没有对外发布仓库或二进制。
