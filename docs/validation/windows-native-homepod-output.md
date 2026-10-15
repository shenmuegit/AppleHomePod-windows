# Windows 原生 HomePod 输出验收记录

日期：2026-10-15。依据 [原生输出规格](../superpowers/specs/2026-10-14-windows-native-homepod-output-design.md)与[实施计划](../superpowers/plans/2026-10-14-windows-native-homepod-output-implementation.md)。用户已在计划交接后继续，进入 Native inline 实现。

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
| .NET 常驻宿主、系统路由和无面板后台 | 待实现 |
| 新路径 15 秒暂停/静音恢复与完整 30 分钟 | 待验收；旧版被手动停止的长播不算成功 |
| 断网测试 | 用户明确暂缓 |
| Windows 11、其他型号/固件与立体声对 | 待对应环境实际验证 |

未通过重启、命名与 PCM 门槛，不启用登录后台，不把开发代码替换为日常入口。物理设备音量/静音、Voicemeeter、驱动签名策略与系统启动任务未由应用改动；默认角色变化如上如实记录。

用户暂时不能重启，因此先继续不依赖硬件的实现和自动化回归。Rust 新路径先准备显式播放端点，冻结最近 100 ms 首段并有界保留最多 2000 ms；初始化关门不消耗首段，溢出明确失败，正常静音以零增益持续消费。停止后重置 epoch，旧帧不能进入下一路由。媒体调度在首次放行时重建，实际成功发送后才报告 first_send；声学分析同时兼容旧报告。此处均为队列/本地 IPC/localhost 故障测试，未在新增驱动上启动真实采集或连接 HomePod。启动暂存的附加延迟及长播时钟漂移需要实测。
