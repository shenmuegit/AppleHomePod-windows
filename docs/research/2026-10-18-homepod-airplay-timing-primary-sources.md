# HomePod AirPlay 时间基准：primary 证据与迁移边界

查询日期：2026-10-18（Asia/Shanghai）。外部源码均固定 commit；本文只记录调查，不修改发送协议，也不构成硬件延迟资格。

## 本次本机证据

- 用户报告：已安装 d92 原生 500ms 候选播放音乐仍感觉约 4 秒；这是主观近似值，500ms WAV 此前只确认可听，没有声学计时。
- 主线在 f6f472b 的一次连接快照记录 `capture_to_send_p95_ms=1190.20274`、`max_queue_age_ms=1191.401637`、`underrun_packets=0`、`dropped_frames=0`。WASAPI QPC×100 与发送侧 `qpc_ns()` 同基准，可确认约 1.19 秒本机积压；不能据此精确分配“4秒减去1.19秒”的剩余部分。
- 原始位置：`native/airflash-engine/src/live.rs:256-312`；年龄统计来自最近最多 4000 个已消费帧，44100 Hz 下约 91ms 的音频，不是整个会话的声学 p95。主线现场日志与用户反馈是本条数据来源，本文没有原始录音。

## 固定版本

| 项目 | 本轮核对 commit |
| --- | --- |
| OwnTone | `3aee59740c715d40df8de87e06b7b91badfbe20b` |
| Shairport Sync | `cf6998daa1876c5a8f97128a960c8afc0e731841` |
| pyatv | `b277a4c8222ecdcbaab8a24e3e713ca44765adb4` |
| Preroll | `9e4a039cf6eb27a9486264f21c6ff870dc6eb537` |

## OwnTone：SETUP 范围与实际时间基准是两层

- **事实**：AP2 `airplay.c` 使用 type 96 实时流；SETUP 为 `latencyMin=11025`、`latencyMax=88200`，即 44100 Hz 下 250..2000ms。[O3]
- **事实**：`start_buffer_ms` 默认 2250；实际 `output_buffer_samples=(start_buffer_ms-250)*sample_rate/1000`，不是直接采用 SETUP 最小值。[O1][O4]
- **算法**：保存播放器提供的采样时间 `ts`；该时刻应播放的 RTP 位置为 `send_pos + input_buffer_samples - output_buffer_samples`。NTP sync 的第一个 RTP 字段写此播放位置，第二个写发送位置，NTP 字段写对应 `ts`。[O2][O5]
- **算例**：在无未发送余量时，默认 2250ms 配置产生 88200 帧的显式时间锚点差；配置 500ms 则产生 11025 帧差。两个算例仍带 SETUP min=11025。这里只说明发送器算法，不承诺 HomePod 会再精确叠加 250ms。
- **边界**：该 AP2 实现本身对 latency 字段仍有 TODO。它证明“SETUP min 低”不等于实际渲染延迟低；不能把其旧设备兼容常量当现代 HomePod 的硬件下限。

## Shairport Sync：区分 AP1、AP2 实时与 buffered

- **事实**：官方 AP2 文档将实时流描述为通常约两秒，buffered 流可更快启动并允许数据先于播放到达。这是该实现的观察，不是 Apple 发布的 HomePod 最低延迟规范。[S1]
- **实现**：AP2 type 96 使用 UDP；type 103 分支建立 TCP 数据口并配合 `SETRATEANCHORTIME` 时间锚点控制。不能只把本引擎的 `type=96` 改为 103。[S2]
- **事实**：AP2 `handle_record_2` 回 `Audio-Latency: 0`；相邻 AP1 `handle_record` 才回 11025。没有证据支持给我们的 AP2 NTP 路径无条件添加一个“额外两秒 Audio-Latency”。[S3]
- **界限**：本 commit 的 AP2 SETUP 处理代码没有读取 `latencyMin/latencyMax`；AP1 的最小/最大延迟钳制不能直接推广到 HomePod 的 AP2 NTP。未找到 Apple 公开的强制遵守合同，也未读到 HomePod 闭源实现，故不能断言这些字段对 HomePod 是强制值或一概只是 advisory。

## pyatv：AP2 NTP 也有独立的发送时间策略

- AP2 实现使用 NTP、type 96，SETUP min=11025/max=88200；共享 `StreamContext` 另将 `latency` 设为 `22050+sample_rate`，44100 Hz 时是 1.5 秒。[P1][P2]
- 共享 control client 发 NTP sync：第一个 RTP 字段是 `rtptime-latency`，第二个是 `rtptime`，flags 固定 `0x0007`。[P3]
- **可推断**：请求范围、发送器选择的播放时间线、接收器实际行为必须分开。此源码支持检查时间锚点一致性，但没有在本次 HomePod 上测过，不能据此立即改 NTP 或保证降延迟。

## Preroll：350ms 的证据等级与 Windows 适用性

