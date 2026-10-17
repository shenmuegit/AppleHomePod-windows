# Windows 原生 HomePod 输出 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让本机 Windows 10 的用户只选择 `HomePod – da` 输出，并使用 Windows 音量、静音与应用混音器，自动将声音送到当前单只 HomePod mini。

**Architecture:** 独立安装原厂 VB-CABLE；AppleHome 绑定其真实播放端点，以当前用户的无窗口后台运行。复用 NAudio、现有 Rust 发送器、JSONL v1 和 SessionController；Rust 常驻准备虚拟端点采集，按 Windows 路由需求建立网络会话。实际驱动身份、命名持久性和音量取样校准通过后，才启用登录后台。

**Tech Stack:** Windows x64、.NET 10、NAudio 2.2.1、Windows Core Audio / Task Scheduler、现有 Rust MSVC / WASAPI / NTP / ALAC；不增加运行时依赖。

**Spec:** [Windows 原生 HomePod 输出设备设计规格](../specs/2026-10-14-windows-native-homepod-output-design.md)。状态：2026-10-16 驱动重启已核验，用户已完成 Windows 系统命名，PCM 正向测量通过、参考音待用户确认并保存；名称是在这次重启后修改，跨重启持久性仍待验证。Tasks 1、3–6 软件、整体代码审阅与候选包已完成，后台启用与资格结论仍受 Task 2 门槛约束。保留此前 Native 执行方式：主代理逐项实现，最后由独立审阅者检查整体。

## Global Constraints

- 首轮仍使用现有 Windows 10 与单只 mini 27.0，断网测试暂缓。
- 当前 mini 继续使用已实测的 NTP / ALAC / 44100 Hz 与 2000 ms 目标配置。
- 日常不显示窗口或托盘控制菜单；Windows 原生控件是唯一日常音量入口。
- 独立 VB-CABLE Pack45，应用不依赖已安装的 Voicemeeter，也不更改它的设备或路由。
- 驱动独立原厂安装，不嵌入或自动运行于 AppleHome 安装流程；管理员安装及重启遵循原厂要求。
- 新模式不设置物理播放设备静音；只读取明确绑定的虚拟播放端点，不开放物理麦克风，不录制用户音乐。
- 认证证明或接收端身份验证失败必须终止；保留原有取消、IPC 长度、DPAPI、日志脱敏与有界重试。
- Windows 11、其他 HomePod 型号/固件及立体声对继续保留待验收状态；网络缓冲仍存在，不能承诺零延迟。
- 不改变防火墙、驱动签名策略或安全启动，不购买证书、不联系厂商、不对外发布。

## Review Focus

| 条件 | 应有结果 | 负责测试 |
| --- | --- | --- |
| 已有禁用/未连接的 VB-CABLE，或同包的 16 通道端点 | 不占用、不猜测绑定、不改名 | Task 1 `PreexistingAndSixteenChannelEndpointsAreRejected` |
| 握手期间的短音、缓冲溢出及切换后的旧回调 | 就绪后保留首段；溢出明确失败；旧代际不能重放 | Task 3 `ArmedPrerollPreservesFirstFrames` / `PrerollOverflowIsExplicit`；Task 5 `OldGenerationCannotReopenRoute` |
| 取消读取时 stdout 只到半行，随后新会话复用进程 | 单一读泵保持行边界；取消只影响等待者 | Task 4 `LeaseCancellationDoesNotCancelHalfLinePump` |
| 接收端初值 0、连接中多次改 Windows 音量、后台自身回调 | 最新用户操作生效，0 不被擅自抬高，不产生回调环或重复衰减 | Task 2 `GainIsAppliedOnce`；Task 5 `LatestUserRevisionWinsInitialRemoteZero` |
| 应用留在旧端点、后台自己的采集会话、用户手动改名/改任务 | 真正的外部播放需求有效；自己的会话不保活；清理保留用户修改 | Task 5 `OwnCaptureNeverKeepsRouteAlive`；Task 1/6 所有权测试 |

## 工作区、文件与共用约定

在已附属工作树 `C:\Users\Administrator\.codex\worktrees\applehome-native\applehome` 执行；起点 `a6a188a`，原始 `G:\code\applehome` 保持原状。先核对本计划提交及 `git status --short`。所有相对路径以该工作树为根；已有可听音旧包保留。

每个构建/测试 PowerShell 进程先运行 `. '.\.runtime\dev-env.ps1'`。下文简写 D：`pwsh -NoProfile -File scripts/dotnet.ps1`；R：`cargo test --manifest-path native/airflash-engine/Cargo.toml`。执行时展开简写；测试报告进入忽略的 `artifacts/validation/native-output/`。不运行会清空 `dist/` 的旧 `scripts/build.ps1`。

| 文件 | 单一职责 |
| --- | --- |
| 新 `desktop/AirFlash.Core/NativeBinding.cs`、`NativeBindingStore.cs` | 端点/驱动绑定、配置所有权与原子持久化 |
| 新 `desktop/AirFlash.Core/PcmVolumeCalibration.cs` | 校准判定与唯一发送增益计算 |
| 新 `desktop/AirFlash.App/Services/WindowsNativeBinding.cs`、`Verification/NativeOutputCheck.cs` | PnP/拓扑核验、系统命名核验、低峰值校准和只读诊断 |
| 改 `native/airflash-engine/src/live.rs`、`session.rs`、`main.rs` | 常驻采集、有界暂存与增量 JSONL 命令 |
| 新 `desktop/AirFlash.Core/NativeEngineHost.cs`；改 `EngineConnection.cs`、`SessionController.cs` | 单进程、单 stdout 读泵和网络会话租约；原生增益覆盖 |
| 新 `desktop/AirFlash.Core/NativeOutputController.cs`、`desktop/AirFlash.App/Services/WindowsNativeOutput.cs` | 路由/会话需求、Windows 音量与代际；MTA COM 生命周期 |
| 新 `desktop/AirFlash.App/Services/NativeOutputAgent.cs`、`NativeAutostart.cs`、`NativeNotification.cs`、`ShellNotification.cs`；改 `Program.cs`、`SingleInstance.cs`、`TrayService.cs` | 无窗口入口、当前用户启动任务、错误通知和有界退出 |
| 新/改同名 `desktop/AirFlash.Tests/*Tests.cs`、Rust tests、既有 Verification | 纯逻辑回归、管道契约与实际 Windows 检查；不以模拟测试替代听音 |
| 新 `docs/validation/windows-native-homepod-output.md`；改 README / NOTICE | 实机门槛、可复制安装/撤销步骤、产物溯源 |

