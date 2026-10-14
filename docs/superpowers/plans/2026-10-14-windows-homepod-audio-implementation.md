# Windows HomePod Audio Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付能将 Windows 系统音频发送至用户 HomePod mini 的原生 AppleHome 测试版，并为 Windows 11、其他 HomePod 型号和已有立体声对保留明确的兼容验收矩阵。

**Architecture:** 固定并复用 AirFlash 的 WPF/.NET 界面和 Rust 音频引擎，通过已有 JSONL v1 接口通信。复用设备发现、WASAPI、HAP、时钟和音频处理；优先修复启动行为、产品数据隔离、IPC 长度边界和本机声音状态恢复。暂保留内部 `AirFlash.*` 命名空间、程序集资源名和 Rust crate 名，避免全仓改名。

**Tech Stack:** Windows x64、.NET 10/WPF、Rust MSVC（上游最低 1.85）、NAudio 2.2.1、Windows DNS-SD、上游锁定的 Python/uv 验证工具。

**Spec:** [已获用户确认的设计规格](../specs/2026-10-14-windows-homepod-audio-design.md)。状态：本实施计划待用户审阅与执行方式选择，尚未开始产品实现。

## Global Constraints

- Windows 10：首轮基线为 22H2 x64 / build 19045；Windows 11：x64 桌面应用。
- 用户目前有 HomePod mini；第 1/2 代、已有立体声对和 mini 2 纳入目标，但只有实际测试过的组合可标记为“已验证”。
- 启动后处于停止状态；没有用户的开始操作，不启动声音采集。
- 音频只在内存处理并发送到用户选择的局域网接收端，默认不录音落盘，不采集麦克风，不增加遥测。
- AppleHome 使用自己的应用数据目录，不读取、覆盖或迁移既有 AirFlash 等产品的设置或凭据。
- 认证证明或接收端身份验证失败必须终止，不切换为允许失败的模式。
- 音量范围 0–100；现有发送采样率为 44100/48000；保留协议报文、超时、取消与重试边界。
- 复用 GPLv3+ 来源、版权、许可证、第三方说明和已有测试；不新增第二套音频引擎、云服务、自研驱动或更新服务。
- 实际画声同步单独验收；目标缓冲、发包成功和 UI 状态均不能替代声学延迟证据。
- 自动实机探测沿用上游：时长不超过 5 秒、WAV 峰值不超过 0.05、发送增益不超过 0.1；长播观察独立记录。

## Review Focus

| 最可能影响用户的条件 | 应有行为 | 负责的任务和测试 |
| --- | --- | --- |
| 保存的自动连接设置为 true，设备在启动时立即出现 | 仍保持停止，只有用户点击开始才创建音频会话 | Task 2：`SavedAutoConnectNeverStartsCapture` UI smoke |
| 同一接收端有两种广播、地址变化、旧回调晚到 | 按物理身份去重，旧结果不能覆盖新网卡/新会话 | Task 3：复用 `ReceiverIdentityTests`、`WindowsDiscoveryTests` 的身份与取消测试 |
| 引擎输出超长无换行、半行 EOF 或停止时正在读取 | 有界读取，错误可取消，不能等到无限分配后才检查 | Task 3：`EngineJsonLineTests` |
| 用户改静音、设备移除或进程在静音后崩溃 | 仅恢复应用拥有的改变，用户修改优先，未完成恢复可重试 | Task 4：`MuteRecoveryTests`、`SessionTests` |
| 密码/家庭权限拒绝或伪造接收端证明 | 停止，明确提示，不能无限重连或将 PIN/密钥写入日志 | Task 3：诊断与认证回归；Task 5：真实访问模式记录 |

## 文件与现有接口

所有相对路径均相对于执行 checkout 根目录；当前仓库为 `G:\code\applehome`。执行前按 `using-git-worktrees` 检查工作区与隔离方式，不能把另一个 checkout 的绝对路径用于修改或清理。

