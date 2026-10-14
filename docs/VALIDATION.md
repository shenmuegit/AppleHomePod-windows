# AppleHome 本地验证记录

日期：2026-10-14。当前结论：**软件检查通过；独立 Rust 参考引擎已实际出声，AppleHome 本体尚无出声与系统音频闭环认证**。

## 已完成的软件与 Windows 检查

| 检查 | 结果 | 证据位置（本机忽略目录） |
|---|---|---|
| 完整 .NET Core 测试 | 270 通过，包含 NTP/ALAC 配置与有效缓冲回归 | `artifacts/validation/ntp-profile-full-green.log` |
| WPF UI smoke | 577 项通过；服务使用模拟实现，托盘与窗口在本机实际运行 | `artifacts/validation/ntp-profile-ui-smoke.json` |
| Rust 单元与本地网络测试 | 53 通过；两个需要硬件或长时间运行的用例明确忽略 | `artifacts/validation/review-full-native.log` |
| Clippy，警告视为错误 | 通过 | 同上 |
| 原生 release 构建与静态 CRT 检查 | 通过 | 同上 |
| 原生导入审核 | 无阻断项；仍有 144 个未由目录确认的导入，不能外推所有 Windows 10 构建 | `artifacts/compatibility/engine-audit.json` |
| 真实 Windows 静音归属与恢复 | 8 项通过，见下文 | `artifacts/validation/audio-ownership.json` |
| Python 验证工具 | 95 通过；Ruff 通过 | `artifacts/validation/final-pytest.log`、`final-ruff.log` |

静音检查使用实际 `AudioService` 程序集、NAudio 2.2.1 和本机默认播放端点，使用独立恢复文件。检查了：应用静音落盘原状态；外部解除静音后撤销恢复所有权；之后外部重新静音时停止不覆盖它；受控子进程静音后直接退出，构造新服务不改声音，显式启动恢复才恢复原状态；音量标量未被改写。执行后端点回到测试前的静音状态。此检查没有采集系统声音或麦克风。

最终整分支审查的六项重要问题已逐一复现并修复：恢复文件删除失败后的静音归属、自动重连覆盖外部解除静音、录音端点进入系统声音采集、手动设备空或重复 ID、密钥标签变体与自由文本身份泄漏，以及网卡切换期间发现快照乱序。每项有先失败再通过的回归；最终完整套件如上。录音端点在界面目录和原生采集边界均被拒绝。

另以真实 Windows 端点锁定恢复文件，复现“外部解除静音、外部再次静音、应用停止”的写入失败路径。修复后保留用户静音选择，错误可见；解锁后重试清除成功，见 `audio-ownership-io-green.log`。若持久清除失败后立即崩溃，旧磁盘记录仍可能产生歧义，应解决写入错误并重试后再退出。

## mini 的实机发送试验

环境：Windows 10 Pro 22H2 build 19045 x64；Intel Wi-Fi 6 AX200；同一局域网；单只 AudioAccessory5,1；OS 27.0；`sourceVersion=980.77.2`。实际地址、设备标识和完整报告只保存在本机忽略目录。