以下跨任务类型统一放在 Core，平台 COM 对象不得进入它们：

```csharp
record EndpointIdentity(string EndpointId, string AdapterInstanceId, string HardwareId,
    string Provider, string DriverVersion, string DriverSha256, string PinName,
    string FriendlyName, string MixFormat, int Channels, AudioEndpointKind Kind, bool Active, string? EndpointLabel = null);
record PcmVolumeCalibration(bool MasterInPcm, string MixFormat, string DriverSha256,
    double MaxDb, int ResumeDeviceVolume);
record NativeBinding(int SchemaVersion, string ReceiverId, EndpointIdentity Endpoint,
    string OriginalName, string AssignedName, PcmVolumeCalibration? Calibration);
record VolumeSample(double EndpointDb, double Scalar, bool Muted, double Rms, double Peak);
record NativeVolume(long Revision, double Db, double Scalar, bool Muted, Guid Context);
record NativeAudioSession(string EndpointId, string InstanceId, int ProcessId, bool Active,
    bool ObservedActive = false);
record NativeOutputSnapshot(long Generation, IReadOnlyList<string?> DefaultRenderIds,
    IReadOnlyList<NativeAudioSession> Sessions, NativeVolume Volume, bool BindingValid);
```

类型所属任务固定：Task 1 在 NativeBinding.cs 定义 EndpointIdentity / NativeBinding，并创建 PcmVolumeCalibration.cs 中的校准记录供持久化使用；Task 2 在后者添加 VolumeSample / NativeVolume / PcmVolumeMath；Task 5 在 NativeOutputController.cs 定义其余快照/会话记录。不能让前一任务依赖后续才定义的类型。

记录值需在构造边界验证；绑定 schema=1，未校准绑定不能激活。`DefaultRenderIds` 顺序固定 Console / Multimedia / Communications，允许单个 null。`Generation` 标识设备/采集代际；`Revision` 仅用户音量操作推进。使用单调计时，不以系统日历变化判断超时。

执行裁定：ObservedActive 保存 50 ms 合并窗口内已经结束的活动边缘，新外部会话创建也获得同样的 30 秒保持；own PID/采集实例仍排除。Host 另提供幂等 `ResetCaptureAsync(CancellationToken)`，用于 begin 后尚未建网络租约时需求撤销。原生路径不发送旧 UI 的均衡器更新，保持 neutral EQ；旧 UI 路径回归不变。实际通知/声音/音量场景仍由 Tasks 2/7 验收，软件模拟不替代它们。

## Task 1: 建立可回滚的端点绑定

**Files:** Create 上表 `NativeBinding.cs` / `NativeBindingStore.cs` / `PcmVolumeCalibration.cs`（仅持久化记录）/ `WindowsNativeBinding.cs` / `NativeOutputCheck.cs`；Modify `Program.cs`、`DiagnosticText.cs`；Test 新 `NativeBindingTests.cs`、既有 `DiagnosticTextTests.cs`。`Program` 只增加显式诊断/维护分派，正常入口待 Task 6。

**Interfaces:** Produces `NativeBindingPolicy.CanClaim(EndpointIdentity, IReadOnlySet<string>): bool`、`PrepareBinding(EndpointIdentity candidate, string receiverId, string requestedName, NativeBinding? previous, IReadOnlySet<string> preexistingAdapterIds): NativeBinding`、`ValidateLive(NativeBinding, EndpointIdentity): string?`；`NativeBindingStore(string path).Load(): NativeBinding?` / `SaveAsync(NativeBinding, CancellationToken): Task`。平台 `WindowsNativeBinding.SnapshotAsync(CancellationToken): Task<IReadOnlyList<EndpointIdentity>>`、`BindAsync(string receiverId, string endpointId, string assignedName, CancellationToken): Task<NativeBinding>`、`RestoreAsync(NativeBinding, CancellationToken): Task`。Restore 仅校验绑定并保留用户系统名称；预安装快照保存在同一用户数据目录，不接受命令行自报驱动身份替代实时核验。

- [x] **Step 1 — RED:** 添加以下行为测试；候选的 Provider=`VB-Audio Software`、HardwareId=`VBAudioVACWDM`、DriverVersion=`3.3.1.7`、SYS 哈希使用 Task 2 固定值，其余字段填本测试的明确值。

```csharp
// PreexistingAndSixteenChannelEndpointsAreRejected
Assert.False(NativeBindingPolicy.CanClaim(candidate, new HashSet<string>{candidate.AdapterInstanceId}));
Assert.False(NativeBindingPolicy.CanClaim(candidate with { Channels = 16 }, new HashSet<string>()));
Assert.False(NativeBindingPolicy.CanClaim(candidate with { Kind = AudioEndpointKind.Recording }, new HashSet<string>()));
// UserRenameIsNotUndone: PrepareBinding adopts the actual Windows user label and keeps OriginalName.
Assert.Equal("我的输出", adopted.AssignedName);
Assert.Equal(binding.OriginalName, adopted.OriginalName);
// DriverOrMixChangeInvalidatesCalibration
Assert.NotNull(NativeBindingPolicy.ValidateLive(binding, candidate with { DriverSha256 = new string('0',64) }));
```

