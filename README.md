# AppleHome

面向 Windows 10/11 x64 的原生 HomePod 音频发送项目，采用 .NET 后台、Rust 音频引擎和 GPLv3+ 开源许可，保留 WPF 诊断界面，基于 Ding-Kyoma 的 [AirFlash](https://github.com/Ding-Kyoma/AirFlash)。

**当前为限定设备的测试候选：Windows 10 Pro 22H2（19045）到单只 HomePod mini 27.0，已通过正常音乐30分钟连续播放、Windows主音量与静音恢复、暂停恢复，以及约5分钟正常视频的主观嘴型同步和无明显漂移。** 缓冲按设备记录、匹配的设备档案和接收端回包自动处理，不要求用户填写毫秒数。视频开始采样为1倍速、末次读取为0.8倍速，未测量精确音画偏差。Windows 11、其他型号、其他固件和立体声对尚未取得实机资格；原生向导目前只开放上述单只 mini 配置。

详细结果见 [兼容矩阵](docs/COMPATIBILITY.md)、[验证记录](docs/VALIDATION.md)。调研、已审阅规格和实施计划保存在 `docs/research/` 与 `docs/superpowers/`。

保留的实机验收快照为 `dist/AppleHome-61992a2-current-mini-20261019.zip`。新的安装候选位于 `dist/auto-discovery-20261020/AppleHome.exe`：[向导](docs/first-run.md)自动安装原厂驱动、命名和选择 Windows 输出、准备连接并播放确认音频，用户可点“再播放一次”，再确认“听到了 / 没听到”。界面参考 Apple Devices 的浅灰层次、设备标识和简洁操作区；不依赖 Apple Devices。连接阶段预先准备音源，健康连接下重播复用同一播放器，不再每次启动子进程和固定等待2秒。多只 HomePod 时选择目标，Windows 要求的安装许可和驱动重启保留。搜索失败时显示HomePod提示与“重新查找”，不要求用户手动添加设备。10月20日修正版已更新正式后台、自动选择Windows输出并打开“已准备好”窗口，用户确认正常音乐连续播放；没有播放测试短音，也没有要求填写IP或选择网卡。新自动流程的完整安装及长期资格按独立实机记录确认。最新结果见[原生输出验收记录](docs/validation/windows-native-homepod-output.md)。

## Windows 原生输出候选

首次设置请使用上述带向导的当前交付包并先打开应用，按界面步骤继续。旧的 `dist/native-output-level-fix/`、`dist/startup-61992a2-30min/` 等目录保留其生成时快照，不作为当前安装入口。未完成驱动身份、重启、命名或校准检查时，应用会明确停止设置。

1. 打开新候选。程序校验内附的原始 VB-CABLE ZIP 与全部文件，仅在缺少驱动且安装检查允许时调用原厂安装器。按 Windows 要求允许安装并完成必要重启。
2. 程序发现 HomePod；唯一合格设备自动连接，多只时选择目标。名称、Windows 默认播放输出和连接参数均自动配置。
3. 程序自动播放一次有限提示音；点击“听到了”完成，点击“没听到”执行有限诊断与重试。不需要手动改名、切输出、启动音乐或填写参数。
4. 明确确认后才保存可听参考和校准，启用登录后台。已有可用安装在重新连接时复用原程序，向导不自动升级二进制。
5. 日常用 Windows 输出、音量、静音和应用混音器；无需打开向导。安装仅设置默认播放角色，后台不改物理输出静音、录音设备或各应用的独立路由。

首轮原生模式仅允许自动发现的单只 HomePod mini `AudioAccessory5,1 / 27.0`，采用已验证的 NTP/ALAC 路径。当前这只设备有自己的已验证100ms请求记录；它不是所有设备的固定默认、已测出的声学下限或总延迟。没有记录时，程序按接收端合法边界和严格匹配的设备档案选择候选；普通音乐确认后才保存参考，未匹配的候选和自动调稳不等于任意设备均已支持。正常音乐30分钟已通过，首次安装、登录及名称跨重启仍待验收；断网测试暂缓。

无参数入口打开首次设置或当前状态；登录任务使用明确的无窗口后台入口，旧面板保留为 `--legacy-panel`。健康后台无日常托盘，不保存密码、不创建系统服务；用户修改过的任务或安装 EXE 会被保留。以下命令仅供维护，普通用户使用向导即可。

只读检查和撤销：

```powershell
.\AppleHome.exe --native-check --output "G:\code\applehome\artifacts\reports\applehome-native-check.json"
.\AppleHome.exe --native-disable
.\AppleHome.exe --native-unbind --output "G:\code\applehome\artifacts\reports\applehome-unbind.json"
```

`disable` 先移除仍属本应用的登录任务，再优雅停止后台。`unbind` 再移除本应用绑定；保留用户设备名称、应用文件和文件所有权证明，不卸载第三方驱动。驱动卸载使用厂商工具，留在用户明确安排的撤销窗口。诊断检查不自动长播、录音或启用任务。

## 历史诊断面板

`dist/AppleHome.exe` 及下述主动开始/停止流程属于历史面板基线，仅作诊断和回退；当前向导与Windows原生输出请使用前面的交付入口。旧面板可通过 `--legacy-panel` 打开。当前交付EXE未签名，也没有自动更新渠道。

1. 使用已经在 Apple 家庭 App 中初始化的 HomePod，让 Windows PC 与它位于可互通的同一局域网。先确认 iPhone AirPlay 到同一设备能正常出声。
2. 启动 AppleHome。启动保持停止；已有的旧自动连接设置不会启动音频采集。
3. 等待发现设备。发现失败时可选择网卡或添加 IPv4/端口，默认 AirPlay 2 端口为 7000。已验证 mini 配置需要自动发现提供的型号、固件与分组信息；只有手动地址时仍待验证。
4. 在设置中选择音频源：默认播放端点，或指定播放端点。只有播放到该端点的共享模式声音才进入采集。
5. 手动点击设备的开始按钮，再播放 Windows 音乐或浏览器声音。状态“正在发送音频”表示发送端已运行，实际出声需要听音确认。
6. 点击停止结束会话。关闭面板会留在托盘；从设置或托盘选择退出才关闭应用。
7. 如需断线后重试，在设置中勾选“连接中断后自动重连”并应用。该选项默认关闭；真实断网验收本轮由用户选择暂缓。

以下边界仅描述历史面板：接收端音量与本机增益独立，不提供原生输出端点。当前原生模式通过独立VB-CABLE提供Windows输出，日常无需面板。独占音频、受保护内容、跨网段和精确端到端声学延迟仍未验证，麦克风不属于系统音频采集范围。

旧面板曾使用NTP/ALAC与2秒目标，其200ms无声记录属于当时的版本和音源条件，不能套用到当前原生模式。后续100ms可听、自动适配、采集积压修复和正常音乐/视频的结果以最新验收记录为准。原生向导需要发现提供的完整型号、固件与分组信息，不能只凭手动地址放行未验证设备。

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
pwsh -NoProfile -File scripts/prepare-native-driver.ps1
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