- **作者记录**：macOS 26.5.1/Apple Silicon 的默认 stream latency 为 88200 帧；`audioLatencyMs=350` 的日志显示实时引擎使用 350ms、最小值 250ms、adjust=-250ms，并报告 `arrivalToRenderLatencyMs=84`。84 是 Apple 发送器日志值，不是我们测出的扬声器声学延迟。[K1]
- **作者声明**：350ms 是其测试网络最低稳定设置。[K2] 本 commit 的仓库没有发布对应的原始录音、逐次 acoustic burst 输出或数据集；不能将“350ms 稳定”改写为“已独立复核端到端350ms”。
- **已有原始数字**：NOTES 的启动实验为 cold：`engine.start=368.8ms`/首 render callback=`377.9ms`；hot：`95.6/105.9ms`；cold control：`359.5/368.5ms`。它们测的是引擎启动/回调，不是 HomePod 发声时间。[K2]
- **测量工具算法**：`aplat.swift` 在输出源回调首次加入 tone 时取 `mach_absolute_time()`，麦克风用 `when.hostTime+块偏移` 标记检测；以 3kHz、128采样块的 Goertzel 幅度阈值识别 80ms burst，输出两时间差及 mean/min/max。工具存在不等于上面的 350ms 声学结果已公开。[K3]
- **移植边界**：Preroll 通过 `defaults` 设置 Apple 私有 sender 的 `audioLatencyMs` 并重启 `AirPlayXPCHelper`；它不 patch 二进制，也没有实现独立的开源 AirPlay 发送协议。Windows/Rust 不能迁移这个偏好开关；可借鉴声学测量和发送策略/接收需求的分层。[K4]
- **版本边界**：同一作者又记录 macOS 27 的 buffered realtime 系统音频引擎，报告 48000 Hz/9600帧=200ms；README 明说尚未声学测量。不要把旧版 NOTES 的“live 永远不能 buffered”当协议禁令，也不要把新的200ms报告当本机实测。[K5]
- 本项目若复用检测思路，仍须沿用安静 WAV 的峰值约5%边界；原工具输出峰值0.30，不能原样套入本项目听音实验。[K3]

## 当前引擎、独立参考与下一步证据

- `native/airflash-engine/src/rtp.rs:153-185` 的 NTP 分支把两个 RTP 字段都写成 `T`，传入 `latency_samples` 对 NTP 无作用；本地参考 `windows-airplay-homepod-cc9a2fe4b564f29f68fc4ad15cad4c1544e853aa` 的 `crates/airplay-audio/src/rtp.rs:634-655` 也如此。参考的 SETUP 是 min500/max2000，额外 `render_delay_ms=0`，所以其可听不构成低延迟对照。
- 两者均先 RECORD 再 FLUSH，且 RTP-Info 与初始数据 timestamp 一致；Native 随机起点、参考零起点本身没有证明秒级延迟。Native 在 gate 关闭时还会反复发送同一 RTP 位置、更新 NTP 的 start sync；这与参考只随数据发首个 start sync 不同，但尚无因果证据。
- **已纠正**：NTP 字节2..3在 OwnTone、pyatv中同样固定为7；此前将其与参考递增值相比称为必须递增的 sequence 不准确，不能据此改码，也不由 AP1 的 flags 解释推定 AP2 的全部语义。[O5][P3]
- **当前决策**：先保留 NTP 锚点。增加正 latency offset 可能增加正常接收器的显式缓冲，尚不能解释或修复约4秒。
- **单变量验证**：先给同一安静 WAV 的500ms试验做真实声学计时，记录 first_send 与声学触发的共同时间基准；再只改 SETUP latency_ms 比较500/2000。有限 WAV 没有 armed queue，能隔离本机1.19秒积压；只确认可听不够。
- 同时保存可得的数字回包：SETUP `latencyMin/Max`、RECORD `Audio-Latency`、`/info` 的 `audioLatencies`。这些是报告/协商值，应与声学测量并列，不应相加后直接宣布硬件下限。保留首段和门控语义的积压修复由主线另行验证。

## 固定源码链接

[O1]: https://github.com/owntone/owntone-server/blob/3aee59740c715d40df8de87e06b7b91badfbe20b/src/outputs/airplay.c#L1196-L1207
[O2]: https://github.com/owntone/owntone-server/blob/3aee59740c715d40df8de87e06b7b91badfbe20b/src/outputs/airplay.c#L2130-L2154
[O3]: https://github.com/owntone/owntone-server/blob/3aee59740c715d40df8de87e06b7b91badfbe20b/src/outputs/airplay.c#L2622-L2633
[O4]: https://github.com/owntone/owntone-server/blob/3aee59740c715d40df8de87e06b7b91badfbe20b/src/conffile.c#L79
[O5]: https://github.com/owntone/owntone-server/blob/3aee59740c715d40df8de87e06b7b91badfbe20b/src/outputs/rtp_common.c#L309-L343
[S1]: https://github.com/mikebrady/shairport-sync/blob/cf6998daa1876c5a8f97128a960c8afc0e731841/AIRPLAY2.md#L5-L7
[S2]: https://github.com/mikebrady/shairport-sync/blob/cf6998daa1876c5a8f97128a960c8afc0e731841/rtsp.c#L2791-L2861
[S3]: https://github.com/mikebrady/shairport-sync/blob/cf6998daa1876c5a8f97128a960c8afc0e731841/rtsp.c#L903-L933
[P1]: https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/protocols/airplayv2.py#L61-L141
[P2]: https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/protocols/__init__.py#L23-L57
[P3]: https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/stream_client.py#L108-L119
[K1]: https://github.com/ksha23/preroll/blob/9e4a039cf6eb27a9486264f21c6ff870dc6eb537/NOTES.md#L4-L38
[K2]: https://github.com/ksha23/preroll/blob/9e4a039cf6eb27a9486264f21c6ff870dc6eb537/NOTES.md#L81-L105
[K3]: https://github.com/ksha23/preroll/blob/9e4a039cf6eb27a9486264f21c6ff870dc6eb537/src/aplat.swift#L57-L151
[K4]: https://github.com/ksha23/preroll/blob/9e4a039cf6eb27a9486264f21c6ff870dc6eb537/helper/preroll-helper.sh#L24-L60
[K5]: https://github.com/ksha23/preroll/blob/9e4a039cf6eb27a9486264f21c6ff870dc6eb537/README.md#L16-L26
