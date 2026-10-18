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
