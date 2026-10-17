# Windows 原生 HomePod 输出验收记录

初始日期：2026-10-15；最近更新：2026-10-17。依据 [原生输出规格](../superpowers/specs/2026-10-14-windows-native-homepod-output-design.md)与[实施计划](../superpowers/plans/2026-10-14-windows-native-homepod-output-implementation.md)。用户已在计划交接后继续，进入 Native inline 实现。

## 当前受控试听状态

固定代码 `d2cf09ee9226098e73b9413108246d974df8032f` 已发布为自包含单文件候选 `dist/native-output-level-fix/AppleHome.exe`，176,925,185字节，SHA256 `c58d11377dc1f6495a62115a5931fdd59261c150612bf4e87e6bcaf06dc2656a`；内嵌引擎与本次构建的 `fe7e1a44dadf5e4708f92e4cf5fbfbc1553213a007826437ed4ea5ad92585b79` 一致。实际候选的compat hello通过。用户确认在场后，经三秒提示运行实际C#校准，用户明确确认听到新测试音；随后以同一实际回读B=72重新取样和复测并保存，`calibrated=true`。主音量不含于PCM，应用半档RMS约0.0141437，peak约0.0200005；七窗口报告三项检查通过。没有把独立参考发送器的出声当成产品通过。

已将同一候选安装到当前用户本地应用目录并注册自己的登录任务。实际安装SHA与上述候选一致；系统任务为InteractiveToken/LeastPrivilege，动作`--native-agent`。`after-enable-check.json`报告`task_exists=true`、`task_owned=true`、`ready=true`、`normal_window_count=0`、`daily_tray_registered=false`；实际冷启动日志`capture_ready cold_start_ms=4753`。本机原有任务和目标安装EXE在这次启用前均不存在，没有覆盖已有用户程序。新后台与其引擎已运行，旧便携包文件保留。

依最新实施裁定，驱动重启/当前命名/稳定ID/校准/认证已通过，允许可撤销的受控后台试听；显示名称跨重启持久性与下一次真实登录一起验收，尚未标通过。当前请求用户在Windows输出中选HomePod、先将Windows音量设到约30%再播放15–20秒；在用户确认前，Windows系统音乐播放、物理扬声器隔离、主/应用音量、静音恢复及完整30分钟仍待验收。首次资源快照仅为自有进程：Agent工作集约60.5MB、引擎约10.4MB；这不是长播稳定性证明。证据目录`artifacts/validation/native-output/package-d2cf09e/`。

## 2026-10-17 设备恢复与听音失败

用户随后明确要求放大声音。第一次33→72的提高音量试验未被观察；之后经三秒提示直接重播，用户全程留意仍反馈无声。改用已固定的独立参考发送器，五秒 WAV 的输入峰值5%、gain1、volume_fraction0.375，用户立即确认听到间隔短音，程序正常停止并断开。此时不能把两个信号强度不同的结果直接认定为媒体协议故障。

产品校准原来把峰值4%的 WAV 再乘0.1，实际只有0.4%。按用户放大测试的要求，修正为 WAV gain1，实际峰值4%；Rust 只将受原输入峰值约束的 WAV probe 放宽到gain≤1，保留S16 stereo、44100/48000、五秒、每样本绝对值≤1639。有限loopback probe仍限制gain≤0.1，实采/start不变，时钟/认证/编码/媒体协议未改。真实临时 WAV 和无网络 IPC 回归先失败再通过；完整 .NET 366 项、Rust 64 项通过，2 项原有忽略，Clippy与静态CRT检查通过。新引擎SHA256为 `fe7e1a44dadf5e4708f92e4cf5fbfbc1553213a007826437ed4ea5ad92585b79`。修正后的产品听音仍需新候选实测。

用户因没有注意前次测试而要求重播。首次重播在发现阶段超时，未发送 HomePod 参考音；用户随后恢复供电或网络。经过有界等待，重新发现同一只 `9efd9099abd6`，型号、27.0 固件、单只状态和地址均与原绑定一致。Windows 的虚拟端点、名称及驱动身份仍正常。

