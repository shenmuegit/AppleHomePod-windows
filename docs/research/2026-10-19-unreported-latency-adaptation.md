# 接收端不回报边界时的设备适配

用户要求：没有设备记录且HomePod不返回缓冲边界时，尽量使用低延迟且稳定的参数；不把250ms变成统一固定值，不提供参数输入，不反复播放短音。正常音乐和Windows原生控件用于确认，断网暂缓。

## 社区核对

本次核对的Music Assistant CLI为 `1701bc25e9d303b42532569f624b64c0d0afb1f6`，server为 `d941b89dbe8795443cf16f97f8406f5b37f85b9f`。它保留按设备厂商、型号、固件匹配缓冲经验的机制；当前表因播放路由变化已清空，因此不能把历史某项设备值推广成所有HomePod的默认值。[匹配逻辑](https://github.com/music-assistant/server/blob/d941b89dbe8795443cf16f97f8406f5b37f85b9f/music_assistant/providers/airplay/helpers.py#L197-L217)、[配置表说明](https://github.com/music-assistant/server/blob/d941b89dbe8795443cf16f97f8406f5b37f85b9f/music_assistant/providers/airplay/constants.py#L137-L147)。

其源码明确区分反馈保活和实际出声：可听设备可能回空stream列表，静音设备也可能回非空列表。HTTP200、已发非零PCM、无重传或无本地错误，都不是实际渲染证明。[feedback语义](https://github.com/music-assistant/airplay-cli/blob/1701bc25e9d303b42532569f624b64c0d0afb1f6/src/ap2_client.c#L4743-L4788)。本项目此前50ms无声而100ms可听、传输均健康的实测，也不支持用保活自动下探最低值。

OwnTone的250ms是历史设备经验下的猜测；pyatv使用固定初始延迟和反馈保活，也没有提供无声学反馈的自动最低可听值算法。[OwnTone常量](https://github.com/owntone/owntone-server/blob/3aee59740c715d40df8de87e06b7b91badfbe20b/src/outputs/airplay.c#L90-L94)、[pyatv流上下文](https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/protocols/__init__.py#L20-L51)。本轮参考这些实现的适配机制，不复制其播放队列、PTP路由或统一时延。arrival-to-render信息仍不用于推导最低缓冲。

## 本轮选择与确认

- 本设备已有且上下文匹配的听音参考优先，当前手工100ms记录保持。新的记录绑定稳定设备ID、型号、固件、分组、时钟/编码路径和采样率指纹；改名、DHCP换址不使记录失效。旧JSON可以读取，不自动重写。
- 没有有效记录且接收端无正数边界时，仅对已实测的单只mini27.0/NTP-ALAC/44100路径给出100ms型号候选，来源 `model_profile`。它不是新设备已验证值，也不宣称硬件最低值。没有型号匹配时，250ms仅为一次兼容候选。
- 首轮SETUP和合法接收边界仍按原算法处理。型号或恢复候选必须通过第二轮固定SETUP实际应用，原人工参考保持null；正数接收边界优先，不被型号或恢复提示覆盖。
- 向导保留一次正常音乐正向确认。只有同设备/目标/采样率的当前Streaming会话、真实AppliedMs、至少5秒及非零PCM和有效传输证据，才可保存实际值与指纹；健康指标本身不保存记录，也不触发降档。
- 用户明确选择“没有声音或有断续，自动调稳”时，才允许在同一次向导预览中最多两次上调实际候选。复用测得的校准与绑定，不重复校准或短音，不改Windows默认输出；成功后只保存该设备正常音乐确认的值。
- 已恢复的历史错误不会永久阻止完成：有历史累计错误或重连时，正向确认会在同一候选、host/session/capture epoch上等待后续5秒窗口，核对无新错误、计数不回退、包和非零PCM继续推进。不会为清空历史而重连或提高缓冲。

这实现的是有来源的起点、有界恢复及每设备确认记录，不是无需反馈就能测得最低声学延迟。不会因为短期发送健康而自动降到50ms、扩大其他型号资格，或把网络故障永久改成其他协议。

软件回归、临时无历史实机验证与交付状态另记入[原生输出验收记录](../validation/windows-native-homepod-output.md)。