| 单元 | 原有文件/目录 | 用途与改动边界 |
| --- | --- | --- |
| 上游基础 | `desktop/`、`native/airflash-engine/`、`scripts/`、`tests/`、`.cargo/`、`pyproject.toml`、`uv.lock` | 固定来源导入，保留源码、资源、测试与锁文件；保留现有研究和规格 |
| 产品身份 | `desktop/AirFlash.App/Services/AppPaths.cs`、`SingleInstance.cs`、`TrayService.cs`；`native/airflash-engine/src/credentials.rs` | 显示名称、数据目录、互斥量/管道、托盘标识、凭据目录隔离 |
| 启动与交互 | `desktop/AirFlash.App/App.xaml.cs`、`Program.cs`、`ViewModels/AppViewModel.cs`、`SettingsViewModel.cs`、`Ui/ControlPanel.xaml`、`SettingsWindow.xaml` | 主动开始、明确状态和访问失败说明，保留已有键盘/托盘行为 |
| 输入与进程 | `desktop/AirFlash.Core/EngineConnection.cs`、`Settings.cs`、`DiagnosticText.cs`；`Services/WindowsDiscovery.cs` | 有界 IPC、手动 IPv4 验证、复用发现与取消、诊断脱敏 |
| 音频状态 | `Services/AudioService.cs`、`desktop/AirFlash.Core/SessionController.cs`；新增 `MuteRecoveryJournal.cs`、`LocalMuteGuard.cs` | 日志化应用拥有的静音改变；用现有采集指标保守校验本机静音 |
| 自动化 | `desktop/AirFlash.Tests/`、`desktop/AirFlash.App/Verification/UiSmoke.cs`；现有 Rust/Python 测试 | 扩展真实差距对应的回归，复用已有认证/传输测试 |
| 交付 | `README.md`、新增 `UPSTREAM.md`、`docs/COMPATIBILITY.md`、`docs/VALIDATION.md` | 可复制启动命令、来源、测试证据、未验证组合；便携包进入 `dist/` |

沿用的关键接口：`SessionController.StartAsync(Receiver, AppSettings)`、`StopAsync()`、`PairAsync(Receiver, AppSettings, Func<Receiver,CancellationToken,Task<string?>>)`；`IAudioService.MuteAsync(string?)` / `RestoreAsync()`；`IEngineConnection.SendAsync(string,string,object?,CancellationToken)` / `ReadAsync(CancellationToken)`。不要为相同职责再加包装服务。

## Task 1: 固定基础源码并证明可构建

**Files:** Create 上表的上游基础、许可证与 `UPSTREAM.md`；合并 `.gitignore` / `.gitattributes`；保留 `docs/research/` 和 `docs/superpowers/`。SDK、工具和报告仅进入忽略目录 `artifacts/` / `.runtime/`，不导入上游 `.github/` 发布工作流。

**Interfaces:** Consumes 已批准规格及固定提交；Produces `scripts/dotnet.ps1`、现有解决方案、`native/airflash-engine/target/release/airflash-engine.exe` 和可编译 WPF 项目。此任务不启动正常界面或真实音频，避免尚未隔离的数据路径影响已有 AirFlash。

- [ ] **Step 1: 验证并导入固定来源。** 使用 `https://codeload.github.com/Ding-Kyoma/AirFlash/zip/cf420d97700463c56adc31408510e8336e99e51c`，记录提交和取得的归档 SHA256；本轮归档 SHA256 为 `f1110808957de47f1c0e7d550b46341206d9d9e59ede73c4b0a496af2f7fe645`。解压前校验每个目标都位于 staging 目录内，导入前检查所有同名文件；既有文档不能被覆盖。保留 `LICENSE`、`LICENSE-GPLv3`、`LICENSE-COMMERCIAL.md` 与第三方归属，明确本项目采用 GPLv3+，不宣称取得商业授权。
- [ ] **Step 2: 准备仅当前构建进程使用的工具。** 本轮已运行确认 Rust/Cargo 1.99.0，路径为 `G:\code\grove\.runtime\tools\rustup\toolchains\stable-x86_64-pc-windows-msvc\bin`；已有 MSVC 组件可复用。Codex bundled Python 为 3.12.14，路径 `C:\Users\Administrator\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`。重新核验这些路径，加入本次进程 PATH；本次进程的 `CARGO_HOME`、`UV_CACHE_DIR` 分别指向 checkout 的 `artifacts/cargo-home`、`artifacts/uv-cache`。`.gitignore` 另加 `.runtime/`；不写用户/系统 PATH，不更改全局 Rust 默认版本。
- [ ] **Step 3: 补齐项目工具。** 若无可满足 `desktop/global.json`（10.0.100、latestFeature）的 SDK，从 Microsoft 官方取得 `artifacts/dotnet-install.ps1`，运行 `pwsh -NoProfile -File artifacts/dotnet-install.ps1 -Channel 10.0 -Quality GA -Architecture x64 -InstallDir artifacts/dotnet -NoPath`，记录实际版本。使用 bundled Python 的 `-m venv .runtime/python-tools` 创建工具环境，再用该环境的 Python 执行 `-m pip install uv` 并记录版本；将该 venv 的 Scripts 仅加入当前进程 PATH，然后 `uv sync --locked`。不能通过降低 Python/.NET 要求、禁用审核或删除锁文件完成构建。
- [ ] **Step 4: 执行基础检查。** 逐条运行 `pwsh -NoProfile -File scripts/check-dotnet.ps1`、`pwsh -NoProfile -File scripts/dotnet.ps1 restore AirFlash.sln --locked-mode`、`pwsh -NoProfile -File scripts/build-native.ps1 -Check`、`pwsh -NoProfile -File scripts/dotnet.ps1 test AirFlash.sln --no-restore`、`uv run pytest`、`uv run ruff check .`。预期退出码均为 0；测试数量与任何原有失败写入 `artifacts/validation/baseline.md`，基线失败必须先定位，不能当作新功能已通过。
- [ ] **Step 5: 验证引擎 hello。** 通过已有 JSONL 发送 `{"version":1,"id":"check","session_id":"check","command":"hello","params":{}}`，断开 stdin 并确认进程结束。预期 `event=hello`、`version=1`、`live_loopback=true`；保留 `qualification=partial` / `production_ready=false`，不能将它们改成成功声明。记录工具版本及来源到 `UPSTREAM.md`，提交 `build: import pinned AirFlash baseline`。