| 试验 | 传输证据 | 听音结果 |
|---|---|---|
| 上游基线 PCM、PTP、150 ms 目标 | 瞬时 SRP 验证通过、627 包、反馈成功、干净停止 | 用户未听到 |
| 相同条件改用 ALAC | 协商 ALAC、627 包、干净停止 | 用户未听到 |
| ALAC 跟随接收端时钟 | 解析配对 Sync/Follow_Up，627 包、干净停止 | 用户未听到 |
| 修正 PTP 采样位置与前向缓冲窗口 | 627 包、干净停止 | 用户在设备旁仍未听到 |
| 2000 ms 目标缓冲 | 627 包、干净停止 | 用户仍未听到 |
| PTP RTP SSRC 为零，并重新应用原 31% 音量 | 627 包、音量确认、干净停止 | 用户仍未听到 |
| 独立 node-airplay 引擎对照 | PTP 协商明确；625 包；50 次 PTP 交换；原 31% 音量；5 秒源音频后额外留出 2 秒缓冲；正常关闭 | 用户仍未听到 |
| iPhone 网易云音乐，经原生 AirPlay 选择同一只 mini | 用户操作并确认正在出声，随后暂停或切回 iPhone | **可以出声** |
| 独立 node-airplay 提高测试信号后重播 | 实际源峰值 0.05，增益 1；5 秒源音频与 2 秒缓冲；626 包、51 次 PTP 交换；保留接收端当时的 38% 音量并正常关闭 | 用户仍未听到 |
| 独立 windows-airplay-homepod Rust 参考引擎 | ALAC、请求 NTP；5 秒源峰值 0.05、增益 1；保留原 -18.75 dB / 37.5% 音量；627 包、1 次 underrun；正常停止和断开 | **用户确认听到声音** |
| AppleHome 原生已有 NTP/ALAC 路径对照 | 仅切换现有时钟选项，2 秒目标，原生增益仍为 0.1；627 包、干净停止；信号比成功参考试验低 20 dB | **用户确认听到较轻的测试音** |
| 相同原生 NTP/ALAC 路径，200 毫秒目标 | 编码、音量和信号不变；627 包、干净停止；第一遍无法判断，倒数后重播 | 用户确认重播仍无声 |

早期 Windows 发送试验为预生成的 44.1 kHz、16-bit 双声道信号；源峰值 0.05，增益 0.1，实际峰值约 0.005（约 -46 dBFS），因此随后另外校准了听音检出能力。没有把发送成功、协商编码、反馈或 PTP 包数当作出声证明。未测得声学延迟。

在 iPhone 对照成功后，独立引擎另以实际峰值 0.05（约 -26 dBFS）校准听音。第一遍用户未留意，记为无法判断；用户确认暂停 iPhone 后重播，反馈仍无声。两次报告分别为 `mini-node-audible-first-unjudged.json` 和 `mini-node-audible-comparison.json`；这不证明接收端已解码音频或采用发送端时钟。

原始试验分别见 `mini-probe.json`、`mini-alac-probe.json`、`mini-ptp-diagnostic.json`、`mini-follow-alac-probe.json`、`mini-anchor-alac-probe.json`、`mini-buffer-alac-probe.json`、`mini-volume-alac-probe.json`、`mini-ssrc-alac-probe.json`、`mini-node-comparison.json`。它们位于 `artifacts/validation/`，不提交设备资料或音频。

