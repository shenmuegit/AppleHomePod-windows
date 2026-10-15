param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$OutputDirectory = 'artifacts/validation/native-output'
)
$ErrorActionPreference = 'Stop'
$taskExecutable = (Resolve-Path -LiteralPath $Executable).Path
$reportDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ('check-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null

function Invoke-NativeCheck([string[]]$CheckArgs) {
    $checkProcess = Start-Process -FilePath $taskExecutable -ArgumentList $CheckArgs -PassThru -WindowStyle Hidden
    if (-not $checkProcess.WaitForExit(30000)) {
        $checkProcess.Kill()
        throw 'The native diagnostic blocked instead of returning without a dialog.'
    }
    $checkProcess.Refresh()
    return $checkProcess.ExitCode
}

$preflightPath = Join-Path $reportDirectory 'preflight.json'
$preflightExit = Invoke-NativeCheck @('--native-preflight-check','--output',('"' + $preflightPath + '"'))
$preflight = Get-Content -LiteralPath $preflightPath -Raw | ConvertFrom-Json
foreach ($property in @('ok','stage','wpf_initialized','driver_installed','unmanaged_driver','preinstall_snapshot_saved','endpoints','defaults')) {
    if (-not $preflight.PSObject.Properties[$property]) { throw "Native preflight omitted required property '$property'." }
}
if ($preflight.ok -isnot [bool] -or $preflight.driver_installed -isnot [bool]) { throw 'Native inspection status must be boolean.' }
if ($preflight.wpf_initialized) { throw 'Native inspection constructed the WPF application.' }
if ($preflight.stage -ne 'native-preflight') { throw 'Native inspection did not reach its dispatcher.' }
if ($preflight.ok -eq $preflight.unmanaged_driver) { throw 'Unmanaged VB-CABLE must prevent an automatic claim.' }
$expectedExit = if ($preflight.ok) { 0 } else { 1 }
if ($preflightExit -ne $expectedExit) { throw 'Native preflight exit code disagrees with its result.' }
if (@($preflight.defaults).Count -ne 3) { throw 'Native inspection must report all three render roles.' }

$bindingPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'AppleHome/native-output.json'
$beforeBinding = if (Test-Path -LiteralPath $bindingPath) { (Get-FileHash -LiteralPath $bindingPath).Hash } else { $null }
$invalidBindPath = Join-Path $reportDirectory 'invalid-bind.json'
$invalidExit = Invoke-NativeCheck @('--native-bind','--output',('"' + $invalidBindPath + '"'))
$invalid = Get-Content -LiteralPath $invalidBindPath -Raw | ConvertFrom-Json
if ($invalidExit -ne 1 -or $invalid.ok -ne $false) { throw 'Missing binding identities must fail without changing endpoints.' }
$afterBinding = if (Test-Path -LiteralPath $bindingPath) { (Get-FileHash -LiteralPath $bindingPath).Hash } else { $null }
if ($beforeBinding -ne $afterBinding) { throw 'Invalid input changed the native binding.' }

$directoryOutput = Join-Path $reportDirectory 'directory.json'
New-Item -ItemType Directory -Path $directoryOutput | Out-Null
$directoryExit = Invoke-NativeCheck @('--native-preflight-check','--output',('"' + $directoryOutput + '"'))
if ($directoryExit -ne 1) { throw 'Invalid diagnostic output must fail without a blocking dialog.' }
$protectedExit = Invoke-NativeCheck @('--native-preflight-check','--output',('"' + $bindingPath + '"'))
$protectedBinding = if (Test-Path -LiteralPath $bindingPath) { (Get-FileHash -LiteralPath $bindingPath).Hash } else { $null }
if ($protectedExit -ne 1 -or $beforeBinding -ne $protectedBinding) { throw 'A diagnostic output path must not overwrite private configuration.' }
$checks = 4
if (Test-Path -LiteralPath $bindingPath) {
    $storedBinding = Get-Content -LiteralPath $bindingPath -Raw | ConvertFrom-Json
    $monitorPath = Join-Path $reportDirectory 'monitor.json'
    $monitorExit = Invoke-NativeCheck @('--native-monitor-check','--output',('"' + $monitorPath + '"'))
    $monitor = Get-Content -LiteralPath $monitorPath -Raw | ConvertFrom-Json
    if ($monitorExit -ne 0 -or -not $monitor.ok -or -not $monitor.listening_only -or $monitor.listener_apartment -ne 'MTA' -or
        $monitor.wpf_initialized -or $monitor.engine_started -or $monitor.capture_started -or @($monitor.snapshot.default_render_ids).Count -ne 3 -or
        $beforeBinding -ne (Get-FileHash -LiteralPath $bindingPath).Hash) { throw 'Read-only native monitoring must retain binding bytes and avoid audio/UI activation.' }
    if (-not $storedBinding.calibration -and $monitor.binding_valid) { throw 'Uncalibrated bindings must remain unusable.' }
    $checks++
    $unnamed = @($preflight.endpoints | Where-Object { $_.endpoint_id -eq $storedBinding.endpoint.endpoint_id -and
        $_.pin_name -eq 'CABLE Input' -and $_.endpoint_label -eq 'CABLE Input' -and -not $storedBinding.calibration })
    if ($unnamed.Count -eq 1) {
        $nameGatePath = Join-Path $reportDirectory 'system-name-required.json'
        $nameExit = Invoke-NativeCheck @('--native-bind','--receiver-id',$storedBinding.receiver_id,
            '--endpoint-id',$unnamed[0].endpoint_id,'--output',('"' + $nameGatePath + '"'))
        $nameGate = Get-Content -LiteralPath $nameGatePath -Raw | ConvertFrom-Json
        if ($nameExit -ne 1 -or $nameGate.error -notmatch 'First name this output in Windows' -or
            $beforeBinding -ne (Get-FileHash -LiteralPath $bindingPath).Hash) { throw 'First-time naming must use Windows properties without changing the binding.' }
        $checks++
    }
}
if (-not (Test-Path -LiteralPath $bindingPath)) {
    $unboundCalibrationPath = Join-Path $reportDirectory 'unbound-calibration.json'
    $unboundExit = Invoke-NativeCheck @('--native-calibrate','--output',('"' + $unboundCalibrationPath + '"'))
    $unbound = Get-Content -LiteralPath $unboundCalibrationPath -Raw | ConvertFrom-Json
    if ($unboundExit -ne 1 -or $unbound.ok -ne $false -or $unbound.error -notmatch 'Bind a verified virtual output') {
        throw 'Calibration must reject a missing binding before starting any capture or reference probe.'
    }
    if ((Invoke-NativeCheck @('--native-calibration-source')) -ne 1) { throw 'The isolated source must require a verified virtual binding.' }
    $checks += 2
}
[pscustomobject]@{ ok=$true; checks=$checks; endpoints=@($preflight.endpoints).Count; driver_installed=$preflight.driver_installed; report_directory=$reportDirectory } | ConvertTo-Json