## Task 2: 隔离 AppleHome 身份并确保主动开始

**Files:** Modify `desktop/AirFlash.App/Services/AppPaths.cs`、`SingleInstance.cs`、`TrayService.cs`、`desktop/AirFlash.App/Program.cs`、`App.xaml.cs`、`ViewModels/AppViewModel.cs`、`SettingsViewModel.cs`、`Ui/ControlPanel.xaml`、`SettingsWindow.xaml`；Modify `desktop/AirFlash.Core/Strings.zh.json` / `Strings.zh-TW.json` 和 `native/airflash-engine/src/credentials.rs`。Modify `desktop/AirFlash.App/Verification/UiSmoke.cs` 与现有设置/本地化测试。

**Interfaces:** Consumes Task 1 的基础；Produces `AppPaths.Name="AppleHome"`、`%APPDATA%\AppleHome` 设置/日志、`%LOCALAPPDATA%\AppleHome\engine` 缓存、`%APPDATA%\AppleHome\native-credentials` DPAPI 文件，以及与 AirFlash 不冲突的实例/托盘身份。现有程序集、资源和 JSONL 名称保持兼容。

- [ ] **Step 1: 添加最小回归。** 在 `UiSmoke` 现有 fake store/engine 中加入 `SavedAutoConnectNeverStartsCapture`：全局和设备自动连接均设 true、立即发现设备、完成启动，断言没有 `start` 命令；调用用户 `ToggleAsync(receiver)` 后才有一次 `start`。重启再次保持停止。使用隔离的测试数据根，写入旧 AirFlash 配置哨兵，验证 AppleHome 的加载/保存不改变它；Rust 凭据目录测试断言以 `AppleHome/native-credentials` 结尾，不操作旧产品真实数据。
- [ ] **Step 2: 跑回归并读失败。** 运行 `pwsh -NoProfile -File scripts/dotnet.ps1 run --project AirFlash.App -- --ui-smoke --output ../artifacts/validation/identity-smoke.json`；输出报告 `ok=false` 应指出自动发送/隔离断言，不以编译错误替代行为失败。不为单纯改名写复制常量的测试。
- [ ] **Step 3: 改共享入口。** 先 `rg -n 'AppPaths.Name|AirFlash.Wpf|AutoConnect|ScheduleAutoConnect|new AudioService|native-credentials' desktop native`。修改路径与稳定身份；移除启动/发现自动发送入口及对应可操作 UI，保留读取旧 JSON 字段的兼容性。用户点击开始仍用 `ToggleAsync` 和现有 session；正在发送时的有界重连继续可用。正常启动不注册未选择的自启动项。
- [ ] **Step 4: 收敛用户可见身份。** 标题、托盘、启动错误使用 `AppPaths.Name`；使用独立且固定的托盘 GUID。保留上游来源说明，解除“检查 AirFlash 更新/下载 AirFlash”与本产品界面的连接；不另建更新器或替代下载站。已有 EQ 内部能力不扩展，不为删它重写协议或测试。
- [ ] **Step 5: 验证后提交。** 运行 `pwsh -NoProfile -File scripts/build-native.ps1 -Check` 重建 release 引擎，再重新构建 WPF，防止界面仍嵌入修改前的凭据目录代码。上述隔离/启动 UI smoke、现有设置/本地化/托盘测试全部通过；Rust 凭据测试确认目录隔离且 DPAPI roundtrip/tamper 仍通过。确认首次正常启动只写 AppleHome 目录，再提交 `feat: isolate AppleHome identity and require manual playback`。

