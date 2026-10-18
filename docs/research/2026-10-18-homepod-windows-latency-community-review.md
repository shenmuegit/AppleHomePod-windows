# Windows / HomePod 延迟：社区实现与后续修正依据

调研日期：2026-10-18。对象是当前 Windows 10、单只 HomePod mini / 27.0，以及已安装的原生输出候选。保留用户已批准的 Windows 输出选择与音量入口，不增加日常面板；断网测试继续暂缓。

## 当前证据

| 项目 | 实际取得的证据 | 不能据此推断 |
| --- | --- | --- |
| 500 ms WAV | 同信号、同音量的五秒短音，用户确认可听 | 实际总延迟为 500 ms |
| Native 500 ms | d92d33f 发送参数和运行日志均为 500 ms；用户仍感到约 4 秒延迟 | 接收器实际采用了请求值，或 4 秒全来自 HomePod |
| 采集到发送 | f6f472b 首次指标 p95=1190.20274 ms，max=1191.401637 ms，underrun=0，dropped=0 | 这是整段播放的长期分布，或可以与用户粗测直接相减得到接收端延迟 |
| 当前应用 | 安装 SHA256 `a9e1ab74ebeb75b80f0a041fa4bdbe02829a8c11501d389d5aa190959bc18f68`；owned/ready=true；369 项 .NET 通过 | 音乐低延迟已经验收 |

以上来自本机 `artifacts/validation/native-output/latency-20261018/` 与应用首次采集指标日志，不是社区结果。f6f472b 仅增加每次连接一次的数字诊断，发送目标仍为 500 ms，不保存 PCM。

## 1. 最相近的 Windows 实现：AirPlayer

