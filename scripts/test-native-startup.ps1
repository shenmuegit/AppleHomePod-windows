param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$OutputDirectory = 'artifacts/validation/native-output'
)
$ErrorActionPreference = 'Stop'
$startupExecutable = (Resolve-Path -LiteralPath $Executable).Path
$startupDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ('startup-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $startupDirectory -Force | Out-Null
function Invoke-StartupCheck([string[]]$CheckArgs) {
    $startupProcess = Start-Process -FilePath $startupExecutable -ArgumentList $CheckArgs -PassThru -WindowStyle Hidden
    if (-not $startupProcess.WaitForExit(30000)) { $startupProcess.Kill(); throw 'Native startup check blocked instead of returning without a dialog.' }
    $startupProcess.Refresh(); return $startupProcess.ExitCode
}
function Read-Inspection([string]$Path) {
    $inspectionExit = Invoke-StartupCheck @('--native-check','--data-directory',('"' + (Join-Path $startupDirectory 'isolated-data') + '"'),'--output',('"' + $Path + '"'))
    $inspection = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($inspectionExit -ne 0 -or -not $inspection.ok -or -not $inspection.headless_dispatch -or $inspection.wpf_initialized -or
        $inspection.normal_window_count -ne 0 -or $inspection.daily_tray_registered -or $inspection.engine_started -or $inspection.capture_started -or
        $inspection.binding_present -or $inspection.can_enable -or -not $inspection.task_schema_valid -or $inspection.uses_service -or $inspection.stores_password) {
        throw 'Isolated native inspection must validate the real in-memory task schema and retain the missing-binding guard without UI/audio activation.'
    }
    return $inspection
}
$before = Read-Inspection (Join-Path $startupDirectory 'before.json')
$checks = 1
$startupBindingPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'AppleHome/native-output.json'
$startupInstalledPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AppleHome/app/AppleHome.exe'
$startupBindingHash = if (Test-Path -LiteralPath $startupBindingPath) { (Get-FileHash -LiteralPath $startupBindingPath).Hash } else { $null }
$startupInstalledHash = if (Test-Path -LiteralPath $startupInstalledPath) { (Get-FileHash -LiteralPath $startupInstalledPath).Hash } else { $null }
$startupBinding = if (Test-Path -LiteralPath $startupBindingPath) { Get-Content -LiteralPath $startupBindingPath -Raw | ConvertFrom-Json } else { $null }
if (-not $startupBinding -or -not $startupBinding.calibration) {
    $enablePath = Join-Path $startupDirectory 'unqualified-enable.json'
    $enableExit = Invoke-StartupCheck @('--native-enable','--output',('"' + $enablePath + '"'))
    $enable = Get-Content -LiteralPath $enablePath -Raw | ConvertFrom-Json
    if ($enableExit -ne 1 -or $enable.ok -ne $false) { throw 'An uncalibrated binding must not enable or install the agent.' }
    $bindingAfter = if (Test-Path -LiteralPath $startupBindingPath) { (Get-FileHash -LiteralPath $startupBindingPath).Hash } else { $null }
    $installedAfter = if (Test-Path -LiteralPath $startupInstalledPath) { (Get-FileHash -LiteralPath $startupInstalledPath).Hash } else { $null }
    $after = Read-Inspection (Join-Path $startupDirectory 'after.json')
    if ($bindingAfter -ne $startupBindingHash -or $installedAfter -ne $startupInstalledHash -or $before.task_exists -ne $after.task_exists) {
        throw 'Rejected enable changed binding, installed executable or startup-task presence.'
    }
    $checks++
}
[pscustomobject]@{ ok=$true; checks=$checks; task_schema_valid=$before.task_schema_valid; report_directory=$startupDirectory } | ConvertTo-Json