## Task 3: 补齐发现、输入与 IPC 边界

**Files:** Modify `desktop/AirFlash.Core/EngineConnection.cs`、`AirFlash.Core.csproj`、`Settings.cs`、`DiagnosticText.cs`、`Services/AppPaths.cs`、`ViewModels/SettingsViewModel.cs` / `AppViewModel.cs`；只有复现失败时修改 `Receiver.cs`、`ReceiverCatalog.cs`、`WindowsDiscovery.cs`。Create `desktop/AirFlash.Tests/EngineJsonLineTests.cs`；Modify `SettingsTests.cs`、`DiagnosticTextTests.cs`、相关现有发现测试。

**Interfaces:** Consumes 原有 `IEngineConnection`、`ReceiverAggregator.Build(IEnumerable<ServiceRecord>,Action<string>?)` 与设置验证。Produces 保持签名的 `ReadAsync(CancellationToken)`，以及同文件内可测试的 `internal static Task<string?> ReadBoundedLineAsync(TextReader reader,CancellationToken cancellation)`；长度上限沿用 65,536 个字符，限制须在读取过程中生效，UTF-8 解析仍沿用进程连接。

- [ ] **Step 1: 写有界读取测试。** `EngineJsonLineTests` 覆盖：65,536 字符加 LF 或 CRLF 可读；第 65,537 个有效载荷字符即抛 `IOException`；超长无换行不得继续读取；半行 EOF 由 JSON 解析拒绝；读取等待可被 token 取消；连续两行不丢数据。测试内定义 `CountingTextReader(string input)`，其只读 `CharactersRead` 统计真正返回给调用方的字符；不能只断言最终字符串长度。先运行该测试类，确认原有“整行后检查”无法满足提前终止断言。关键测试 `OversizeUnterminatedLineStopsReadingAtLimit` 的断言为：

```csharp
var reader = new CountingTextReader(new string('x', 100_000));
await Assert.ThrowsAsync<IOException>(() =>
    ProcessEngineConnection.ReadBoundedLineAsync(reader, CancellationToken.None));
Assert.Equal(65_537, reader.CharactersRead);
```
- [ ] **Step 2: 最小修复共享读入口。** 先搜索所有 `ReadAsync` 调用方，再在 `EngineConnection.cs` 中实现有界 helper 并接入现有方法；只为测试添加 Core 的 `InternalsVisibleTo`。不新增可配置上限、另一个 IPC 服务或依赖。保留 EOF、JSON 错误、超时、旧 session 丢弃和进程清理行为。
- [ ] **Step 3: 验证手动 IPv4 与发现。** `SettingsTests` 加入 `ManualReceiverRejectsInvalidIpv4BeforeSaving`，断言非法地址与端口 0/65536 不能写入配置，合法 IPv4 和端口 7000 可用；`AddManual` 在修改 draft 前复用 `AppSettings.Validate()` 的规则。复跑现有 `ReceiverIdentityTests`、`WindowsDiscoveryTests` 中双广播、身份升级、旧回调、缺 DNS API 与网卡取消用例；已覆盖且通过的能力不另写同义测试、不强行改实现。
- [ ] **Step 4: 错误与隐私回归。** 复用 `AuthenticationFailureDoesNotReconnect`、`StructuredNonretryableFaultDoesNotDependOnMessageText`、`SwitchingWaitsForPreviousCleanupAndRejectsStaleEvents`；为日志/导出加入含合成 PIN、password、secret、用户路径和设备标识的断言，确保本地日志入口调用已有 `DiagnosticText.Protect`，诊断导出另去除地址和设备身份。错误显示可操作访问说明，不自动修改家庭权限。
- [ ] **Step 5: 验证后提交。** 运行上述测试类、现有 engine handshake/initialization 和完整 Core 测试；相应 Rust SRP、身份交换、RTSP 边界和取消测试继续通过。补充 UI smoke：无设备、缺 API、手动输入拒绝、引擎失败不关闭外壳。提交 `fix: bound engine IPC and validate receiver inputs`。