- [x] **Step 2 — Run RED:** `D test AirFlash.Tests --filter NativeBindingTests`。预期新类型尚未定义而失败；随后补充持久化取消/损坏 JSON/缺失字段不能产出可激活绑定的测试，以行为失败收敛。
- [x] **Step 3 — 实现最小边界：** 按现有 SettingsStore 原子替换方式持久化 `%APPDATA%\AppleHome\native-output.json`。枚举 Core Audio 所有端点状态，并用拓扑连接与 SetupAPI/ConfigManager 对应适配器、HardwareId、Provider、版本和加载 SYS 文件哈希；原始 pin/interface 描述与通道数共同排除 `CABLE In16Ch`，不能只靠 FriendlyName。安装前还查已有非 present 的同 HardwareId 适配器；存在未归本应用管理的实例即停止。本机默认输出和其他设备只读。
- [x] **Step 4 — 维护与恢复：** 管理员属性写入在实机返回拒绝后，删除该写入/还原路径；首次用户在 Windows 设备属性命名，应用读取并以 PrepareBinding 核验、原子保存。卸载保留用户名称，清除自己的绑定，Task 6 补启动任务撤销。不直接写 MMDevices 注册表或用未文档化 PolicyConfig。`--native-preflight-check --output <json>` 只读 Windows 状态；`--native-bind --receiver-id <id> --endpoint-id <id>` 要求安装前快照和已完成系统命名。维护失败返回 JSON/非零，无 MessageBox；导出隐藏端点标签与身份，不导出凭据。
- [x] **Step 5 — GREEN / Commit:** 重跑上述测试及 `DiagnosticTextTests`，预期全部通过；只读检查输出完整设备事实且没有写入默认输出、静音或启动项。提交 `feat: add owned virtual endpoint binding and rollback`。

## Task 2: 独立驱动安装、命名持久性与音量校准门槛

**Files:** Modify `PcmVolumeCalibration.cs` / `NativeOutputCheck.cs` / `Program.cs` / `scripts/test-native-output.ps1`；Create `desktop/AirFlash.App/Verification/NativeVolumeCalibration.cs`；Test 新 `PcmVolumeCalibrationTests.cs`；更新 `docs/validation/windows-native-homepod-output.md`，记录本任务真实安装/重启/校准结果。WindowsNativeBinding 接口复用，不为本任务改写驱动逻辑。

**Interfaces:** Consumes Task 1 binding/store；Produces `PcmVolumeMath.Classify(IReadOnlyList<VolumeSample>, string mixFormat, string driverHash, int resumeDeviceVolume): PcmVolumeCalibration`、`Gain(PcmVolumeCalibration, NativeVolume): double`；诊断 `--native-calibrate --output <json>`，首次仅产出候选。用户确认实际参考音可听后，用 `--confirm-reference-volume <实际回读值>` 重新核验，值一致才原子更新 binding.Calibration。校准结果只能来自实际绑定端点，不接受用户输入 `MasterInPcm`。

- [x] **Step 1 — RED:** `GainIsAppliedOnce` 覆盖已含主增益、未含主增益、静音、0 和非有限值；`UnstableCapturePointRejectsCalibration` 覆盖两种模型都不吻合的 RMS 比例与无非零参考音量。

```csharp
Assert.Equal(1d, PcmVolumeMath.Gain(calibration with { MasterInPcm = true, MaxDb = 0 }, new(1,-12,.25,false,Guid.Empty)));
Assert.Equal(Math.Pow(10,-12d/20), PcmVolumeMath.Gain(calibration with { MasterInPcm = false, MaxDb = 0 }, new(1,-12,.25,false,Guid.Empty)), 6);
Assert.Equal(0d, PcmVolumeMath.Gain(calibration, new(2,-12,.25,true,Guid.Empty)));
Assert.Equal(0d, PcmVolumeMath.Gain(calibration, new(3,-96,0,false,Guid.Empty)));
Assert.Throws<ArgumentException>(() => PcmVolumeMath.Classify(samples, "44100/2/float", driverHash, 0));
```

- [x] **Step 2 — Run RED:** `D test AirFlash.Tests --filter PcmVolumeCalibrationTests`，预期缺少实现或计算结果不符而失败。
- [ ] **Step 3 — 实现校准：** 用已有 NAudio 在绑定 render 端点发送峰值 ≤0.05 的短已知信号，只计算内存 RMS/peak。固定测试会话音量，采集最大 dB 与最大值−12 dB 的至少两组重复数据；已包含模型要求变化 −12±1 dB，未包含模型要求变化 0±0.5 dB，否则拒绝。若端点不支持两档则停止复核。再改变本测试会话音量至 0.5，要求其 PCM 振幅比 0.5±0.05；分别记录 mute/0、Scalar、Db、MixFormat、驱动哈希。沿用既有有界 NTP/ALAC probe 和实际远端音量回读确认参考 B（1–100），遵守 probe ≤5 秒 / WAV 输入峰值≤0.05 / WAV gain≤1，实际校准使用峰值0.04、gain1；finite loopback probe 的 gain≤0.1 保持不变。需要用户听音确认，不能复制过往日志当本次 B。初值为 0 时先取得用户明确操作形成的可听参考，不能猜测。恢复且仅恢复本测试仍拥有的端点/测试会话设置，不自动设远端为 100，不录音落盘。
- [x] **Step 4 — GREEN:** 重跑 Task 2 测试，预期通过；诊断无有效 binding 时须退出非零且不选择物理端点。此时未通过下述实机门槛不得启用后台或声称原生音量可用。
- [x] **Step 5 — 独立安装门槛：** 先出示具体包与改动范围，保存 Task 1 安装前快照。用户单独运行官方 `VBCABLE_Setup_x64.exe` 并按原厂要求重启；AppleHome 不自动启动/嵌入该安装器。包 SHA256=`b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb`；x64 Win10 SYS SHA256=`f01344602472f1b527de5ee98f18987c03db48fea444e457d061e937f1d531d5`。复核原厂 setup Authenticode、Microsoft 目录签名及 `/kp /c` SYS 校验；不以已下载或已验签冒称安装成功。2026-10-16 已核验实际启动时间、端点和文件哈希一致，驱动重启门槛通过。
- [ ] **Step 6 — 实机门槛 / Commit:** 已唯一识别新普通 CABLE Input；因管理员属性写入实测被拒绝，按规格更新改用一次 Windows 系统属性命名为 `HomePod – da`，应用只读取和绑定，保留用户名称。真实 pin 改从连接的适配器 Part.Name 读取，新增可变 EndpointLabel（可选字段兼容旧绑定），不以 DeviceDesc 或通道数猜身份。命名后按原厂重启，用 Windows 输出选择和 fresh read 验证持久性；执行校准并取得有效结果/B，核对其他端点与默认选择。未通过仍停在此处。相关软件修正与测试先保存，硬件门槛通过后才提交资格结论。