[AirPlayer airplay.py](https://github.com/SabiQG/AirPlayer/blob/f62cce8c0ebc723cf09d682c43b7e5bf705f0592/airplayer/airplay.py) 对实时输入的永久积压有明确处理：超过约 350 ms 时丢弃较旧音频，收敛到约 100 ms；同时改 pyatv 的 StreamContext.latency，并记录实际 backlog。它的做法同时涉及发送时序和源队列，并非只改 SETUP 数字。

迁移判断：其问题描述与本机约 1.19 秒启动积压吻合，是应优先检查我们队列策略的直接线索。不能照抄丢旧帧，因为本项目已承诺保留握手期间短音；需要区分启动首段与稳定运行，并明确积压回收、静音清理和 epoch 边界。该项目通过 monkey patch 访问 pyatv 内部状态、直接操作 StreamReader 私有缓冲，适合参考行为，不适合原样引入到现有 Rust/.NET 架构。

[AirPlayer volume.py](https://github.com/SabiQG/AirPlayer/blob/f62cce8c0ebc723cf09d682c43b7e5bf705f0592/airplayer/volume.py) 读取虚拟端点的 Windows 主音量和静音，转为接收器音量操作；[set_volume](https://github.com/SabiQG/AirPlayer/blob/f62cce8c0ebc723cf09d682c43b7e5bf705f0592/airplayer/airplay.py#L145) 调用设备音量接口。它把未衰减的虚拟 PCM 与设备音量控制分开。本机校准同样得到 MasterInPcm=false，因此值得独立设计这条控制路径。

限制：README 对默认延迟写了不同数字，源码为 400 ms；没有取得该版本在 mini / 27.0 的声学测量记录。不能将其“最低 250 ms”文案当成所有接收器的保证。

## 2. 成熟协议参考与两种流类型

[Shairport Sync 的 AirPlay 2 说明](https://github.com/mikebrady/shairport-sync/blob/master/AIRPLAY2.md) 区分 realtime 和 buffered；它是接收器实现与逆向观察，不是 Apple 发布的协议规范。其中“通常两秒”不能改写为所有 HomePod 的不可突破下限。

[OpenAir 的管线说明](https://github.com/Antoinenz/OpenAir/blob/f874469faf8228f4620abc3cfd3fecc19907bb0a/docs/audio.md) 展示另一种实现选择：type 96/UDP/ALAC 和 type 103/TCP/AAC 分别有不同的数据管线。它还用有界重采样速率调整回收积压、补偿时钟漂移，并说明这需要时间，不能瞬间消除秒级积压。

迁移判断：可参考控制环和时序分层。type 103 需要不同传输和播放锚点，不能只把类型字段从 96 改为 103。也不能把该项目“ALAC 固定两秒”的概括当成规范：其[硬件状态](https://github.com/Antoinenz/OpenAir/blob/f874469faf8228f4620abc3cfd3fecc19907bb0a/STATUS.md)明确 HomePod 未测试，其[Windows 集成](https://github.com/Antoinenz/OpenAir/blob/f874469faf8228f4620abc3cfd3fecc19907bb0a/docs/windows.md)的无本地声及原生音量方案可作架构对照，不能作为本机资格。

更细的 OwnTone AP2 时间锚点、NTP 字段及 Preroll 测量边界见[协议原始资料核对](2026-10-18-homepod-airplay-timing-primary-sources.md)。当前不根据 AP1 公式直接修改 AP2 的时间锚点。

## 3. 社区实机报告的价值与局限

[Playfruit](https://github.com/bartleman/playfruit/blob/2dcef9f29b42e582f4cbfdcf9edafbb618cc35c0/README.md) 报告在 mini 和二代 HomePod / 26.6 验证，约 450–500 ms 来自其 click_cli 的非正式听感测量。它说明更低延迟有实际实现线索，但固件与我们不同，测量精度也不同。其默认镜像物理输出、静音本地扬声器的产品路线不符合本项目已批准的独立输出交互，不整体替换。

[Preroll](https://github.com/ksha23/preroll) 是 macOS 自有发送栈的调优项目；应借用其区分“驱动报告值、接收器报告值、声学测量”的方法。不能把 macOS 的私有 audioLatencyMs 偏好移植为 Windows 设置，也不能把新系统报告的 200 ms 写成本机实测。

[windows-airplay-homepod 的 JITTER.md](https://github.com/iakacer/windows-airplay-homepod/blob/master/JITTER.md) 保留了请求 500 ms、设备报告 arrivalToRenderLatencyMs=70 的原始调查。该测试来自 Raspberry Pi / Linux，并非该仓库 Windows 程序的正式验收；作者对定时器与接收缓冲的归因也不等于规范。它支持继续区分请求、报告、发送抖动和声学结果，不能证明本机的总延迟是 70 ms。

[AirFlash 作者的社区发布及跟帖](https://www.reddit.com/r/HomePod/comments/1wpq494/windows_homepod_os_27_lowlatency_airplay_2/) 有 OS 27 用户的可用反馈，可用于找兼容性线索；它不含受控测量，不能覆盖我们的 mini 27.0 实测。其[上游发布说明](https://github.com/Ding-Kyoma/AirFlash/releases)也明确目标值不是端到端声学测量，仍列出间歇音频、超时等未解决项。

## 4. 原生音量的正确边界

[Microsoft Endpoint Volume Controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls) 提供端点音量/静音回调和事件 context，适合继续复用现有监听与自身写入识别；不需要新的控制面板。主音量、应用会话音量与设备音量是不同环节。

[GetMasterVolumeLevelScalar](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-getmastervolumelevelscalar) 明确 scalar 是非线性的 audio-taper 曲线，不能直接当线性振幅。把 Windows 的 30% 直接写成 HomePod 的 30%，也不等价于当前 dB 单次增益模型。

建议：在已确认 MasterInPcm=false 的驱动上，将主音量/静音作为独立接收器控制来设计，应用混音器仍由 Windows 处理；根据 dB 和参考音量校准映射，避免 PCM 与接收器重复衰减。保留最新用户意图、远端回读、静音恢复及零音量边界，不能仅为响应速度取消这些保护。此处是设计修正建议，当前应用尚未这样改。

实时发送可继续使用[微软 MMCSS](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service)与绝对包截止时间。本项目已有相关基础；线程优先级只解决调度抖动，不能消除已存在的秒级队列，不能拿提高线程优先级代替时序修正。

## 后续实施顺序

1. **分离音量控制与音频排队。** 先回归虚拟端点 dB、设备回读、静音/恢复和单次衰减，目标是使用户操作能作用于已缓冲声音；不要再以降低 PCM gain 后等待队列清空作为最终体验。
2. **修正启动后积压策略。** 保留首次短音，在稳定播放阶段建立明确队列年龄目标；比较丢旧帧、静音段回收和轻微速率纠偏的取舍。首段完整与立即追实时存在约束，不能偷偷清空队列冒称兼得。
3. **核验 AP2 播放时间锚点。** 对照 OwnTone 与现有参考发送器，核对 RTP 位置、NTP/PTP 时间和实际接收字段；每次只验证一个时间关系，保留可听基线。
4. **取得共同时间基准的延迟证据。** 分别记录首次出声、稳态播放和音量/静音响应；请求值、软件队列、接收器报告和声学结果各自记录。声学录制如需麦克风，须另有明确授权，当前没有录音。
5. **低延迟路线通过后做持续播放。** 仍按已批准的 30 分钟窗口检查漂移与恢复；网络断开保持暂缓。其他型号、Windows 11 与立体声不扩大资格。

本轮社区调查没有安装或运行其他项目、改变驱动/防火墙、绕过接收器身份检查或发起新的测试音。当前低延迟问题仍未解决，500 ms 只是请求目标。
