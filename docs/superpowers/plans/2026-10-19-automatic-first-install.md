# 自动首次安装实施记录

用户已批准：安装程序自动安装驱动、命名 Windows 输出、选择输出并播放确认音频；用户只确认是否听到。先前的“不放短音”限制在这一明确的安装确认步骤中被后续要求替代，日常和持续验证仍使用正常音乐与 Windows 原生控件。断网测试继续暂缓。

## 范围与边界

- 复用现有绑定、校准、设备适配、原生后台和 NTP/ALAC 引擎。
- 嵌入未修改的原厂 VB-CABLE Pack45，保留来源、Donationware 信息、原始 readme 与全部文件。校验 ZIP、文件列表和每个文件，拒绝路径跳转、链接及包内容变更。系统安装许可和必要重启保留。
- 只配置经身份核验的普通播放端点；不接管 16ch、录音设备、其他音频驱动或被用户改动的启动任务。
- 首次发现必须完成有上限的浏览窗口，避免把最先解析的 HomePod 当作唯一设备；多只设备需要目标选择。
- 自动命名并读回 Windows 的设备描述，切换 Console/Multimedia 默认播放角色；其他应用路由与 Communications 角色保留。
- 先完成隔离的音量测量，再准备发送会话。确认音源固定使用绑定端点，无物理输出回退、无话筒采集；单次 PCM 1.5 秒且带包络，子进程上限30秒，stdin EOF停止。
- 明确听到和有效发送证据共同允许保存；负反馈最多触发已有两次有界参数恢复，耗尽后显示连接错误。
- 正常取消与异常中断均按变更所有权恢复，保留后续外部修改；中断记录先落盘再改系统设置，下一次设置启动先恢复。

## 最小改动到达位置

`NativeSetupFlow`、Windows平台实现、首次安装窗口、入口源命令、发布资源准备、模拟/UI检查和文档。新增有限确认音源、输出变更事务、原厂包校验/安装器组件。没有新增依赖，没有修改 Rust 发送或延迟策略，没有实现自动升级。

## 证据分层

Windows10 19045 上的策略 API 探针成功修改并恢复空闲同适配器端点的描述（PID2）；FriendlyName（PID14）写入仍拒绝，生产代码不使用它。当前默认输出的幂等设置成功。探针没有放声音、修改绑定端点或重启后台。

软件回归与模拟界面不等于新流程完整实机安装通过。持续保留原可用后台及旧交付包；候选单独构建。完整原厂静默安装、UAC、真实重启、Windows11和其他设备仍需对应验收。

## 原始参考

- [VB-Audio当前分发条件](https://vb-audio.com/Services/licensing.htm)：可识别来源及Donationware信息；原包旧readme还有作者协议文字，公开分发前需核对适用条件。
- [Microsoft端点友好名称](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/friendly-names-for-audio-endpoint-devices)：标准属性表的只读行为不等于所有系统策略接口均不可用。
- [AudioDeviceCmdlets策略接口声明](https://github.com/frgnca/AudioDeviceCmdlets/blob/master/SOURCE/IPolicyConfig.cs)：内部COM声明以本机往返测试为准；其他Windows构建尚未取得资格。
- [Parsec原厂安装调用](https://github.com/parsec-cloud/Parsec-Cloud-Preparation-Tool/blob/master/PostInstall/PostInstall.ps1)：参考`-i -h`，不复制任何证书信任或安全设置修改。