2026-10-16 执行裁定：已核验驱动重启与当下系统名称，可以先独立执行有界 PCM/参考音校准；它不安装后台或改默认输出。跨重启名称持久性继续作为启用后台之前的门槛，不把今天重启后才设置的名称记作已通过。实测旧 100 ms 排空等待混入上一档 PCM，改为 300 ms 后在真实候选通过取样报告回归；校准保存仍等待听音确认。Task 2 不标完成，Task 7/8 的实际资格不提前。

2026-10-17 执行裁定：用户明确要求放大测试声音；提高 HomePod 参考音量到72后的守候测试仍无声，独立参考发送器以五秒、5%峰值信号出声。当前应用把已限幅至4%的 WAV 再乘0.1，实际仅0.4%。将 WAV probe 的最大增益改为1并保留输入5%峰值、S16 stereo、固定采样率及五秒边界，因此实际输出仍不超过5%；应用的参考音采用gain1、实际4%。实采 finite loopback 的0.1增益上限不变，未改时钟、认证或媒体协议。该变化先做回归和新候选听音，不能仅因参考引擎出声就认定产品已通过。

## Task 3: Rust 常驻准备采集与有界首段暂存

**Files:** Modify `native/airflash-engine/src/live.rs`、`session.rs`、`main.rs`；Test live.rs 单元测试及新 `tests/armed_capture.rs`；Modify `native/airflash-engine/README.md`。不另造第二个发送器或 PCM 跨进程协议。

**Interfaces:** 增量 JSONL v1：`arm_capture {capture_id,endpoint_id,sample_rate:44100}` → `capture_ready {capture_id,epoch,session_instance_id}`；`begin_capture {capture_id,epoch}` → `capture_begun`，在发现/DNS/RTSP 前冻结首段；`reset_capture {capture_id,epoch}` → `capture_reset {capture_id,epoch:新值}`（请求 epoch 必须等于当前值，成功后 checked +1）；`disarm_capture {capture_id}` → `capture_disarmed`；`set_capture_gate {capture_id,epoch,open,clear_pending}` → `capture_gate`。`start` 可选 `capture_id` / `capture_epoch` / `await_volume_gate`，全部缺省时沿用旧路径；字段组合不完整、身份不匹配均拒绝。`epoch` 为 u64，arm 初值 1。所有事件保留 version/id/session_id；gate 关闭仍允许 streaming/volume 事件和时钟控制。

- [x] **Step 1 — RED:** 新测试先写再跑；保留旧 queue 60 ms / target 20 ms 行为。

```rust
// ArmedPrerollPreservesFirstFrames: 模拟 100 ms 短音及 800 ms 握手，gate 开启前不得 pop
assert_eq!(first_sent_frame_marker, first_captured_frame_marker);
assert_eq!(sent_short_tone_frames, 4_410);
// PrerollOverflowIsExplicit: 44100 Hz 新模式容量 88_200 帧
assert_eq!(overflow_error_code, "capture_preroll_overflow");
// ResetDropsOldEpoch / OldCaptureKeyRejected
assert_eq!(frames_after_reset, 0);
assert!(start_with_old_epoch.is_err());
// OpeningGateRebasesSchedule / ResolvedMuteCanLastFifteenSeconds
assert_eq!(scheduler_drops_on_first_send, 0);
assert_eq!(resolved_mute_overflows, 0);
```

- [x] **Step 2 — Run RED:** `R --test armed_capture`，预期命令/新状态未实现，测试失败；测试命名、时钟和模拟帧使用真实新增队列状态，不用写死预期的假引擎代替它。
- [x] **Step 3 — 实现采集生命周期：** 扩展现有 `live::Loopback`，新增 `arm(endpoint: String, rate: u32, equalizer: Control, capture_id: String) -> Result<Self>`、`begin(epoch: u64) -> Result<()>`、`reset(epoch: u64) -> Result<()>`、`set_gate(epoch: u64, open: bool, clear_pending: bool) -> Result<()>`，保留旧 `start`。主进程持有唯一 Arc 采集器；初始化取得真实 AudioSessionControl2 的不透明 session instance id，使用独立会话 GUID。只允许已指明的 render，保留录音端点拒绝。EOF/退出释放采集线程和 COM。
- [x] **Step 4 — 实现暂存和接入：** ArmedIdle 只留最近 100 ms 的滚动帧；新需求 begin 后冻结起点并允许最多 2000 ms / 88,200 帧，重复 begin/角色通知不得重置。idle 旧路由在停止/重连边界 reset；从 route 停止到下一次 begin 的帧属于新 epoch。Buffering/gate 关闭不能消费为零后丢掉首段，不能使用旧 discard_stale 丢首段；超限返回上述错误并结束该网络会话。状态已解析为正常静音时清旧暂存、开 gate 以 gain=0 正常消费，不能把 15 秒静音当成初始化溢出。新增使用已准备 Arc 的 session 入口，旧 probe API 不改。gate 初次打开时以当前时钟重建媒体调度与 RTP 时间锚点，避免初始化等待触发 late-recovery；实际 first_send 指标只能在真发送后填值。暂存不在开始发送后立刻缩回 60 ms；正常速率下的额外启动延迟不会自动消失，需记录并实测。网络开始后旧 late-recovery 如需丢帧必须记录，首段验收失败不得冒称完成。
- [x] **Step 5 — GREEN / Commit:** `R --test armed_capture`、`R`、`cargo clippy --manifest-path native/airflash-engine/Cargo.toml --all-targets -- -D warnings`。预期全部通过/仅原有有理由 ignored 测试；补充错误 key、并发 arm、EOF 释放及旧 5 秒 probe / 0.05 峰值 / 0.1 gain 上限回归。提交 `feat: arm virtual capture before HomePod handshakes`。

