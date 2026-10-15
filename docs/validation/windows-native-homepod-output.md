# Windows 原生 HomePod 输出验收记录

日期：2026-10-15。依据 [原生输出规格](../superpowers/specs/2026-10-14-windows-native-homepod-output-design.md)与[实施计划](../superpowers/plans/2026-10-14-windows-native-homepod-output-implementation.md)。用户已在计划交接后继续，进入 Native inline 实现。

## 已实现和已验证

- 新增真实端点/驱动绑定、配置原子保存、严格必需字段和大小限制；未校准绑定不能激活。
- 通过原始 pin 描述、方向、适配器、供应商、驱动版本及 SYS SHA256 核验普通 VB-CABLE 播放端点；已有实例、16 通道变体及麦克风不被猜测绑定。
- 显式命名维护保存原名，失败尝试只恢复仍属本应用的写入；用户改名保留。实际命名和重启持久性尚未实测。
- 原生维护命令在构造 App 前分派，成功/错误返回 JSON 与退出码；诊断导出隐藏端点与会话身份，保留驱动版本。
- `scripts/test-native-output.ps1` 在本机实际执行三项：全状态检查、缺少绑定身份拒绝、输出是目录时无阻塞对话框地失败。真实报告 `ok=true`、`wpf_initialized=false`、`driver_installed=false`；枚举 63 个所有状态的端点，其中 11 个活动播放端点。安装前快照已保存；前后默认输出角色相同。

源码工作树：`C:\Users\Administrator\.codex\worktrees\applehome-native\applehome`。原便携包 `dist/AppleHome.exe` 未替换；目前诊断使用本次 Debug 构建，不把它当成新模式交付包。

## 可复制的诊断

在该工作树运行：

```powershell
. '.\.runtime\dev-env.ps1'
pwsh -NoProfile -File scripts/dotnet.ps1 build AirFlash.App --no-restore
pwsh -NoProfile -File scripts/test-native-output.ps1 -Executable desktop/AirFlash.App/bin/Debug/net10.0-windows/win-x64/AirFlash.exe
```

报告保存在 `artifacts/validation/native-output/check-*/`。安装前记录为 `%APPDATA%\AppleHome\native-output-preflight.json`，绑定为同目录 `native-output.json`。这些私有数据与本地报告不进入 Git。

## 驱动门槛与未完成项

当前尚未安装独立 VB-CABLE。安装必须使用独立原厂包，管理员运行 `VBCABLE_Setup_x64.exe`，遵循厂商重启要求；AppleHome 不自动运行或嵌入该安装器。包 SHA256 固定为 `b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb`，x64 Win10 SYS 为 `f01344602472f1b527de5ee98f18987c03db48fea444e457d061e937f1d531d5`。此前验签证据在 `artifacts/validation/vbcable-pack45-inspection.json` 与 `vbcable-kernel-signature.log`，不能替代本次实际安装/绑定证据。

| 项目 | 状态 |
| --- | --- |
| 端点绑定规则/存储/诊断出口 | 已有软件回归与本机只读检查 |
| 实际驱动安装、名称写入和重启持久性 | 待执行 |
| PCM 取样位置与 Windows 主/应用音量校准 | 待执行 |
| 真实 HomePod 非零参考音量与听音确认 | 待执行 |
| 常驻采集、首段暂存、无面板后台 | 待实现 |
| 新路径 15 秒暂停/静音恢复与完整 30 分钟 | 待验收；旧版被手动停止的长播不算成功 |
| 断网测试 | 用户明确暂缓 |
| Windows 11、其他型号/固件与立体声对 | 待对应环境实际验证 |

未通过驱动、命名与 PCM 门槛，不启用登录后台，不把开发代码替换为日常入口。实际路由、物理设备音量/静音、Voicemeeter、驱动策略与系统启动任务本轮均未改动。