## Task 4: 安全管理本机静音与崩溃恢复

**Files:** Create `desktop/AirFlash.Core/MuteRecoveryJournal.cs`、`LocalMuteGuard.cs`、`desktop/AirFlash.Tests/MuteRecoveryTests.cs`、`LocalMuteGuardTests.cs`；Modify `Services/AudioService.cs`、`SessionController.cs`、`App.xaml.cs`、`SessionTests.cs`、对应错误文案。

**Interfaces:** `IAudioService` 的公共方法保持不变。实际 `AudioService` 使用 `AudioService(string recoveryPath)` 和 `Task RecoverPendingMuteAsync()`，恢复动作只由正常启动显式调用，诊断模式不调用。新增 `MuteRecoveryJournal(string path)`，公开 `IReadOnlyDictionary<string,bool> Entries { get; }`、`void Remember(string endpointId,bool originalMute)`、`void Forget(string endpointId)`、`bool? RestoreValue(string endpointId,bool currentMute)`；仅记录应用拥有的原静音状态。新增 `LocalMuteAction { None, Mute, RestoreAndWarn }` 与 `LocalMuteAction LocalMuteGuard.Observe(long lastAudioQpcNs)`；使用现有 `capture_metrics.last_audio_qpc_ns`，无需增加 Rust 音频协议字段。

- [ ] **Step 1: 写状态与持久化测试。** 用临时目录测试以下契约：`Remember(id,false)` 后重新打开 journal 保留 false；`RestoreValue(id,true)` 返回 false；当前值已变为 false 时不提出恢复；`Forget` 后重启不恢复；重复 Remember 不覆盖最初状态。文件为 schema 1、最多 65,536 字节，损坏/超长不能改变真实音频状态；写入失败不能先把设备静音。先跑 `MuteRecoveryTests` 确认行为缺失。
- [ ] **Step 2: 实现最小 journal。** 使用标准库 JSON 和同目录临时文件原子替换，落盘原状态后才允许改设备。AudioService 保留已有专用 COM 线程，持有被静音端点及回调至恢复完成；使用 NAudio 2.2.1 的 `NotificationGuid` 和通知 `EventContext` 区分自己的改变。外部通知改变静音时放弃该端点的恢复所有权；音量标量不由应用重写。设备暂不可用时保留条目，成功恢复或确认当前状态已改变后移除。
- [ ] **Step 3: 接入启动和清理。** 更新 `App.xaml.cs` 的两个 AudioService 构造调用点；正常模式传 AppleHome 数据目录下的 `local-mute.json`，诊断模式传自身隔离目录。构造不恢复声音；正常启动外壳创建后、启动 view model 前显式调用 `RecoverPendingMuteAsync()`，只读 `AudioCheckAsync` 不调用它。损坏/写失败时给出明确错误并保留“同时本机播放”路径，不重置整个系统音频。崩溃后只能比较当前值与应用设置值，无法推断离线期间同值重设；在相关判断旁写 `shortcut:` 注释，说明需要完全独立路由时再升级虚拟端点。
- [ ] **Step 4: 校验静音是否影响采集。** 先写并运行 `LocalMuteGuardTests.StalledAfterMuteRestoresOnlyOnce`，断言如下；另覆盖正常递增不回退，缺指标不贸然静音。搜索并统一 `SessionController` 的全部 `audio.MuteAsync` 调用点，包含发送开始和运行中设置应用，只有 guard 获得有声样本证据后才静音。连续两个指标不推进时恢复本机播放、显示“采集未继续收到有声样本，已恢复本机播放，可选择其他输出设备”。同一用户启动产生的重连不能重新自动静音；用户重新开始或明确更换采集源才新建 guard。它是保守回退，不能据此认定驱动损坏。