## Task 4: 单一 stdout 读泵与可复用网络会话

**Files:** Create `NativeEngineHost.cs`；Modify `EngineConnection.cs`、`SessionController.cs`；Test 新 `NativeEngineHostTests.cs`、既有 `EngineJsonLineTests.cs` / `SessionTests.cs`。不改旧 IEngineConnection / IEngineFactory 签名，不要求重写所有旧 fake。

**Interfaces:** `ProcessEngineConnection.ProcessId: int` 只读。`NativeEngineHost(IEngineConnection connection, int processId): IEngineFactory, IAsyncDisposable`；`ArmAsync(NativeBinding, CancellationToken): Task`、`BeginCaptureAsync(CancellationToken): Task`、`SetCaptureGateAsync(bool open, bool clearPending, CancellationToken): Task`、`Open(): IEngineConnection`、`CaptureSessionInstanceId: string?` / `ProcessId: int` / `Completion: Task`。`SessionController.SetNativeGainAsync(double gain): Task`，默认 null 覆盖则保持旧行为，非法/非有限值拒绝。Host.Open 返回租约，租约 Dispose 关闭网络会话，Host.Dispose 才结束进程/采集；致命 Host 故障通过 Completion 传播给 Agent。

- [x] **Step 1 — RED:** 使用可控制分段的 TextReader 与现有 fake connection 模式增加以下测试；不用真实设备。

```csharp
// LeaseCancellationDoesNotCancelHalfLinePump
Assert.Equal("streaming", resumedEvent.Text("event"));
Assert.Equal(secondSessionId, resumedEvent.Text("session_id"));
// LeaseDisposeKeepsArmedHost / GateOpensAfterLatestGainWrite
Assert.False(processConnectionDisposed);
Assert.True(stopAcknowledgedBeforeNextStart);
Assert.True(lastGainWriteIndex < openGateWriteIndex);
// FatalHostRejectsNewLeases
Assert.Throws<IOException>(() => fatalHost.Open());
// InvalidGainRejected / ConnectingGainSurvivesStart
await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetNativeGainAsync(double.NaN));
Assert.Equal(expectedNativeGain, nextStartParameters.Number("gain"));
```

- [x] **Step 2 — Run RED:** `D test AirFlash.Tests --filter "NativeEngineHostTests|EngineJsonLineTests|SessionTests"`。预期新行为缺失而失败。
- [x] **Step 3 — 实现 Host：** 一生只开一个 stdout pump，调用原 65,536 字符有界读取；租约取消只取消自己的 Channel 等待，绝不取消基础 reader。按 session_id 分发，发送首条命令前登记通道；每租约通道容量 64，溢出/坏 JSON/EOF 明确 fault 所有租约及 Completion 并关闭本 Host，不能无界增长或在死 Host 上重建租约。控制请求用独立 session_id，沿用捕获初始化 3 秒及命令 ack 2 秒上限；hello 临时租约不结束 Host。首次需求先显式 BeginCaptureAsync；健康 Host 的网络重试若已 reset，租约 SendAsync(start) 先 await begin，幂等且不二次清首段，再补充 Task 3 capture key/gate，拒绝其他端点/采样率。stop ack 最多等 2 秒，已有 stop 不重复发送；确认停止后 reset 到新 epoch，失败关闭自有进程，不能让旧流和新流共享源。
- [x] **Step 4 — 实现原生增益：** SetNativeGainAsync 校验、保留最新值且按已有发送串行边界送出；Gain 选择原生覆盖或原有设置，禁止双乘。连接期间也保留更新，在开 capture gate 前 await 最新 gain 写入同一 stdin。旧 EQ、旧静音和现有 UI 的默认行为回归不变；新路径由 Task 5 使用 neutral EQ / MuteWhileStreaming=false，不改旧控制面板。
- [x] **Step 5 — GREEN / Commit:** 重跑本任务测试，增加迟到旧 session_id、64 条溢出、半行 EOF、超长行、disposal 与 reconnect 回归；预期通过且正常释放后下一次 lease 可正确开始，fatal 后拒绝 lease 并传播退出信号。提交 `feat: reuse armed engine with isolated stream leases`。

## Task 5: Windows 路由需求、原生音量和静音恢复

**Files:** Create `NativeOutputController.cs` / `WindowsNativeOutput.cs`；Test 新 `NativeOutputControllerTests.cs`；Modify `NativeOutputCheck.cs` / `DeviceVolumeTests.cs`，仅在新路径组合现有 SessionController。

**Interfaces:** Core 定义 `INativeOutputMonitor : IAudioService, IAsyncDisposable`，包含 `Snapshot: NativeOutputSnapshot`、`event Action<NativeOutputSnapshot>? Changed`、`SetMutedAsync(bool muted, Guid context, CancellationToken): Task`。`NativeOutputController(NativeBinding, INativeOutputMonitor, NativeEngineHost, SessionController, TimeProvider? timeProvider = null, Action<string>? log = null)`、`RunAsync(Func<string, Receiver?> resolveReceiver, CancellationToken): Task`。平台 `WindowsNativeOutput(NativeBinding): INativeOutputMonitor`。跨任务类型使用上表；有新增事件/接口须同步 fake 与调用者。

- [x] **Step 1 — RED:** `AnyRenderRoleOrExternalSessionStartsRoute` 覆盖三角色/null/单独应用；`OwnCaptureNeverKeepsRouteAlive` 按当前代理 PID、实际 owned engine PID、采集实例 ID 排除；`OldGenerationCannotReopenRoute` 覆盖 stop 后迟到事件；音量测试包含下述断言。

