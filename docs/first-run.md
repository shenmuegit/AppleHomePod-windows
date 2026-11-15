# AppleHome 首次安装

适用于 **0.2.0 Preview 5**。当前实机范围：**Windows 10 Pro 22H2（19045，x64）与单只 HomePod mini 27.2**。没有型号／固件播放白名单，但其他系统、设备组合与立体声对尚未取得对应实机验证。

## 安装前

- HomePod 已在 Apple“家庭”App 初始化，与电脑位于同一局域网。
- 从 [GitHub Release](https://github.com/shenmuegit/AppleHomePod-windows/releases/tag/v0.2.0-preview.5) 下载 `AppleHome-0.2.0-6fc6482b1521.zip`，可对照同页 `SHA256SUMS.txt` 校验文件。
- 应用为自包含版本，无需安装 .NET 或 Rust。

## 三步完成设置

1. **解压并打开 `AppleHome.exe`。** 按向导安装音频驱动。Windows 要求管理员许可或重启时，按提示完成，重启后重新打开 AppleHome。
2. **等待发现并连接 HomePod。** 只有一只连接信息有效的已发现设备时自动连接，多只时选择目标。设备名称、Windows 播放输出和连接参数均由程序配置。
3. **确认听到声音。** 需要重听时点击“再播放一次”，听到后确认并完成设置；未出声时选择“没听到”，由程序检查并重试。

无需手动改名、切换首次播放输出、填写 IP、选择网卡或输入缓冲时间。本构建通过更新已有安装验收，使用有效驱动、绑定与校准记录；全新自动驱动安装、重启到确认的完整流程仍待独立实机验收。

向导按 Windows 首选显示语言自动选择英文、简体中文或繁体中文，其他语言回退英文，无需手动选择语言。

## 日常使用

直接播放正常音乐或视频，音量与静音使用 Windows 控件。首次设置会自动选择 HomePod；以后可在 Windows 输出列表切换 HomePod、耳机或电脑扬声器。后台随当前用户登录启动，日常无需打开 AppleHome。

设备未出现时先确认通电、同网和 AirPlay 可访问，再点击“重新查找”。当前向导未提供 PIN 输入，不要求手填 IP 或选择网卡。反馈问题时请提供系统构建、HomePod 型号与固件，分享日志前先检查个人信息。

## 已有预览安装

重新打开向导会复用已安装程序，不承担自动升级。当前已运行同一构建时无需重装。替换更早的预览后台时，在解压目录打开 PowerShell：

```powershell
$stop = Start-Process .\AppleHome.exe -ArgumentList '--native-disable' -Wait -PassThru
if ($stop.ExitCode -ne 0) { throw '后台停止失败，请按错误提示处理。' }
$update = Start-Process .\AppleHome.exe -ArgumentList '--native-enable' -Wait -PassThru
if ($update.ExitCode -ne 0) { throw '后台更新失败，请按错误提示处理。' }
```

维护仅适用于已有有效绑定与校准的安装。程序只更新仍属于本应用的任务和文件，出现所有权、绑定或校准问题时请按具体错误处理。

## 播放与自动恢复的验证范围

Preview 5 移除可能在握手前拒绝已发现 HomePod 的型号／固件限制，设备身份、连接信息与实际认证仍需有效。时钟偏好和缓冲记录用于连接适配，不作为播放许可；固件变化后旧档案的缓冲参考失效。

本轮 mini 27.2 的正常音乐、Windows 音量变化和至少 15 秒静音恢复均获用户确认。该会话自动协商后采用 250 ms 缓冲候选，用户无需输入；这不是统一默认值或声学延迟测量。

本版也包含选择 HomePod 输出期间的引擎常驻监督与已有的 mDNS 自动切换。空闲、暂停、静音或累计失败次数不会取消运行意图；切换到其他 Windows 输出才释放发送引擎。

**本发布包的过夜空闲恢复与真实睡眠／唤醒尚未实机验证。** 持续音乐／视频、断网恢复以及其他家庭访问／PIN 策略仍需按构建单独验收，具体范围见兼容记录。

本版对应源码提交 `6fc6482b152188f29e37a9a7d55f8502ebec29ad`，源码包为 `AppleHome-source-6fc6482b1521.zip`，EXE 的 SHA256 为 `434abefdc0ec75646a4e30ab6cfbcbd100ecd73cf318bee9d7380a7627e0cfd0`。归档文档保留构建时快照，当前安装与验证范围以 Release 随附指南和版本说明为准。

## 驱动与许可

VB-CABLE 来自 [VB-Audio Software](https://vb-cable.com/)，采用 **Donationware** 模式，欢迎向作者捐助或支付许可。原厂 Pack45 与说明保持未修改，“关于”保留来源与许可入口；驱动遵循[厂商独立条款](https://vb-audio.com/Services/licensing.htm)，不属于 AppleHome 的 GPL 源码。AppleHome EXE 当前未签名。

完整范围见[兼容矩阵](https://github.com/shenmuegit/AppleHomePod-windows/blob/main/docs/COMPATIBILITY.md)，本包验证及哈希见[版本说明](https://github.com/shenmuegit/AppleHomePod-windows/blob/main/docs/releases/v0.2.0-preview.5.md)。