固定 `f462560` 候选随后完成五秒 NTP/ALAC/2000 ms 参考测试，回读音量 33，PCM 报告三项检查通过；用户明确反馈仍然无声。此前可听的原生测试会重新应用回读的音量，故另做单变量对照：相同 .04 峰值、997 Hz、.1 增益、五秒与协议参数，只把当前回读的 33 原值应用一次。该写入得到确认，发送 627 个媒体包，媒体/同步错误为 0，反馈成功两次；用户仍反馈无声。因此未把此假设并入产品，也不把传输完成算作出声。

历史可听测试回读为 72，本轮为 33，音量差异尚未排除。用户随后确认 iPhone 网易云音乐经 AirPlay 可正常出声，并已暂停或切回 iPhone。此后 Windows 校准在参考音发送前被单只配置门槛拒绝：完整发现仍为同一 mini/27.0，但 GroupId 非空、IsLeader=false；等待 15 秒再进行八秒 fresh discovery 后状态不变。不能将此当作部分元数据、固件变化或新的听音失败，也没有绕过分组检查。已请求用户明确将 AirPlay 输出选回 iPhone，等待期间不抢占手机播放。

校准没有保存，后台未安装或启用，源码没有因上述未证实假设而改变；原交付 ZIP 和 manifest 仍保留其快照。最新证据在本地 `package-f462560/` 下的 `calibration-recovered-20261017-2246a0897462460c9a0db9b93f268473.json`、`reference-same-volume-20261017.json`、`calibration-after-iphone-20261017-67144392959c4a55b7f767a4994ee038.json` 与各发现/清理报告中。

## 2026-10-16 实机续验状态

本机驱动重启已核验：Windows 10 19045，启动时间为本地 18:22:18，`reboot_verified=true`；普通端点 ID、适配器、3.3.1.7 版本和固定 SYS 哈希保持一致。用户随后通过 Windows 属性将普通播放端点命名为 `HomePod – da`，fresh read 读到该 EndpointLabel，原始 pin 仍为 `CABLE Input`，已刷新绑定。该命名发生在此次重启之后，不能据此声称名称已经跨重启保留；这一门槛仍须在后台启用前验证。

首次正向 PCM 校准在应用混音器检查失败：测试会话已降到 0.5，最初窗口仍混有旧满档样本。数值诊断中基线 RMS 约 0.0282875，半档窗口为 0.0178001、peak 0.0400009；保持半档再取样得到 RMS 0.0141434、peak 0.0200005。修正仅将每档取样前的排空等待由 100 ms 改为 300 ms，保留五秒源上限、身份核验、严格单次增益判定与恢复规则。诊断仪表代码已移除。

新增 `scripts/test-native-calibration-report.ps1` 直接检查实际报告的半档波形统计和取样窗口。它先拒绝含旧满档 peak 的报告，再通过修复后的真实七窗口报告。完整 .NET 366 项通过，构建无警告/错误。新候选 `f4625603abf66fc0b50e503e5d509a725b166fc6` 位于 `dist/native-output-f462560/AppleHome.exe`，176,925,185 字节，SHA256 `1ab5a71680541ac2f66361535691166909eb484aa5a6b66d0be19c866294b7c2`；原候选及旧面板包均保留。

该实际候选通过 compat hello、577 项 UI smoke、5 项原生只读检查及 2 项启动 schema/未校准拒绝检查。正向校准退出 0：主音量不含于 PCM，应用半档 RMS 0.0141376 / peak 0.0200005，接收端当前参考音量 33。已播放五秒低峰值参考音，但仍等待用户确认实际听到，因此校准尚未保存。校准后的 fresh monitor 读到虚拟端点恢复为 dB 0 / scalar 1 / 未静音，默认输出没有改变，自有测试源、引擎和 agent 均已退出。没有安装日常应用或创建启动任务。

