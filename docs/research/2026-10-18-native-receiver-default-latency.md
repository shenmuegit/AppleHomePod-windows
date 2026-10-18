# Native 自动缓冲：省略请求值的互通试验

日期：2026-10-18。用户要求不对所有设备写死100 ms，并检查50 ms无声、能否省略参数尽快发送。本次是现有原生输出路径的有界修正；保留 Windows 原生输出/音量/静音，不做断网、驱动修改或声学录音。

## 已确认的50 ms对照

- 已通过的100 ms包为 `c19753a`，内嵌B623引擎。纯50 ms包 `d4bf79a` 的EXE SHA256为 `f7ee45eb6cc9615876fae40dd648c0c71bbca3f35e4a7711e13adca3a51eaf1c`，保留同一B623，仅修改 Native 请求值。用户对同页两声测试再次反馈“仍然没有声音”。已恢复原100 ms包，未把无声候选留作日常后台。
- 因而50 ms失败不需要此前E7的缓存修正才能出现。没有发现发送器在低于100 ms时主动静音；目前仍不能认定某个硬件极限或唯一根因。
- 原来发送 `latencyMin=latencyMax=2205`（50 ms、44100 Hz），接收端没有选择范围；100 ms则为4410。日志50 ms是估计值，不是HomePod确认的声学播放延迟。需要区分接收端窗口/初始化时序与本机零输出，新增的增益后非零PCM成功发包计数只提供后者的数字证据。

## 原始实现证据