```csharp
// LatestUserRevisionWinsInitialRemoteZero
Assert.Equal(latestUserRevision, appliedRevision);
Assert.Equal(calibratedResumeDeviceVolume, requestedRemoteRestore);
Assert.False(captureGateOpenedBeforeRemoteReadback);
// InitialRemoteZeroWithoutNewIntent / NoPhysicalMuteEver
Assert.Empty(unrequestedRemoteVolumeWrites);
Assert.True(virtualEndpointMutedWithOwnContext);
Assert.Equal(0, physicalMuteCalls);
// MutedPrerollIsNotReplayed / PauseForFifteenSecondsResumes
Assert.DoesNotContain(preMuteMarker, framesAfterUnmute);
Assert.True(homePodRouteActiveAfterResume);
```

- [x] **Step 2 — Run RED:** `D test AirFlash.Tests --filter "NativeOutputControllerTests|DeviceVolumeTests"`，预期新协调器/行为缺失而失败。测试使用可控 TimeProvider，不 sleep 15 秒模拟逻辑。
- [x] **Step 3 — MTA 监听：** 沿 AudioService 的专用 MTA 队列模式，保有 endpoint/session manager；先订阅 OnSessionCreated、准备实例持有列表，再访问 Sessions.Count 启用通知并枚举已有会话、注册状态/断开，最后发布初始快照。创建回调与初始枚举按 instance ID 去重，补 `SessionCreatedDuringInitialEnumerationIsRetained` 回归。COM 回调只排队，释放/网络操作均出回调。读取全部三角色、绑定端点上的活动外部会话及 Db/Scalar/Mute；50 ms 合并通知，但记录外部 active 短暂转换并启动 30 秒需求保持，不能合并掉短音。保留 session 实例对象到注销/Dispose。暂停留下端点的应用仍计实际需求，不谎称能区分手动单应用路由与应用暂未迁移。自己的三个身份不能算需求。依据：[NAudio 2.2.1 初始化源码](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/CoreAudioApi/AudioSessionManager.cs)、[微软 GetCount 通知要求](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionmanager2-registersessionnotification)。
- [x] **Step 4 — 路由协调：** 用单一容量 64 的事件队列/代际串行处理，满时明确失败而不静默丢用户意图；有效 binding 且任一默认角色选中或绑定端点有外部活动会话才运行。首次需求立即 BeginCaptureAsync，再查接收端/解析地址/StartAsync。默认选中保持就绪；仅单应用需求消失后 30 秒再停，覆盖至少 15 秒暂停。重复角色通知不重开；失效/删除立即取消，不改送物理端点。启动/端点或格式变化实时核验 Task 1/2，失配停止并要求维护。CoreAudio 的 IAudioService.MuteAsync 在此适配器中抛 invariant 错误、RestoreAsync 无 owned 状态直接完成，避免旧 journal 被日常路径调用。
- [x] **Step 5 — 音量协调：** 固定设置 endpoint、44100、2000、neutral EQ、MasterVolume=100、MuteWhileStreaming=false、StandbyEnabled=false、ForceReconnect=true、MaxReconnectAttempts=5；仅 Receiver.PrefersNtpAlac 为 true 的已验证 mini profile 可开始，固件/分组变化失配则停止待核验，不自动落入未验证 PTP；补 `UnsupportedReceiverProfileNeverStarts`。按 Task 2 Gain 唯一施加 Windows 主音量，应用混音器由 Windows 保留。volume own-context 不推进用户 Revision；connecting 更新排队。初始远端 R>0 保留 R；R=0 无更新用户意图则同步虚拟 mute、清旧暂存，设置 gain=0 后开 gate 正常消费，不在 15 秒静音期间持续 hold。用户后来正向 unmute 才暂关 gate、仅恢复运行时 LastNonZero 或校准 B，等待序列匹配/readback 后以最新 gain 开 gate；错误/无 B 不放行。post-master PCM 在 gate 关闭期间有音量变化必须清除旧档位帧，明确 mute 清除暂存，不能恢复后放出旧较大声音。Gate 初始关闭时仍处理设备音量/时钟事件。健康 Host 内仅网络失败由 SessionController 有界重连；Host fatal 传播给 Task 6 Agent，不在死实例上循环。
- [x] **Step 6 — GREEN / Commit:** 重跑本任务及 SessionTests / LocalMuteGuardTests，补绑定失效、设备名/IP变化但 ReceiverId 不变、时钟变化、自己的音量回调、晚到远端 sequence、暂停 15 秒和 30 秒闲置边界；预期通过。实际 Windows 诊断记录通知与场景；不把模拟通过当作可听静音。提交 `feat: follow Windows output and native volume without physical muting`。

## Task 6: 无窗口后台、当前用户登录启动与撤销

**Files:** Create `NativeOutputAgent.cs` / `NativeAutostart.cs` / `NativeNotification.cs` / `Services/ShellNotification.cs`；Modify `Program.cs`、`SingleInstance.cs` / `NativeOutputCheck.cs`，将 `TrayService.cs` 既有 Shell_NotifyIcon 结构/调用原样移到 ShellNotification 供错误通知共用；Test 新 `NativeStartupTests.cs`，按现有测试项目方式链接无 COM 的入口/所有权判定部分，回归现有 TrayRegistrationTests。

**Interfaces:** `NativeOutputAgent.RunAsync(CancellationToken): Task<int>`；`NativeAutostart.InstallAsync(string executable, CancellationToken): Task` / `RemoveOwnedAsync(string executable, CancellationToken): Task`；`NativeNotification.ShowAsync(string message, CancellationToken): Task`。入口：`--native-agent`、`--native-enable`、`--native-disable`、`--native-check --output <json>`；`--native-unbind` 扩充为先 disable 再 Task 1 restore。SingleInstance 新 `Listen(Action<byte> command, Func<bool>? isReady = null)` / `NotifyExistingAsync(byte command)` / `QueryReadyAsync(CancellationToken): Task<bool>`，保留旧 Action/无参重载；新模式独立 suffix=`Native`，byte 2 优雅停止，byte 1 不打开日常面板。native 管道使用 InOut，byte 3 查询 isReady 并回 0/1；旧 byte 1 客户端仍可只写。

