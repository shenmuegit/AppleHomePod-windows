# Verify a legacy MSI delivery without writing into that immutable directory.
param([Parameter(Mandatory)][string]$Version, [string]$Directory = 'dist')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
. (Join-Path $PSScriptRoot 'release-common.ps1')
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version must be canonical X.Y.Z.' }
$delivery = Resolve-AirFlashReleasePath $repoRoot $Directory
$exe = Resolve-AirFlashReleasePath $repoRoot (Join-Path $delivery 'AirFlash.exe')
$msi = Resolve-AirFlashReleasePath $repoRoot (Join-Path $delivery "AirFlash-$Version.msi")
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $exe))
$actual = '{0}.{1}.{2}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart
if ($actual -ne $Version) { throw "EXE version mismatch: $actual" }
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase((Resolve-Path $msi).Path, 0)
$view = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductVersion'")
$view.Execute(); $record = $view.Fetch()
if ($record.StringData(1) -ne $Version) { throw 'MSI version mismatch.' }
$view.Close()
$auditPath = Resolve-AirFlashReleasePath $repoRoot (Join-Path $delivery 'bundle-audit.json')
if (-not (Test-Path -LiteralPath $auditPath)) {
    $auditPath = Resolve-AirFlashReleasePath $repoRoot 'artifacts/compatibility/bundle-audit.json'
}
$audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
if (-not $audit.ok -or $audit.published_exe_sha256 -ne (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Compatibility audit does not match release EXE.' }
$report = Resolve-AirFlashReleasePath $repoRoot ('artifacts/verify-release/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $report | Out-Null
foreach ($diagnostic in @('self-check', 'compat-check')) {
    $output = Join-Path $report "$diagnostic.json"
    $process = Start-Process -FilePath $exe -ArgumentList @("--$diagnostic", '--output', "`"$output`"") -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw "$diagnostic timed out." }
    if ($process.ExitCode -ne 0) { throw "Application $diagnostic failed." }
    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if (-not $result.ok -or $result.version -ne $Version) { throw "$diagnostic version mismatch." }
}
# An existing directory exercises the dispatcher error path without changing user state.
$invalidOutput = Join-Path $report 'invalid-output'
New-Item -ItemType Directory -Path $invalidOutput | Out-Null
$process = Start-Process -FilePath (Resolve-Path $exe) -ArgumentList @('--compat-check', '--output', "`"$invalidOutput`"") -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Early diagnostic failure opened a blocking dialog or failed to exit.' }
if ($process.ExitCode -ne 1) { throw "Unexpected early diagnostic failure exit: $($process.ExitCode)" }
$hashes = foreach ($file in @($exe,$msi)) { '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
$hashes | Set-Content -LiteralPath (Join-Path $report 'SHA256SUMS.txt') -Encoding ascii
Write-Output $report