本轮证据目录为 `artifacts/validation/native-output/post-reboot-20261016-0fd05b0a35e94c2ebbc1438179acfa59/` 和 `package-f462560/`，另有 `check-b7a92b9e5ad3445e9238194989ae229b/`、`startup-52343ef6ddce4610a9078638f60bb8ab/`。名称持久性、可听参考确认与保存、实际启用/登录/通知、原生控制听音和完整 30 分钟仍待验收；断网继续按用户要求暂缓。以下保留 2026-10-15 的分阶段历史记录，其中“尚未重启/未采集”等描述对应当时状态。

## 已实现和已验证

- 新增真实端点/驱动绑定、配置原子保存、严格必需字段和大小限制；未校准绑定不能激活。
- 通过原始 pin 描述、方向、适配器、供应商、驱动版本及 SYS SHA256 核验普通 VB-CABLE 播放端点；已有实例、16 通道变体及麦克风不被猜测绑定。
- 绑定保存原名，首次命名通过 Windows 自带播放设备属性完成；应用不写名称，用户改名保留。重启持久性尚未实测。
- 原生维护命令在构造 App 前分派，成功/错误返回 JSON 与退出码；诊断导出隐藏端点与会话身份，保留驱动版本。
- `scripts/test-native-output.ps1` 在本机实际执行三项：全状态检查、缺少绑定身份拒绝、输出是目录时无阻塞对话框地失败。真实报告 `ok=true`、`wpf_initialized=false`、`driver_installed=false`；枚举 63 个所有状态的端点，其中 11 个活动播放端点。安装前快照已保存；前后默认输出角色相同。
- Task 2 的软件部分已实现：dB 单次增益计算、重复 PCM 分类、16/24/32 位 PCM 与 float 统计、应用混音器单次衰减判定，以及受绑定约束的本机校准入口。16 项新校准测试通过；全部 .NET 回归为 308 项通过，应用构建无警告/错误；既有 UI smoke 的 577 项检查通过。
- 本机诊断脚本已扩展到六项并通过：另验证私有配置不能被报告输出覆盖、无绑定时拒绝校准、无绑定时拒绝短时测试源。此处只验证了拒绝路径，没有启动 PCM 采集或 HomePod 参考音。
- 校准测试源使用单独的当前程序进程，峰值 0.04、最多 5 秒，退出只处理该自有进程。采集与测试播放不共用进程的默认音频会话；原生后台实例存在时拒绝维护。mute/0 的无数据情况独立记录，不当作非静音采集成功。
- 驱动包、SYS 哈希及安装器/目录签名本轮重新核对通过，SignTool 内核目录验证退出 0，报告 `artifacts/validation/native-output/driver-prepared.json` 明确 `setup_executed=false`。

源码工作树：`C:\Users\Administrator\.codex\worktrees\applehome-native\applehome`。原便携包 `dist/AppleHome.exe` 未替换；目前诊断使用本次 Debug 构建，不把它当成新模式交付包。

## 可复制的诊断

在该工作树运行：

```powershell
. '.\.runtime\dev-env.ps1'
pwsh -NoProfile -File scripts/dotnet.ps1 build AirFlash.App --no-restore
pwsh -NoProfile -File scripts/test-native-output.ps1 -Executable desktop/AirFlash.App/bin/Debug/net10.0-windows/win-x64/AirFlash.exe
```

报告保存在 `artifacts/validation/native-output/check-*/`。安装前记录为 `%APPDATA%\AppleHome\native-output-preflight.json`，绑定为同目录 `native-output.json`。这些私有数据与本地报告不进入 Git。

安装、绑定与名称持久性通过后才运行 `--native-calibrate --output <独立JSON路径>`。该命令只对已核验的虚拟播放端点校准，随后通过已有有界 NTP/ALAC probe 读取当前 mini 的实际非零参考音量；首次仅产生候选报告，不保存可用校准。用户听音确认后，以 `--confirm-reference-volume <报告中的实际值>` 再核验并保存；值不符、没有参考、绑定变化或用户中途改音量均停止，不自动设置 HomePod 为 100%。这些内部维护命令不是日常控件。