```csharp
var guard = new LocalMuteGuard();
Assert.Equal(LocalMuteAction.None, guard.Observe(0));
Assert.Equal(LocalMuteAction.Mute, guard.Observe(10));
Assert.Equal(LocalMuteAction.None, guard.Observe(10));
Assert.Equal(LocalMuteAction.RestoreAndWarn, guard.Observe(10));
Assert.Equal(LocalMuteAction.None, guard.Observe(11));
```
- [ ] **Step 5: 验证整个生命周期。** `SessionTests` 增加停止重连后不再开始、静音回退不重启引擎、不采集时不静音、切端点先恢复旧端点；复用 Stop/失败/取消的已有测试。journal 的 Forget/重启契约配合 Task 5 的真实外部静音修改验证所有权，不用 fake 音频实现来冒充 Windows 回调测试。停止、失败、退出和设备变更必须清理通知订阅与 COM 对象。运行 journal、guard、session 和 UI smoke，通过后提交 `fix: restore only app-owned local mute changes`。

## Task 5: 在 mini 上证明真实声音通路

**Files:** Use 现有 `scripts/e2e_stream.py`、`native_probe.py`、WPF 应用和采集代码；Create `docs/COMPATIBILITY.md` / `docs/VALIDATION.md`。只有出现可复现失败时修改该失败实际涉及的 Windows/认证/会话/音频文件，并给根因加入最小回归。

**Interfaces:** Consumes Tasks 1–4 的应用和引擎；Produces 精确记录 Windows 构建、型号、固件、访问策略、网卡/采集端点、协商方式、二进制哈希及实机结果的矩阵。原始本地地址仅留在忽略的本机诊断中，提交的记录使用脱敏标识。

- [ ] **Step 1: 实际发现与权限检查。** 启动应用但保持停止，确认 DNS-SD、重复服务合并、所选网卡和可读错误。从发现和 `/info` 取得可取得的型号/版本；无法取得固件时请用户从家庭 App 提供。不要扫描无关网段或修改 HomePod 初始化/家庭权限。
- [ ] **Step 2: 短时原生探测。** 将上一步实际发现并选中的设备 IPv4 保存到 PowerShell 变量 `$ReceiverAddress`，运行 `uv run python scripts/e2e_stream.py --host $ReceiverAddress --source wav --duration 5 --gain 0.1 --timing ptp --report artifacts/validation/mini-probe.json`。本步骤明确只用 WAV；上游 Python `--source loopback` 分支引用已不存在的 `app.audio_volume`，不能未经修复就拿它验证系统采集。系统声音由下一步真实 WPF 路径验收，不为未使用的旧 helper 分支重写音量服务。
- [ ] **Step 3: 确认实际出声，再验收系统声音。** 传输报告退出 0 只记 transport completed；用户听到测试音或独立采集确认后才记录 audible。随后用户点击开始，播放实际 Windows 音乐/浏览器声音，验证音量、静音片段、停止、退出、本机状态恢复和一次断网重连。额外验证发送期间从 Windows 主动解除静音、切换/移除输出设备、崩溃后重启恢复；退出不能覆盖用户的新选择。未发现设备、局域网不通或缺少听音确认时保留具体诊断，实机项继续待验证。
- [ ] **Step 4: 定位真实失败。** 使用 systematic-debugging，先复现再修改共享根因；保留认证验证，不因无声而直接跳过证明、改密钥算法或永久切换时钟。PTP 为已有默认；仅在设备能力和测试证据支持时比较 NTP。要求密码、PIN 配对、仅家庭成员访问分别记录，未验证的模式不能显示为已支持。
- [ ] **Step 5: 记录视频与型号边界。** 对视频使用画面/点击标记和外部录制或用户观察，记录方法、偏移和漂移；不默认启用 PC 麦克风。Windows 11、第 1/2 代和双机仅在现有测试环境具备时验收；没有设备时明确待验证，不安装虚拟化平台来制造测试条件。提交 `test: document native HomePod validation and compatibility`，不把首轮 mini 成功标成全项目完成。

## Task 6: 打包可运行测试版并完成回归

**Files:** Modify `README.md`、`desktop/AirFlash.App/AirFlash.App.csproj` 的产品说明元数据、必要的 XAML/本地化；Use 现有 `scripts/dotnet.ps1`、`build-native.ps1` 和兼容审核；更新 `UPSTREAM.md`、`docs/COMPATIBILITY.md`、`docs/VALIDATION.md`。便携二进制和报告位于忽略目录。

