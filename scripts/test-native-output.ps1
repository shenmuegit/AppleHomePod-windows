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
[pscustomobject]@{ ok=$true; checks=3; endpoints=@($preflight.endpoints).Count; driver_installed=$preflight.driver_installed; report_directory=$reportDirectory } | ConvertTo-Json