- [x] **Step 1 — RED:** `AgentDispatchPrecedesWpfInitialization` 断言显式 `--native-agent` 和有效 binding 的无参数原生入口均不构造 App/ControlPanel/TrayService、外层 catch 失败非零且无 MessageBox；`UserChangedStartupTaskIsPreserved` 验证 SID、exe/args 不匹配不删除；`OwnedNativeTaskHasBoundedRestart` 验证当前 SID InteractiveToken、RunLevel LeastPrivilege、Restart Count=3 / Interval=PT1M、无密码。增加 `FirstEnableArmsWithoutRelogin`、`RepeatedEnableKeepsOneAgent`、`FatalHostReturnsNonzero`、`SingleInstanceCanDisposeAfterThreadSwitch`。

```csharp
Assert.False(wpfInitializedForNativeAgent);
Assert.Equal(0, normalWindowCount);
Assert.False(taskDeletedWhenExecutableChanged);
Assert.Equal(3, taskRestartCount);
Assert.Equal("PT1M", taskRestartInterval);
Assert.False(backgroundUsesPhysicalMuteRecovery);
```

- [x] **Step 2 — Run RED:** `D test AirFlash.Tests --filter NativeStartupTests`，预期新分派/任务逻辑缺失，失败。
- [x] **Step 3 — 入口与资源：** 在 new App() 前判定并分派原生模式；外层 catch 也根据已判定模式处理，所有原生入口失败日志/通知/非零退出，不能走旧 MessageBox。正常启动有有效 binding 则原生模式，无 binding 则一次性安装指引，不创建空面板；原面板仅 `--legacy-panel` / 显式 smoke/诊断。Agent 先 arm/等 capture_ready，再接受需求，记录就绪与冷启动空窗；复用 WindowsDiscovery/ReceiverCatalog，按 ReceiverId 订阅地址变化。健康 Host 的网络失败保持 Task 5 重试；Host.Completion fatal 时取消协调器、清理后返回 1，由计划任务最多重启 3 次，新 Agent 重新核验并 arm。取消/显式 stop 返回 0。SingleInstance 改为创建不持有线程所有权的 named Mutex，按 createdNew 判定并保留句柄，Dispose 仅关句柄、不 ReleaseMutex；保持旧身份和重载，验证异步跨线程退出。
- [x] **Step 4 — 系统通知：** 仅状态进入需处理错误时复用既有 Shell_NotifyIcon / AppIconService；独立错误通知在专用 STA 的隐藏 HwndSource/消息泵上短暂注册无菜单图标，显示后在 dismiss/hide/timeout 或最多 15 秒清理，健康状态无图标，不创建 App/资源字典/控制面板。沿用 TrayRegistration 所有权与有界失败日志，用户关闭通知/安静时间须尊重；API 失败留诊断，不改变系统设置。使用现有桌面框架，无新 NuGet 或 Toast 部署层。依据：[微软 Shell_NotifyIcon 行为与通知限制](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)。
- [x] **Step 5 — 启动与迁移：** 先请用户退出旧已停止的面板，核验仅旧应用仍拥有的 mute journal 并一次恢复。`--native-enable` 将已构建程序放入 `%LOCALAPPDATA%\AppleHome\app\AppleHome.exe`，使用系统 Task Scheduler COM 注册当前 SID 的 `AppleHome.NativeOutput` 登录任务，限权/无密码/允许电池供电/不因电池切换停止/无执行时限/IgnoreNew。绑定及校准有效后立即运行自有任务一次，等待 Agent 通过 CurrentUserOnly 管道报告 capture_ready 才返回成功，无需再次登录；重入不覆盖运行中程序、不创建第二实例。管道 byte 3 查询 ready，响应仅 0/1，不包含设备身份，沿用 3 秒连接界限及 5 秒就绪等待。HKCU Run 仅移除与已知旧路径/参数完全匹配的 AppleHome 值。任务所有权比较 SID、路径和参数；用户修改保留。disable 先取消自有启动再通过管道优雅停止，避免自动重启；不按进程通用名称杀进程、不删除第三方驱动。
- [x] **Step 6 — 软件 GREEN / Commit:** 重跑启动测试、TrayRegistrationTests 和既有 single-instance/兼容 UI smoke；隔离数据根运行实际 `--native-check`，确认检查路径 0 日常窗口/0 tray 注册、无密码/无服务，并由系统内存任务定义验证 schema。未校准 enable 必须拒绝且不改绑定/安装文件/任务。人工登录、实际任务运行/权限、通知可见性与资源退出验收受 Task 2 门槛约束，转到 Task 7；不能把只读 schema/模拟 ready 当作实际启用成功。提交 `feat: run HomePod output as a per-user headless agent`。

## Task 7: 当前 mini 的原生操作与听音验收

**Files:** Modify `NativeOutputCheck.cs`、`docs/validation/windows-native-homepod-output.md`；只在复现实际失败后按所属 Task 补小回归再修复代码，不扩展范围。

**Interfaces:** Consumes Tasks 1–6；Produces 分场景 JSON 统计及有用户反馈的验收记录，每项记录 build/hash、路由、开始/结束时间、实际观察时长、资源、错误和用户判断；无音频文件。`--native-check` 不自动长播或抓物理源。

Task 6 软件完成后仍待在本任务验证：实际启用无需再次登录、真实登录/退出与限权任务、错误通知可见性/关闭通知的日志行为以及资源释放。用户暂不能重启时可先生成和审核候选包，不启用；该包仍是未取得实机资格的候选。