- OwnTone和pyatv的实时type96都显式发送250..2000 ms范围；airplay-cli也只在实时分支发送这两个字段，buffered type103省略字段并采用另一套播放时间锚点。没有Apple公开合同证明当前type96省略后即零缓冲或最快播放。[OwnTone](https://github.com/owntone/owntone-server/blob/3aee59740c715d40df8de87e06b7b91badfbe20b/src/outputs/airplay.c#L2622-L2634)、[pyatv](https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/protocols/airplayv2.py#L125-L143)、[airplay-cli](https://github.com/music-assistant/airplay-cli/blob/1701bc25e9d303b42532569f624b64c0d0afb1f6/src/ap2_client.c#L2192-L2202)。
- 接收端有时返回latencyMin/Max或arrivalToRenderLatencyMs，不能保证存在，也不能将最小值或send成功当实测播放进度。当前feedback没有提供已核对的声学游标，不能据此自动试遍档位并宣布可听。[airplay-cli返回值处理](https://github.com/music-assistant/airplay-cli/blob/1701bc25e9d303b42532569f624b64c0d0afb1f6/src/ap2_client.c#L2264-L2299)、[feedback边界](https://github.com/music-assistant/airplay-cli/blob/1701bc25e9d303b42532569f624b64c0d0afb1f6/src/ap2_client.c#L4743-L4759)。
- 当前实时发送每352帧一包，44100 Hz下约7.98 ms；100/50 ms不决定每包sleep。不能提前取得未来的系统音频；去掉实时节奏可能快速补零并让时间戳跑向未来。[pyatv发送循环](https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/stream_client.py#L481-L534)。

## 这次实现与验收边界

Native向引擎传入null请求值，SETUP真正省略latencyMin/Max；旧显式数值包括0继续发送对应字段，不把0偷换成自动。只允许NTP省略，PTP继续要求显式同步偏移。现有采样时钟、RTP/NTP锚点、音量校准、门控和首段保留规则不改。

若接收器返回有效延迟参数则保存为设备回报参数；完全未知则metrics返回null，不伪报100/150或零延迟。未知时的一秒窗口仅用于既有重传缓存，不增加播放等待。非零PCM指标只累计原始UDP发送成功且增益后PCM含非零的包，不保存音频、不证明接收端实际播放。

这是互通候选，省略参数后的实际可听性、延迟和视频效果必须实机确认。接收器默认策略可能较慢；不把省略等同于最优，不通过重传计数盲目调低参数。当前设备已有100 ms已听音回退，其他设备/固件、完整长播和断网未取得资格。

## 随后实测与字段语义核对

早期信号过轻，三段连播又无法让用户确认听到的是哪段，两类反馈不作为有效的逐项对照。改成每次一个条件、同一引擎 `59d11d2c1404bb20ce546a1fc3d0105b799cca084f01abbdf5570b00a3d209e9`、同一997Hz双短音/峰值0.05/5秒，并在播放前用全零音频确认设备72%音量，结束恢复原51%与原后台：固定50 ms无声；50..100 ms无声；固定100 ms明确听到两声。省略字段的有限短音收到“听到了，但几分钟前”的延迟反馈，保留时间归属不确定性；随后实际Native省略版本的连续Windows音乐仍被用户明确反馈为无声，已恢复原可听包。

这四种SETUP的实际回包都包含 `arrivalToRenderLatencyMs=70`，而 `latencyMin/Max` 均未返回。此值先前已由协议事件收集，现在完成核对；不能把“未返回最小/最大值”写成“完全没有任何延迟相关字段”。`GET /info`仍没有相关字段，两处并不矛盾。非零计数证明发送侧未全零，不证明接收播放；50..100与100..100的结果将差异收窄到最小请求相关路径，不等于固件给出了拒绝原因或100 ms硬件下限。

社区字段定义区分三层：SETUP的latencyMin/Max是采样帧数；发送器自己的lead/时间锚点是单独的调度状态；arrivalToRenderLatencyMs是毫秒单位的设备报告呈现延迟。44.1kHz下50/100/250/2000 ms分别为2205/4410/11025/88200帧。[社区逆向字段表](https://github.com/lmcgartland/airplay2-rs/blob/master/AIRPLAY_2_SPEC.md#532-phase-2-response-fields-observed)不是Apple发布的规范，成熟源码是额外互证。

[windows-airplay-homepod作者的JITTER记录](https://github.com/iakacer/windows-airplay-homepod/blob/master/JITTER.md)也观察到请求500 ms、设备返回70 ms，并将70解释为缓冲；其测试是树莓派/Linux，不能直接推广到本机。相较之下，[airplay-cli DESIGN的呈现延迟说明](https://github.com/music-assistant/airplay-cli/blob/1701bc25e9d303b42532569f624b64c0d0afb1f6/DESIGN.md#6-timing-and-the-start-contract)明确将该字段当信息值，不再次叠加到调度，以免重复补偿。不能据70推导所有HomePod最低70，不能推导50失败的唯一固件原因，也不能直接按70加网络RTT算出一个通用100。

## 实现收口：使用设备自己的已验证值

失败的Native省略默认不留作生产默认。`NativeBinding.VerifiedBufferMs`将听音通过的请求值与已有ReceiverId、端点、驱动/格式校准一起保存；Native运行时从该记录读取，不在代码统一写100。旧记录没有该字段时保留未知并拒绝无依据启用；既有首次音量参考校准在明确听音确认后，可保存那次实际使用的2000 ms参考值，后续保留已经验证的更低值。当前mini已验证100 ms，可迁移为它自己的记录。驱动/混音格式使音量校准失效时同时清除该值。

这是按设备保存已知可听参数，尚不等于自动找到最小声学延迟。原生型号/固件资格边界继续保留，不因为字段可配置就宣布其他设备通过。省略参数和独立min/max的引擎能力保留为有测试的协议能力，实际日常入口使用设备记录。

独立审查发现并修复一项范围模式的恢复问题：无接收器回报时，50..100 ms不能把下界50当作实际重传截止。回归先得到Some(50ms)而期望未知，再使非退化范围走已有未知延迟缓存策略；固定值、显式0和日常记录值保持原行为。原先无声对照全程没有重传请求，此问题不解释那次无声，也没有改变其初始发送内容或节奏。
