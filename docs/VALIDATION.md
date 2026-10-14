# AppleHome 本地验证记录

日期：2026-10-14。当前结论：**软件检查通过，HomePod 实际出声失败，尚无可用性认证**。

## 已完成的软件与 Windows 检查

| 检查 | 结果 | 证据位置（本机忽略目录） |
|---|---|---|
| 完整 .NET Core 测试 | 242 通过 | `artifacts/validation/clock-core-final.log` |
| WPF UI smoke | 576 项通过；服务使用模拟实现，托盘与窗口在本机实际运行 | `artifacts/validation/mute-ui-final.json` |
| Rust 单元与本地网络测试 | 见原始日志的各套件；两个需要硬件或长时间运行的用例明确忽略 | `artifacts/validation/ptp-ssrc-native-check.log` |
| Clippy，警告视为错误 | 通过 | 同上 |
| 原生 release 构建与静态 CRT 检查 | 通过 | 同上 |
| 原生导入审核 | 无阻断项；仍有 144 个未由目录确认的导入，不能外推所有 Windows 10 构建 | `artifacts/compatibility/engine-audit.json` |
| 真实 Windows 静音归属与恢复 | 8 项通过，见下文 | `artifacts/validation/audio-ownership.json` |

静音检查使用实际 `AudioService` 程序集、NAudio 2.2.1 和本机默认播放端点，使用独立恢复文件。检查了：应用静音落盘原状态；外部解除静音后撤销恢复所有权；之后外部重新静音时停止不覆盖它；受控子进程静音后直接退出，构造新服务不改声音，显式启动恢复才恢复原状态；音量标量未被改写。执行后端点回到测试前的静音状态。此检查没有采集系统声音或麦克风。

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

上述试验为预生成的 44.1 kHz、16-bit 双声道信号；源峰值 0.05，增益 0.1，实际峰值约 0.005（约 -46 dBFS），因此听音检出能力也需要校准。没有把发送成功、协商编码、反馈或 PTP 包数当作出声证明。未测得声学延迟。

原始试验分别见 `mini-probe.json`、`mini-alac-probe.json`、`mini-ptp-diagnostic.json`、`mini-follow-alac-probe.json`、`mini-anchor-alac-probe.json`、`mini-buffer-alac-probe.json`、`mini-volume-alac-probe.json`、`mini-ssrc-alac-probe.json`、`mini-node-comparison.json`。它们位于 `artifacts/validation/`，不提交设备资料或音频。

独立对照使用 [sonn-audio/node-airplay](https://github.com/sonn-audio/node-airplay/tree/a77dff7b9ec0a50dd126a2bd965e95360827ad86)，固定提交 `a77dff7b9ec0a50dd126a2bd965e95360827ad86`，Apache-2.0；源码归档 SHA256 `812f9e83360a0efacd4c059a8df68020329015c34d58575ec3d391afffd12c25`。它的 14 项自身测试通过，SRP 服务端证明校验保留。该引擎目前只用于诊断，没有作为产品依赖或替换原生引擎。

## 尚未完成

iPhone 到同一 mini 的原生 AirPlay 对照正在等待用户结果。Windows 真实系统声音、持续音乐、静音片段、视频偏移、断网重连、输出端点移除以及最终便携包出声均待验证。Windows 11、其他 HomePod 型号和立体声对也待实机验证。

三轮协议修正仍未解决无声后，用户已批准改用独立引擎进行实机对照。后续应先建立已知可听的源和设备基线，再决定继续修改哪个传输环节。
