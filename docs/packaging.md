# 本地构建与不可变交付

本页描述 AppleHome 的本地候选交付。打包不上传文件、不安装驱动，也不更新已安装后台。使用流程见 [first-run.md](first-run.md)，模块与数据归属见 [architecture.md](architecture.md)。

## 构建和验证输入

从干净、已提交的源码 checkout 开始，先按 [README](../README.md#从源码构建)完成 Rust、.NET 和相关软件检查。准备原始驱动输入，将发布输出放入本轮 `artifacts` staging：

```powershell
$Revision = (git --no-replace-objects rev-parse HEAD).Trim()
$Version = ([xml](Get-Content desktop/AirFlash.App/AirFlash.App.csproj -Raw)).Project.PropertyGroup.Version
$Publish = Join-Path $PWD ('artifacts/release-inputs/' + [Guid]::NewGuid().ToString('N') + '/publish')
pwsh -NoProfile -File scripts/prepare-native-driver.ps1
pwsh -NoProfile -File scripts/dotnet.ps1 publish AirFlash.App -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:InformationalVersion=$Version+$Revision" "-p:PathMap=$PWD=/_/" -o $Publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; keep existing deliveries.' }
```

发布目标验证原始 VB-CABLE ZIP 的固定哈希，并审核打包的二进制输入。保留本次 EXE、引擎及兼容性报告的对应关系，不复用其他构建的成功报告。

打包需要两份显式 JSON。下列内容说明字段结构；`passed` 应来自对应检查实际成功的记录，硬件资格保持本轮事实或明确的未验证状态。

`provenance.json`：

```json
{
  "schema": 1,
  "source_commit": "完整小写HEAD",
  "exe_sha256": "发布EXE的SHA256",
  "engine_sha256": "本次构建引擎的SHA256",
  "vendor_archive_sha256": "未修改原始厂商ZIP的SHA256",
  "vendor_readme_sha256": "原始厂商readme的SHA256"
}
```

`validation.json`：

```json
{
  "schema": 1,
  "source_commit": "与provenance相同的完整小写HEAD",
  "exe_sha256": "与provenance相同的EXE哈希",
  "software_checks": {
    "dotnet": { "status": "passed", "report": "对应测试报告" },
    "packaging": { "status": "passed", "report": "对应打包检查报告" }
  },
  "qualification": {
    "hardware": "本次构建未做实机验收",
    "network_disconnect": "deferred_by_user",
    "limits": "实际系统、型号、固件与尚未验证的范围"
  }
}
```

证据输入只包含适合交付的字段，不包含配对凭据、用户配置或私人日志。引擎哈希和编译来源是构建者的显式声明；哈希匹配不构成二进制可由源码重现的证明。软件测试通过不会自动复制旧版本的听感资格。

## 受版本管理的打包入口

将上述记录和原始厂商 readme 放在项目 `artifacts` 内，执行：

```powershell
python -m scripts.package_release --source-root . --version $Version --source-revision $Revision --exe "$Publish/AirFlash.exe" --provenance artifacts/release-inputs/provenance.json --validation artifacts/release-inputs/validation.json --vendor-archive artifacts/vendor/VBCABLE_Driver_Pack45.zip --vendor-readme artifacts/vendor/VB-CABLE-original-readme.txt --output-root dist
```

脚本只使用 Python 标准库和 Git，`python -m scripts.package_release --help` 显示参数。源码必须是指定 HEAD 的干净 checkout；输入和输出均须位于其中，路径祖先和文件本身的符号链接／reparse point 会被拒绝。源码按原始 Git 对象归档，忽略本机 `git replace` 引用，并拒绝被跟踪的工作数据和常见凭据文件。

输出目录是 `dist/<version>-<完整commit>/`，包含 EXE、对应源码 ZIP、许可与 GPLv3 全文、第三方及厂商说明、原始厂商输入、来源及验证记录、manifest、SHA256SUMS、交付 ZIP 和外部 ZIP 哈希。源码 ZIP 和交付 ZIP 文件名使用提交前12位以控制 Windows 路径长度。

目标已存在时直接拒绝。先完成独立 staging，再提升新交付目录；失败和重试不删除历史交付物。ZIP 使用固定时间戳和原始字节稳定归档，这不表示跨工具链 EXE 编译已经达到可重现构建。

## 保留的 MSI 和诊断工具

`scripts/build.ps1` 保留为可选原 MSI 入口，其版本预留机制可能使用远端 Git；它不是上述本地打包命令。每轮构建使用 `artifacts/build/<GUID>`，完整结果进入 `dist/legacy-msi/<version>-<完整commit>`，不清空根 `build` 或 `dist`。

- MSI 校验：`pwsh scripts/verify-release.ps1 -Version <version> -Directory <明确的legacy交付目录>`；结果写入独立 artifacts，不修改原交付。
- MSI 生命周期：`pwsh scripts/test-installer.ps1 -Directory <明确的legacy交付目录>`；仅在脚本允许的一次性托管 Windows runner 中运行。
- 旧托盘检查：`pwsh scripts/test-tray.ps1 -Executable <明确的已发布EXE>`；当前原生后台没有日常托盘。

这些保留工具不能替代当前 HomePod 自动安装、正常音乐或音画同步的实机验收。[RELEASING.md](RELEASING.md) 已标记为上游历史流程。