**Interfaces:** Consumes 经过验证的原生应用；Produces `dist/AppleHome.exe`、许可证/对应源码说明、可复制构建启动命令，以及与该二进制 SHA256 对应的验证报告。内部程序集仍为 AirFlash，单文件发布结果复制为 AppleHome.exe；不增加重命名框架或新打包脚本。

- [ ] **Step 1: 核对产品流程。** 主面板有设备、源、开始/停止、音量、文字状态和错误；将发送状态从容易误读的“Playing”改为“Sending audio / 正在发送音频”，缓冲值明确写目标值。复用 WPF 现有焦点/键盘模式，验证取消、隐藏托盘、二次启动和退出。文案/样式不另写镜像测试，使用现有 UI smoke 和必要的人工检查。
- [ ] **Step 2: 跑最终自动化。** 逐条运行 Task 1 的 .NET/Rust/Python 检查，另运行 `pwsh -NoProfile -File scripts/dotnet.ps1 run --project AirFlash.App -- --ui-smoke --output ../artifacts/validation/ui-smoke.json`。预期所有检查退出 0，UI 报告 `ok=true`。模拟 soak 如需运行只针对 localhost，和真实 HomePod 声学证据分开。
- [ ] **Step 3: 发布便携包。** 把当前 checkout 的绝对路径保存在 `$CheckoutRoot`，使用现有 .NET helper：`pwsh -NoProfile -File scripts/dotnet.ps1 publish AirFlash.App -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$CheckoutRoot=/_/" -p:Version=0.1.0 -p:InformationalVersion=0.1.0-rc.1 -o ../build/wpf-publish`。审核现有 bundle 报告 `ok=true` 后，把 `build/wpf-publish/AirFlash.exe` 复制至 `dist/AppleHome.exe` 并记录 SHA256。首轮不运行会预留发布版本、清理 dist 并生成 MSI 的完整发布流水线；不递归删除用户目录。
- [ ] **Step 4: 验证最终二进制。** 将 `$Preview` 设为当前 checkout 下 `dist/AppleHome.exe` 的绝对路径，将 `$CompatReport` 设为 `artifacts/validation/package-compat.json` 的绝对路径。运行 `$PreviewCheck = Start-Process -FilePath $Preview -ArgumentList @('--compat-check','--output',('"' + $CompatReport + '"')) -WindowStyle Hidden -Wait -PassThru`，确认 `$PreviewCheck.ExitCode` 为 0、报告可读、引擎 hello 正确。还需在最终便携 WPF 包上重复 mini 的开始/停止出声流程；只重新测试独立 target 目录引擎不能证明打包结果。自包含启动不等于所有型号/Windows 版本认证，严格沿用矩阵。
- [ ] **Step 5: 完成交付记录与提交。** README 写清已初始化 HomePod、同网访问、音频源、开始/停止、视频同步限制、要求密码边界和恢复方法；列出检查命令、实际结果及未验证项。保留 GPLv3+ 与上游归属、给出对应源码位置。`git diff --check` 通过，确认没有凭据、音频、运行时、原始诊断或外部发布配置进入提交，再提交 `feat: package AppleHome native preview`。

## 计划自查与执行交接

规格 1–3 对应 Tasks 1/5/6；4–7 对应 Tasks 2/3/5；8 对应 Task 4；9 对应 Tasks 5/6；10 对应 Tasks 2/3/4/6；11–12 对应 Tasks 1/5/6。Review Focus 五类条件均有指定测试或实机记录。没有依据改写采样协议、删除 Windows 10 或缩掉全型号目标；缺硬件的认证仍保持待验证。

推荐 **Native**：由本会话依次执行这六个相互依赖的任务，最后安排一次独立整体审查。这样便于在同一条音频链路中定位失败，避免多个实现者同时改 SessionController 和状态恢复。另一选择为 **Subagent-driven**：每个任务交给独立实现者与审查者，成本更高、逐任务独立审查更充分。

用户审阅本计划并选择执行方式后，才进入相应 execution skill 和产品实现。计划审阅前不复制产品源码至工作区、不安装 SDK/依赖、不启动音频发送。

工具依据：[Microsoft 官方 dotnet-install 说明](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)、[uv 官方安装说明](https://docs.astral.sh/uv/getting-started/installation/)、[NAudio 2.2.1 NotificationGuid](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/CoreAudioApi/AudioEndpointVolume.cs)、[EventContext](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/CoreAudioApi/AudioVolumeNotificationData.cs)。