## 驱动门槛与未完成项

用户已从桌面的完整原厂包独立安装 VB-CABLE，尚未重启；Windows 已枚举新增的普通 Input、16ch 播放和 Output 录音端点。实际供应商、版本 `3.3.1.7` 与 x64 Win10 SYS 哈希均匹配。包 SHA256 为 `b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb`，SYS 为 `f01344602472f1b527de5ee98f18987c03db48fea444e457d061e937f1d531d5`。

管理员进程的首次命名尝试在 `IPropertyStore.SetValue` 返回 `0x80070005`，原名称未改变；保存的未校准绑定不能激活。已删除不受支持的名称写入/还原代码，采用首次在 Windows 自带 `mmsys.cpl` 的播放设备属性中命名。用户名称保留，绑定无需管理员权限。

检查还证明 16ch 变体当前同样报告 2 通道，不能靠混音通道数排除；用户也可通过系统属性改变 DeviceDesc。识别已改为 `connector.ConnectedTo.Part.Name` 的原始驱动 pin，普通输出为 `CABLE Input`、16ch 为 `CABLE In 16 Ch`，独立保存可变 EndpointLabel。全状态枚举中失效连接器保留为不能认领的未知 pin，避免旧设备的 `FileNotFoundException` 阻塞新设备检查。相关回归按失败再修复执行；现有 .NET 测试 311 项通过，构建 0 警告/错误，本机五项检查通过（含命名前拒绝绑定且原绑定文件不变）。

当前全状态端点数 66。Console/Multimedia 默认输出在安装前后已变为 CABLE Input，Communications 仍为原输出；应用没有修改默认输出。后台未启用，用户需要临时电脑声音时可用 Windows 切回物理输出。证据在 `post-install.json`、`first-bind.json`、`check-a89408fcb6a8475e999dfa920b97dac0/`，均为本地忽略报告。

| 项目 | 状态 |
| --- | --- |
| 端点绑定规则/存储/诊断出口 | 已有软件回归与本机只读检查 |
| 实际驱动安装 | 已独立安装且实际身份/文件哈希核验通过；厂商要求的重启待完成 |
| 首次系统命名与重启持久性 | 改为 Windows 系统属性一次性命名；待用户完成并重启验证 |
| PCM 取样位置与 Windows 主/应用音量校准 | 软件工具就绪，实际正向路径待驱动安装后执行 |
| 真实 HomePod 非零参考音量与听音确认 | 待执行 |
| 常驻采集、首段暂存 | Rust 实现已通过隔离回归；真实音频路径未启用 |
| .NET 常驻宿主 | 软件回归通过；真实进程与虚拟端点的组合待校准后验收 |
| 系统路由/音量协调 | 软件回归通过；本机只读 MTA 监听通过，真实转发/通知操作待验收 |
| 无面板后台与当前用户启动任务 | 软件实现和本机拒绝路径通过；实际启用/登录/错误通知待硬件门槛后验收 |
| 新路径 15 秒暂停/静音恢复与完整 30 分钟 | 待验收；旧版被手动停止的长播不算成功 |
| 断网测试 | 用户明确暂缓 |
| Windows 11、其他型号/固件与立体声对 | 待对应环境实际验证 |

未通过重启、命名与 PCM 门槛，不启用登录后台，不把开发代码替换为日常入口。物理设备音量/静音、Voicemeeter、驱动签名策略与系统启动任务未由应用改动；默认角色变化如上如实记录。

用户暂时不能重启，因此先继续不依赖硬件的实现和自动化回归。Rust 新路径先准备显式播放端点，冻结最近 100 ms 首段并有界保留最多 2000 ms；初始化关门不消耗首段，溢出明确失败，正常静音以零增益持续消费。停止后重置 epoch，旧帧不能进入下一路由。媒体调度在首次放行时重建，实际成功发送后才报告 first_send；声学分析同时兼容旧报告。此处均为队列/本地 IPC/localhost 故障测试，未在新增驱动上启动真实采集或连接 HomePod。启动暂存的附加延迟及长播时钟漂移需要实测。