独立对照使用 [sonn-audio/node-airplay](https://github.com/sonn-audio/node-airplay/tree/a77dff7b9ec0a50dd126a2bd965e95360827ad86)，固定提交 `a77dff7b9ec0a50dd126a2bd965e95360827ad86`，Apache-2.0；源码归档 SHA256 `812f9e83360a0efacd4c059a8df68020329015c34d58575ec3d391afffd12c25`。它的 14 项自身测试通过，SRP 服务端证明校验保留。该引擎目前只用于诊断，没有作为产品依赖或替换原生引擎。

成功的参考试验使用 [iakacer/windows-airplay-homepod](https://github.com/iakacer/windows-airplay-homepod/tree/cc9a2fe4b564f29f68fc4ad15cad4c1544e853aa)，v0.1.2 固定提交 `cc9a2fe4b564f29f68fc4ad15cad4c1544e853aa`，GPL-2.0；归档 SHA256 `f0745af172e61beeb9472ea21a6fac4bbc5987f25ef8c0d602de6a2f84d107cc`。用已有 MSVC Rust 工具编译，仅在忽略的参考目录增加有限 WAV 示例；未启动其系统采集应用、安装 MinGW/驱动、保存持久配对或修改防火墙。

参考库完整配对套件为 153 通过、5 失败。失败来自未被实际连接调用的旧 `EncryptedChannel` 封装；未修改或隐瞒这些失败。实际连接直接调用 `ControlCipher`，相关加密检查 29 通过，排除整个旧封装后的配对检查 150 通过，严格 SRP M4 服务端证明校验保留。此参考源码没有并入 GPLv3+ 产品，不能以一次出声宣称其全部实现可靠。

实机报告为 `mini-rust-ntp-comparison.json`；只有收到用户“听到声音了”之后，才把 `acoustic_verified` 设为 true。这是 Windows 有限音源到 mini 的成功基线；真实 WASAPI 系统声音、当前 AppleHome 包和其他型号均未由此完成验收。当前证据也不足以断言 NTP 是唯一根因，两个引擎还存在编码和封包等差异。

随后，现有 AppleHome 原生引擎通过已有 NTP/ALAC 选项和 2 秒目标，同样得到用户出声确认，见 `mini-native-ntp-alac-probe.json`；原生 WAV 峰值与增益限制保持不变。200 毫秒目标重播无声，见 `mini-native-ntp-alac-default-probe.json`。本次产品修改只选择已有可听配置，没有并入第二引擎或再改原生协议算法；会话命令、实际目标缓冲、UI 状态和发现元数据更新后的重启均有回归。

## 首轮便携诊断包（审查修复前的历史记录）

代码构建提交：`5bd11dc814a134605cf94ed122c5d939e7b55977`。文件为本机 `dist/AppleHome.exe`，176,542,209 字节；SHA256：

`bae11f04699fa391d2571f0dbdf2fcfd2b421de53955e0d971c6a49f4a051b77`

嵌入的原生引擎 SHA256 为 `60d9b109dd77ed7cf19efe184dc163c9f7bc6570e3464b0bfa80826ac3b909ac`。便携包的 `--compat-check` 退出 0，返回 JSONL v1、native 模式、`live_loopback=true`，同时如实保留 `qualification=partial`、`production_ready=false`。便携包自身的 UI smoke 576 项通过。

单文件发布审核了 450 个映像，无阻断项，1555 个导入仍未由审核目录确认；这是静态审核，不等于全版本 Windows 认证。对应报告为 `artifacts/compatibility/bundle-audit.json`、`artifacts/validation/package-compat.json` 和 `package-ui-smoke.json`。交付目录包含 GPL 全文、来源说明、第三方声明和对应源码归档；未对外发布。

## 审查修复后的当前便携诊断包

代码构建提交：`497f7fa864a494ce93cfb3680eac247db6c389eb`。当前 `dist/AppleHome.exe` 为 176,548,865 字节，SHA256：

`b95aeb9c96493439b59f5e309833d7d30a935b6796e530447bc8a92cf30b665d`

嵌入的原生引擎 SHA256 为 `c76049e397e4d0056e91fc8442b4b0b5b800aa2737b683566903783c5e2173ea`。当前包自身的 `--compat-check` 退出 0、UI smoke 577 项通过；仍报告 `qualification=partial` 和 `production_ready=false`。发布静态审核仍为 450 个映像、1555 个未确认导入、无阻断项。

对应源码为 `dist/AppleHome-source-497f7fa.zip`，SHA256 `bc5c77186c399b70a762b11818fd766a47dbf9fb49436da909940de718b4b451`。精确来源和哈希另保存在 `dist/build-manifest.json`。该包替换了上面的历史包；本轮未通过它进行真实系统声音采集或宣称 HomePod 出声成功。

## 后续实机验收

iPhone 到同一 mini 的原生 AirPlay 已确认正常出声；两个早期 Windows 发送引擎无声，第三套独立 Rust 参考引擎已实际出声。Windows 真实系统声音、持续音乐、静音片段、视频偏移、断网重连、输出端点移除以及最终便携包出声均待验证。Windows 11、其他 HomePod 型号和立体声对也待实机验证。

三轮协议修正仍未解决无声后，用户已批准改用独立引擎进行实机对照。后续应先建立已知可听的源和设备基线，再决定继续修改哪个传输环节。