- [ ] **Step 1 — 固定实际候选包：** 在 Task 6 代码提交上用既有 dotnet publish 自包含 win-x64 / 单文件 / 内嵌本次 Rust，输出 `artifacts/native-output-publish/`，复制为 `dist/native-output/AppleHome.exe` 并记录代码 commit、EXE/native hash；不运行会清空 dist 的 build.ps1。如开发后台在运行，先 disable 自有实例再更新到此候选包，Task 6 enable 后记录安装路径 SHA 与候选相等。驱动命名持久性、校准/B、认证均通过；用户暂停 iPhone，旧流退出。只在 Windows 选择 `HomePod – da`，用户明确反馈是否出声及物理扬声器有无先行副本，不能用发包替代听音。
- [ ] **Step 2 — 首段/切换：** 已 arm 且无网络会话时播放短已知音，验证首段完整；分别测首次认证和已缓存认证路径。快速切到扬声器再切回，记录可听尾音/启动延迟，确保不重放之前提示音。Windows 全部三角色和实际单应用输出场景分别检查；输出切换后仍留在虚拟端点的应用行为如实记录。
- [ ] **Step 3 — 主/应用音量与静音：** 通过 Windows 控件实测三个 dB 档、应用混音器、滑到 0、静音键、恢复；连接中快速改档后验证最新值。HomePod 原值 0 的场景核验保持 0→Windows 明确恢复→回读 B→可听，全程不升到 100。记录可听静音尾音；若仅 digital gain=0 不能满足实际静音体验，判未通过，凭证据另审 remote mute/flush，不能直接声称立即静音。
- [ ] **Step 4 — 暂停与持续播放：** 音乐暂停 ≥15 秒再恢复、静音 ≥15 秒再恢复。随后与用户约定真实 30 分钟连续音乐窗口，从用户确认播放后计时；每 60 秒仅记录自有 Agent/engine 的 CPU、RSS、handle、线程和协议状态，期间交流及时，结束请用户确认有无断续/异常。手动停止、用户不在场或观察不足必须记实际时长并保留待验收，不补写 30 分钟成功。
- [ ] **Step 5 — 退出/撤销与验收判断：** 切回物理输出，再 disable 自有任务，确认自有 engine、pipe、COM 资源释放，电脑声音正常。名称恢复先以所有权策略测试，实际解绑/重装留在用户要求的安装撤销验证窗口，不能为测试反复卸驱动。明确记录断网测试未做、其他硬件/Windows 11 未验收。全部必需实际项通过才提交 `test: qualify native Windows output on the current HomePod mini`；否则记录失败项而不标完成。

## Task 8: 保留基线、产物溯源与整体回归

**Files:** Modify README、`desktop/THIRD-PARTY-NOTICES.md`、`desktop/AirFlash.App/Assets/ThirdPartyNotices.txt`、上述 validation；忽略目录中新建 `dist/native-output/` 发布文件与 manifest/source archive，不改上游发布脚本或启动外部发布。

**Interfaces:** Produces `dist/native-output/AppleHome.exe`、GPL/NOTICE、源码归档、SHA256 与 manifest；安装只复制本应用并注册自有任务，VB-CABLE 仍作为独立第三方安装，保留原 `dist/AppleHome.exe` 基线。

- [ ] **Step 1 — 完整回归：** `D test AirFlash.sln`、`R`、上述 Clippy、`uv run pytest`、`uv run ruff check .`；使用 `scripts/build-native.ps1 -Check` 构建。预期退出码 0，ignored 项及实际计数写报告；校准/实机检查不能由这些测试取代。
- [ ] **Step 2 — 生成溯源：** 保留 Task 7 已验收的实际候选 EXE，不无理由重编/覆盖运行中文件；使用项目既有 version 属性，不另编未来版本。固定代码提交生成源码 archive 和二进制 hash；manifest 将 code_commit / documentation_commit 分开，禁止残留引擎冒称新代码。若 Task 7 修复了代码，提交并重建到新目录、更新安装后重跑受影响验收，不能沿用旧 hash 证据；仅文档变化不用重跑完整 30 分钟。
- [ ] **Step 3 — 检查实际包：** 对新 exe 运行现有 `--compat-check` / `--ui-smoke`（旧显式诊断仍须通过）与 `--native-check`；兼容静态审核对应实际 EXE SHA，并核对 Task 7 包/hash/安装 SHA。开发运行通过不能替代包运行，验收过且未变化的包不再重复长播。README 给独立原厂安装、一次绑定、Windows 原生选择、诊断/disable/unbind 和官方驱动卸载边界，附真实延迟、冷启动空窗和未验收矩阵。
- [ ] **Step 4 — 整体审阅 / Commit：** 保留此前 Native 方法，由独立审阅者只读审查整组差异、Task 7 证据与源码/包一致性；主代理处理有效发现并重跑受影响检查。`git diff --check` 与干净状态核对后提交文档/溯源；仅本地提交，不推送、不合并、不发布、不删除工作树。验收缺项明确列出，不能把计划勾选代替实际完成。

## 计划自审与执行交接

主代理已自行按 writing-plans 完成五项自审：规格 1–9 节均落到上述任务；各任务有验证结果；跨任务类型及命令补齐；五项 Review Focus 均有所属测试；计划以签名/断言/门槛为主，未抄写实现函数体。自审修正了类型定义先后、初始化 gate 的媒体调度与候选包验收顺序。共同工程审阅另补齐 begin 命令、正常静音消费、单实例异步退出、Host fatal 传播、通知订阅顺序及首次 enable 就绪，不替代主代理自审。

资格依赖仍要求 Task 2 在 Task 7 实际启用/听音之前通过；按执行裁定，Tasks 3–6 软件及 Task 8 静态回归/整组审阅/候选构建已提前完成。Task 4/5 宿主增量提供 CaptureEpoch 和按预期 epoch 的 TrySetCaptureGateAsync，旧会话不能放行新采集。门槛未通过不得启用后台或把新包替换日常入口；Task 7 和 Task 8 的实际资格/安装哈希匹配仍未完成。

2026-10-15 用户已独立安装核验过的原厂驱动，暂时不能重启。Task 1、3–6 软件已完成，整体审阅的两项 Important 经失败回归后修复；实际候选和对应 GPL 源码已构建并通过本机包检查，旧包保留。名称持久性、重启、PCM 校准和 Task 7 实机窗口仍待执行，断网测试继续暂缓；不重问已接受的路线选择。