.NET 常驻宿主使用唯一 stdout 读泵和每会话最多 64 条事件的通道；取消租约读取不取消基础 reader。停止确认后重置采集 epoch，hello 租约退出保留宿主；坏 JSON、半行 EOF、超长行、通道溢出或停止无确认会使整个宿主失败并结束其自有进程。新增原生增益覆盖复用现有发送串行边界，连接期间的更新在开门前写入，默认仍保留旧 UI 的增益行为。该任务定向 65 项通过，完整 .NET 322 项通过；没有启动真实引擎或修改系统路由。

路由协调已覆盖 Console/Multimedia/Communications、单独应用需求、三种 owned 身份排除、重复/旧代际通知、绑定失效、接收端名称/IP 改变和未验证固件拒绝。单应用实际活动持续保持，暂停后才开始 30 秒保持；可控单调时钟覆盖 15 秒暂停/静音、30 秒边界及模拟 30 分钟活动，后者不是实际长播。远端初值 0 无新意图时仅同步虚拟静音并零增益消费；后续 Windows 正向操作才恢复运行时/校准参考，匹配回读前不放行，连接中更新保留最新增益，post-master 的旧音量暂存被清除。新路径不调用物理 mute/恢复 journal，也不发送旧 UI EQ 更新。相关定向回归 99 项通过，完整 .NET 343 项通过，构建 0 警告/错误。

本机六项诊断通过，报告 `check-899598d5117c4c69b214318a82febb5b/monitor.json` 记录真实 listener_apartment=MTA、2 个会话、1 次快照通知、三个默认角色；engine_started/capture_started/wpf_initialized 均为 false，binding_valid=false。绑定文件未变，未启动 PCM 或 HomePod 会话；该检查不要求校准合格，也不会把只读监听成功当作可启用资格。监听先订阅再 GetCount 的顺序依据 [NAudio 2.2.1 源码](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/CoreAudioApi/AudioSessionManager.cs)和[微软会话通知要求](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionmanager2-registersessionnotification)。真实用户通知、恢复听音和持续播放仍待硬件门槛后验证。

无窗口入口、Native 独立单实例与 ready/stop 管道、当前 SID 登录任务及安装/撤销代码已实现；旧面板只通过 `--legacy-panel` 或既有显式诊断进入。Task Scheduler 定义固定 InteractiveToken/LeastPrivilege、失败最多 3 次且间隔 PT1M、电池允许、无执行时限、IgnoreNew，不保存密码或创建服务。任务名包含 SID；私有 receipt 保存系统归一化 XML 和实际安装二进制哈希，任务或文件被用户修改时保留。第一次启用在资格通过后运行自有任务并等待 capture_ready，无需再次登录；正常停止返回 0，Host fatal 返回 1。Native 管道断开的客户不能终止后续监听，Mutex 不持有线程所有权，异步跨线程退出已回归。

本次 18 项启动测试与 25 项原托盘策略测试通过；完整 .NET 为 361 项通过，旧 UI 实际 smoke 为 577 项通过。实际隔离 `--native-check` 首轮在缺失任务的 COM HRESULT 被映射为 FileNotFoundException 时失败，修复该具体映射后通过；它在 Windows Task Scheduler 中创建**仅内存**的任务定义并验证真实 schema，未注册任务。`scripts/test-native-startup.ps1` 两项通过：缺少绑定仍为无界面检查，实际未校准绑定的 enable 请求返回非零，绑定/安装 EXE 哈希及任务存在状态不变。最新报告 `startup-c885afd4cc56404daa866bfcf5994209/`；正常窗口、日常托盘、引擎和采集均未创建。原生输出六项检查也重新通过，报告 `check-1052bbd907a443f293d5358c2582f265/`。

