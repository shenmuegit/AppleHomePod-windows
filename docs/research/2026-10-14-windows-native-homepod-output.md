# Windows 原生 HomePod 输出设备：可行性与驱动核查

核查日期：2026-10-14。本文记录新交互方向的一手资料和本机只读证据；尚未安装新驱动、修改音频设备名称或路由，也未实现新的后台模式。

## 用户已明确的方向

- Windows 音频输出列表出现独立的 HomePod 设备。
- 用户选择该设备即可播放；音量和静音使用 Windows 原生控件。
- 日常无需 AppleHome 控制面板或额外音频路由设置。
- 用户已选择独立设备路线，接受一次驱动安装。具体驱动包和改动范围须在安装前明确。
- 真实断网测试继续暂缓。

## 为什么旧模式会出现本机先响

现有应用采集一个正常播放端点的声音，再经网络发往 HomePod。采集源本身有物理播放路径，HomePod 另有网络缓冲；当前 mini 的可听配置使用 2000 ms 目标。应用另尝试静音本机，并在音频样本停滞时保守恢复。

这是当前声音路径的解释，不是对本次每次先后出声原因的完整测量。只读检查时本机端点未静音；现有日志不能区分此次是启动期间的短暂本机播放、用户外部解除静音，还是保护回退。新的专用虚拟输出应从声音路径上避免物理端点参与。

本次用户启动过长播，随后确认手动停止。没有完成可证明的 30 分钟连续听音验收，不能把进程年龄或准备开始的回复当作通过证据。

## Windows 机制

Windows 的音频选择器使用系统音频端点。虚拟音频驱动是建立可被常规应用选择的虚拟播放设备的现成路径；给现有 UI 加一个设备名称不足以建立 PCM 数据通路。Microsoft 的 SYSVAD 提供 WDM 虚拟音频设备开发样例。[Audio endpoint devices](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-endpoint-devices)、[SYSVAD sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/)

默认输出变化可通过 `IMMNotificationClient` 接收，按渲染端点及设备角色处理。原生设备音量和静音使用 EndpointVolume；应用混音器音量由 Windows 音频会话处理。驱动存在硬件控制时和软件控制时，增益位置不同，必须校准所取 PCM 是否已应用主音量，避免重复衰减。[Default device notification](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-ondefaultdevicechanged)、[EndpointVolume API](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpointvolume-api)、[Volume controls](https://learn.microsoft.com/en-us/windows/win32/coreaudio/volume-controls)

## 本机现状

实际便携包的只读 `--audio-check` 枚举到 11 个播放端点，其中 8 个是 Voicemeeter 虚拟播放端点，没有 HomePod 名称的端点。Voicemeeter Remote64 DLL 的 Authenticode 校验有效；这不构成其驱动在新用途下的功能认证。

用户选择独立设备方案，因此不占用、改名或更改现有 Voicemeeter 端点和路由。Windows SDK 10.0.26100.0 可用，但未发现对应 WDK `portcls.h` / `wdm.h`。自行开发驱动会额外需要 WDK 和生产签名流程。

本机完整枚举与签名证据在忽略目录 `artifacts/validation/windows-output-redesign-audio-check.json`、`vbcable-pack45-inspection.json`、`vbcable-kernel-signature.log`。

## 候选比较

| 路线 | 证据与代价 | 结论 |
|---|---|---|
| 独立 VB-CABLE + AppleHome 后台发送 | 已有独立虚拟播放设备，官方包声明支持 Windows 10/11，当前包签名已本地验证；驱动为独立第三方软件，需遵循其安装与分发许可 | 首轮本机方案 |
| 复用现有 Voicemeeter 输入 | 本机已安装，官方资料说明其虚拟输入是 Windows 播放设备；需要保留用户已有路由并确认独占使用的端点 | 用户未选择这条产品路线，不修改 |
| 自行基于 SYSVAD 开发 | 能自主定义设备与数据接口；WDK、驱动开发和生产签名增加交付工作 | 后续独立驱动候选，本轮不以测试签名替代正常安装 |
| Scream | 上游说明 Windows 11 安装涉及测试模式和 Secure Boot 相关步骤，且默认音频是独立的 PCM 网络协议 | 不作为本轮 Windows 10/11 安装基线 |

来源：[VB-CABLE 官方页面](https://vb-audio.com/Cable/)、[Voicemeeter 虚拟输入说明](https://voicemeeter.com/quick-tips-voicemeeter-virtual-inputs-and-outputs-windows-10-and-up/)、[SYSVAD](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/)、[Scream 上游说明](https://github.com/duncanthrax/scream)。

## 已核查的独立驱动包

- 来源：官方页面链接的 [VBCABLE_Driver_Pack45.zip](https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip)。
- 归档 SHA256：`b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb`。
- 本轮目标 INF：`vbMmeCable64_win10.inf`，版本 `3.3.1.7`，日期 2024-10-07。
- `vbaudio_cable64_win10.cat` 签名有效，签署者为 Microsoft Windows Hardware Compatibility Publisher。
- 使用已安装 Windows SDK 的 SignTool 按 `/kp`、明确指定上述目录文件校验 x64 `.sys`：成功 1、警告 0、错误 0。
- `VBCABLE_Setup_x64.exe` 的 Authenticode 校验有效，签署者为 VB-Audio 作者；未执行安装器。
- INF 的硬件标识为 `VBAudioVACWDM`；识别设备必须结合实例与供应商信息，不能只按通用友好名称搜索。
- 官方要求管理员安装并在安装后重启。具体安装时机仍需安排，未触发重启。

这些证据证明包的来源、完整性与签名，不代替本机安装后的实际出声、增益和 Windows 11 验收。

## 许可和交付边界

包内 `readme.txt` 的最终用户许可允许原样复制、分发，要求保留来源与 donationware 说明；将该包集成到其他软件安装流程需要作者同意。本轮没有取得该同意，因此不把驱动包嵌入 AppleHome 安装器，也不改动 INF、CAT、SYS 或供应商安装器。

首轮采用一次独立的官方驱动安装，然后 AppleHome 绑定和管理自己获准使用的虚拟播放端点。应用与发送引擎保持 GPLv3+；VB-CABLE 的许可另行保留，不称其为 GPL 开源驱动。后续如需一个统一安装包，应另取得许可或完成独立驱动及签名路线。[厂商许可说明](https://vb-audio.com/Services/licensing.htm)，具体集成限制依据本次下载包内 `readme.txt`。

## 必须实证的技术问题

1. 专用虚拟端点的友好名称、唯一身份和卸载恢复可可靠管理。
2. 此驱动的播放端点 loopback 能提供稳定样本；仅使用明确绑定的虚拟播放端点，不开放物理麦克风。
3. Windows 主音量、静音、应用混音器对最终声音都有效，且每项增益只应用一次。
4. 原生选择自动开始和停止；默认设备变化及异步握手不能重放过时声音。
5. 暂停、静音和空闲期间保持正常会话状态，恢复后可继续播放，不使用本机静音回退维持路由。
6. 新路径重新进行持续播放、静音后恢复和音量实测；旧版基本播放证明不能直接充当新路径验收。
