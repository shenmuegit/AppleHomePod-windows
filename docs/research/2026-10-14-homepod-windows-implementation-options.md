# Windows → HomePod：实现路线调研与设计讨论稿

调研日期：2026-10-14（北京时间）。本文是选型依据和设计讨论稿，尚不是经过用户审阅的 Superpowers 实施规格，也不代表产品已实现或已通过实机验收。

## 已确认的目标

- 将 Windows 系统声音，以及音乐、浏览器和视频播放器的声音发送到 HomePod。
- Windows 10 与 Windows 11 都是目标平台；首轮以 x64 为基线。
- HomePod mini、第 1 代和第 2 代均纳入兼容目标。用户目前有 HomePod mini 可用于验收，固件版本尚未提供。
- 用户接受 GPL 开源，优先复用现有引擎。
- 当前 `G:\code\applehome` 原为空目录，没有现有代码、Git 仓库或启动方式。

用户已在本轮确认采用 AirFlash GPL 原生结构的设计方向，并继续形成正式规格；这不等于已经审阅后续书面规格或实施计划。

新增型号时效说明：Apple 中国于 2026-10-13 公告 HomePod mini 2，计划 2026-10-30 发售。它加入后续兼容目标，目前不能把旧 mini 的协议与实测结果外推到新型号，也不能承诺上市即支持。[Apple 官方公告](https://www.apple.com.cn/newsroom/2026/10/apple-announces-the-next-generation-of-homepod-mini/)

现有立体声对与多房间是不同问题。建议保留基础项目对已有立体声对的能力，先让单只 mini 跑通，再对双机配置单独验收；不能把单机成功外推为立体声或多房间同步成功。

## 证据的含义

以下分别注明官方文档、源码观察、维护者报告和工程推论。本次已查询网页、仓库源码及公开元数据，未安装或运行任何发送端，未连接 HomePod，也没有测量实际出声延迟。仓库标题中的 “verified” 或 README 的功能列表不算我们的验收证据。

## 系统音频发送需要解决什么

Microsoft 提供 WASAPI loopback，可以采集指定播放端点的混合声音；事件驱动 loopback 从 Windows 10 1703 起得到支持。它针对共享模式播放流，不能承诺捕获独占模式或受保护内容。这里的“系统声音”是被选中输出端点上的声音；应用若播放到另一个端点，需要选对采集源。[Microsoft：Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)

设备发现可以使用 Windows 自带的 DNS-SD API，官方最低客户端要求为 Windows 10 桌面应用。实际还要检查 API 可用性、网卡选择、mDNS 与 UDP 通路。[Microsoft：DnsServiceBrowse](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicebrowse)

据现有发送端源码，采集后的数据还要经过采样格式转换、配对与接收端验证、加密控制通道、音频封包、时钟同步、反馈与重传、停止清理。这些是引擎职责，单独画一个设备列表不能完成声音发送。[AirFlash 引擎说明](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/README.md)、[会话源码](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/src/session.rs)

普通 loopback 镜像会保留本机声音，声音经过网络后可能形成回声。如果需要 HomePod 出现在 Windows 的系统输出选择器中，现有 AirPlayer 采用 VB-Cable 虚拟音频驱动；这是额外的驱动安装与声音路由工作。建议第一版在应用里选择 HomePod，利用现有端点采集，不默认安装虚拟驱动。[AirPlayer：工作方式与两种采集模式](https://github.com/SabiQG/AirPlayer#how-it-works)

## 开源候选核查

| 候选 | 已核对的证据 | 不能外推的部分 | 选型判断 |
| --- | --- | --- | --- |
| AirFlash | WPF/.NET + Rust；已有 WASAPI、Windows DNS-SD、HAP、PTP/NTP、PCM/ALAC、托盘与恢复逻辑；GPLv3+ 或商业许可 | Windows 10 兼容修复仍是候选；引擎自己报告部分认证、非生产就绪；ALAC 和持久配对的测试不等于所有 HomePod 实机通过 | 接受 GPL 后的优先基础候选 |
| Pabldi08/AirSend | 已有完整 Windows/Tauri/Rust 应用；README 明示 pre-alpha | 多设备同步与立体声仍在实验或验收中，发布版与开发集成功能不同 | 可作为交叉对照与备用基础 |
| Solst1cee/AirSendWindows | 针对 mini 增加 PTP，保留 WASAPI 与 AirPlay 2 实现 | 作者明确区分日志成功与实际出声；其他型号、最新 PTP 路径仍待硬件确认 | 适合研究修复，不能直接当“mini 已解决”的依据 |
| pyatv / AirPlayer | pyatv MIT；当前源码有 AirPlay 2 音频路径，AirPlayer 接入 Windows 采集与托盘 | pyatv 的在线支持页部分内容落后于源码；NTP/PCM 路径和新固件需要实测；上层应用的延迟配置不等于声学测量 | 适合诊断与快速探测，暂不作为所有型号的唯一产品基础 |
| airplay2-sender-cpp | Apache-2.0；可嵌入 C++ 库，提供协议测试和发送示例 | 当前文档明确接收端认证默认允许失败后继续，需显式开启严格模式；独立版本仍待新的实机确认，未提供内置发现 | 备用研究对象，不把默认配置直接用于产品 |
| OpenAir | Rust，含多房间与时钟同步设计 | README 明确 HomePod 未实测 | 不选作第一轮 HomePod 验收基线 |

对应一手来源：[AirFlash 项目](https://github.com/Ding-Kyoma/AirFlash)、[AirFlash 许可证](https://github.com/Ding-Kyoma/AirFlash/blob/main/LICENSE)、[AirSend](https://github.com/Pabldi08/AirSend)、[AirSendWindows](https://github.com/Solst1cee/AirSendWindows)、[pyatv 当前 AirPlay 2 音频源码](https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/protocols/airplayv2.py)、[AirPlayer](https://github.com/SabiQG/AirPlayer)、[C++ 发送端的状态与安全说明](https://github.com/akustikrausch/airplay2-sender-cpp#security-scope-read-me)、[OpenAir 的状态说明](https://github.com/Antoinenz/OpenAir#status)。

### 两项容易误读的证据

pyatv 在线支持页仍有“RAOP 尚不支持 HAP 加密”的旧表述，但当前 `airplayv2.py` 已实现配对验证、加密音频封包与反馈。不能据旧文档断言它只支持 AirPlay 1，也不能反过来仅凭存在代码就承诺当前 HomePod 固件兼容。其流式播放指南提到约两秒启动缓冲，这也不能当成所有配置的实际稳态延迟。[pyatv 支持页](https://pyatv.dev/documentation/supported_features/)、[当前源码](https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/protocols/airplayv2.py)、[流式播放指南](https://pyatv.dev/development/stream/)

AirFlash 引擎源码允许一个接收端或一个双机组，并允许 PTP/NTP 选项。调研快照提交为 `cf420d97700463c56adc31408510e8336e99e51c`，GitHub API 返回提交时间为 2026-10-11 22:07:48 UTC。这个编号是调研锚点，不代表我们已经构建或认证该提交。[提交](https://github.com/Ding-Kyoma/AirFlash/commit/cf420d97700463c56adc31408510e8336e99e51c)、[会话选项与验证](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/src/session.rs)

## 三种实现路线

1. **优先推荐：基于现有 GPL 原生应用与引擎开发。** 以 AirFlash 为首选验证候选，复用 WPF、设备发现、Rust 引擎和测试结构；先证明 Windows 10 + 单只 mini 能播放，再做必要的兼容修复和产品收敛。收益是复用完整声音通路；代价是要维护固定的上游来源、许可证与 Windows 10 修复，不能把上游宣传直接变成产品承诺。
2. **Python + pyatv。** 初期代码较少，可利用当前机器已有 Python 做协议诊断。但产品还需要声音采集、界面、打包及新固件适配；不能因 Python 脚本能发送文件，就认为系统实时声音、视频同步和所有型号都已完成。
3. **自行实现全套协议，或从较低层 C++ 库开始。** 控制力高，但需要重新承担发现、时钟、认证、传输和生命周期的集成测试。用户已接受 GPL，没有充分理由在第一版重写这些部分。

以上是工程判断。最终基础版本要经过本机构建、启动、发现和 mini 实机出声后才定版；若首选验证失败，应记录原因并比较备用方案，不能给失败基础套上界面就宣称完成。

## 推荐方案的初步结构

```text
Windows 应用 → 用户选定的播放端点
                         ↓ WASAPI loopback
WPF 托盘/控制面板 ←→ Rust 原生引擎 → 局域网 AirPlay → HomePod
    设备发现、状态、设置     采集、重采样、认证、时钟、音频、清理
```

复用上游已有 WPF 与引擎分工，通过已有 JSONL v1 子进程接口通信，避免再加浏览器前端、常驻 Web 服务或云端音频转发。上游已有会话编号、取消、EOF 清理、配对及音量命令，先保留这些边界。[引擎接口说明](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/README.md)、[WPF 进程连接实现](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/desktop/AirFlash.Core/EngineConnection.cs)

界面先保留设备列表、音频源选择、开始/停止、音量、连接状态和可读错误。启动时保持停止，用户主动开始才采集音频；停止、断网和退出时释放本次会话并恢复由应用改变的本机声音状态。认证失败应停止并说明原因，不降低接收端验证来换取“连接成功”。配对资料使用当前用户的系统保护存储，日志不写密码、PIN 或密钥。[上游认证实现](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/src/auth.rs)、[引擎存储说明](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/README.md)

同一设备的 `_airplay` 与 `_raop` 服务应合并，能力选择结合广播信息和设备 `/info`，不把 mini 型号写成唯一允许值。防火墙只为本应用所需局域网通信提供明确诊断与配置入口，不通过关闭防火墙或开放所有程序解决问题。局域网 IPv4 是首轮基线；IPv6、ARM64 和复杂 VLAN 不在未验证时宣称支持。

密码访问是必须单独记录的兼容项：当前 AirFlash 快照的直播 `Peer` 参数没有任意扬声器密码入口，瞬时认证使用协议预设值，持久配对走数字 PIN。它们不能等同于已支持“家庭 App → 要求密码”的所有设置。首轮先验证同网允许访问的 mini，受限设置要显示明确原因并纳入后续适配，不能暗中绕过访问限制。[会话参数](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/src/session.rs)、[认证入口](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/src/auth.rs)

## 音乐与视频分别验收

音乐先看连续播放、静音片段、音量、停止和恢复是否可靠。视频还要验证画面与实际出声的偏移及漂移；发送端的目标缓冲、已发包数量和连接状态都不等于出声时间。AirFlash 明确其实时档位 120ms 只是目标，原生引擎也没有跨型号 200ms 保证。[项目延迟说明](https://github.com/Ding-Kyoma/AirFlash#usage)、[引擎限制](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/native/airflash-engine/README.md)

工程推论：浏览器或其他播放器掌握视频时间线，独立声音发送器无法统一延迟所有应用的画面。我们应测出实际偏移，在支持同步调整的播放器中验证补偿；不要宣传“任意视频、零延迟、天然嘴型同步”，也不要把播放音频称为视频传输。游戏、会议等交互音频需要更严格的延迟目标，暂不作为默认承诺。

## 验收矩阵与当前状态

| 维度 | 必须记录的条件 | 本次状态 |
| --- | --- | --- |
| Windows 10 | 精确版本、构建号、启动、发现、播放、停止、恢复 | 本机为 Windows 10 Pro x64 22H2 / 19045，仅查询系统信息 |
| Windows 11 | 精确版本、构建号，同样跑完整流程 | 尚未有本次执行证据 |
| HomePod mini | 型号、固件、单只/双机、访问设置、采集端点、协商路径 | 用户有实机，固件和网络尚未核验 |
| HomePod 第 1/2 代 | 每个型号与固件组合各自验收 | 目标范围，未提供实机 |
| HomePod mini 2 | 发售后的实机、固件、广播能力与完整播放流程 | 新公布型号，尚未具备验收条件 |
| 立体声对 | 已在家庭 App 配好的兼容两台、左右声道、同步、停止与恢复 | 保留基础能力，待双机验收 |
| 稳定性与延迟 | 短时低音量出声、静音后恢复、断网重连、实际偏移、长播漂移 | 全部未执行 |

AirFlash 自己的 Windows 10 兼容文档也要求分别验证版本，明确静态检查和 Windows 11 回归不能证明 Windows 10 实际运行。我们可以在本机 19045 上补足证据，但不能外推所有旧版 Windows 10。[上游 Windows 兼容文档](https://github.com/Ding-Kyoma/AirFlash/blob/cf420d97700463c56adc31408510e8336e99e51c/docs/WINDOWS-COMPATIBILITY.md)

## 本地准备情况与下一步

只读检查发现 Python 3.11.9 可用，默认 PATH 未发现 `cargo`，`dotnet --list-sdks` 没有列出 SDK；Visual Studio 安装器报告已有 C++ 工具组件。本文没有据此安装或修改工具链。正式实施时先检查可复用的运行时路径，再决定是否需要项目范围安装，避免改变全局默认工具。

设计方向已获用户确认，正式规格已另存为[设计规格](../superpowers/specs/2026-10-14-windows-homepod-audio-design.md)，待用户审阅后再依照已指定的 Superpowers 架构流程形成实施计划。规格覆盖兼容状态、访问模式、声音状态恢复、验收标准、上游版本和许可证归属。正式实现优先沿用现有调用方、测试和打包方式，不先添加家庭设备控制、麦克风转发、自研驱动、EQ、云服务或账号系统。