校准、测试源和 enable 现在均要求私有安装前证明之后的重启；boot UTC 由系统单调 uptime 推算，系统时间大幅校正后需复核证明。当前不运行正向校准或 enable，不复制日常安装 EXE、不注册登录任务、不恢复旧物理 mute journal。一次性迁移代码只在资格通过且旧面板退出后使用既有 journal 所有权规则；正常后台的 RestoreAsync 始终无物理设备状态。通知代码共用原 Shell 结构，在专用 STA 的短时隐藏窗口上尊重 quiet time，最迟 15 秒清理；实际通知可见性和用户登录启动未验收，不能由代码或 schema 检查替代。

整组软件由独立上下文审阅，范围 `6ab8c22..66b3eb9`。两项 Important 发现均先复现失败再修复：接收端 IP 更新/网络租约更换时不再用旧 streaming/音量状态放行，新 gate 写入携带预期采集 epoch 并在同一串行边界拒绝旧 epoch；发现固件/型号/leader 信息未补齐时在原 2 秒上限内等待，已知不兼容固件或分组仍拒绝。修复还保护关门期间新音量的 PCM 不被开门时重复清除，并按会话事件发出时的用户 Revision 识别先后顺序。宿主/协调器定向 35 项和完整 .NET 366 项通过。审阅没有提出 Critical 或 Minor；硬件、实际任务/通知、首音/尾音/时钟漂移/长播、断网及其他环境均明确留在既有门槛内，不因此取得资格。

完整 Rust 检查为 62 项通过、2 项原有 ignored（需要明确设备的 WASAPI 检查和 30 分钟 localhost soak）；Clippy 以 warnings-as-errors 通过。`scripts/build-native.ps1 -Check` 已从当前源码重新构建 release 引擎并验证静态 CRT/兼容审核；原生静态审核保留 144 个未确认导入项，退出 0 不代表 Windows 11 实机资格。Python 96 项通过，Ruff 通过。候选与对应源码/哈希将在后续记录中固定，尚未安装或启用。

候选已从代码提交 `46a0275c996580ed18c62b39ab85f2388c9e1733` 固定生成，版本 `0.2.0+46a0275c996580ed18c62b39ab85f2388c9e1733`，自包含 x64 单文件位于 `dist/native-output/AppleHome.exe`，大小 176,925,185 字节。EXE SHA256 为 `8f19be37b281b30cec3539b9910e25d70d17c84095344d302bf1b0ba2f43e85a`；本次 release 引擎为 `2b03b0eb19ee4f2db5ee8910c681080f77af5eebe57951be4d9d39ca7f076208`。实际候选的 compat-check 启动了**仅 hello** 的内嵌引擎，确认 arm_capture 等新命令，提取文件哈希与本次 release 一致，未 arm 或播放。该包的 UI smoke 577 项、启动/schema/拒绝启用 2 项和原生只读检查 6 项均通过。报告分别在 `package-46a0275/`、`startup-5579bef0cc57410e92bef8e09f9d593e/`、`check-6823bdb53a8848938235829694f028fa/`。

实际候选 EXE 的静态审核退出 0，记录对应 EXE 哈希及 447 个未确认导入；发布前 450 个 bundle 输入镜像的审核记录 1,555 个未确认导入。这些是静态边界，不是其他系统的运行证明。对应 GPL 源码 `AppleHome-source-46a0275.zip` 为 1,467,090 字节、235 个条目，SHA256 `3c729581b89a5d48a6dd55b2ef63283feef6f40c929924389f224b8abb7f426f`；已核对实现、锁文件和 GPL 全文，并排除运行目录、私有绑定与凭据。manifest 区分代码提交与后续文档提交，所有资格字段仍为 pending_hardware。

保留的旧 `dist/AppleHome.exe` SHA256 重新核对为 `992c36249a4b91c014c71b78959ba82d561bb4f958c8eefed49998d995e44ca5`，没有被新候选覆盖。新候选未复制到日常安装位置，未注册任务，未启动真实采集或 HomePod 会话；仅 hello 检查生成了按哈希管理的自有引擎缓存。重启、系统命名持久性、PCM/听音参考、正向 enable/登录/通知及完整 30 分钟仍待实机窗口。断网仍暂缓，其他环境仍未验收。
