# Legacy MSI build; preserve prior deliveries and promote only a complete new run.
param([switch]$Console, [string]$ReservedVersion, [ValidateSet('stable','preview')][string]$ReleaseChannel = 'stable', [string]$NewVersion)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
. (Join-Path $PSScriptRoot 'dotnet-common.ps1')
. (Join-Path $PSScriptRoot 'release-common.ps1')
$dotnetContext = Get-AirFlashDotnetContext -RepoRoot $repoRoot
$null = Assert-AirFlashDotnetSdk -Context $dotnetContext

$artifacts = Resolve-AirFlashReleasePath $repoRoot 'artifacts'
$lockPath = Resolve-AirFlashReleasePath $repoRoot 'artifacts/release.lock'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$releaseLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
try {
. (Join-Path $PSScriptRoot 'release-version.ps1')
$null = Resolve-AirFlashReleasePath $repoRoot 'artifacts/release-version.txt'
$releaseVersion = Reserve-ReleaseVersion $repoRoot $ReservedVersion $ReleaseChannel -NewVersion $NewVersion
Write-Host "Building AirFlash $releaseVersion"
$sourceRevision = & git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot determine release source revision.' }
$delivery = Resolve-AirFlashReleasePath $repoRoot "dist/legacy-msi/$releaseVersion-$sourceRevision"
if (Test-Path -LiteralPath $delivery) { throw "Release delivery already exists: $delivery" }
$stage = Resolve-AirFlashReleasePath $repoRoot ('artifacts/build/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
& (Join-Path $PSScriptRoot 'build-native.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }

$publish = Join-Path $stage 'wpf-publish'
$compatibility = Join-Path $stage 'compatibility'
$project = Join-Path $repoRoot 'desktop/AirFlash.App/AirFlash.App.csproj'
$consoleFlag = if ($Console) { 'true' } else { 'false' }
$informationalVersion = if ($ReleaseChannel -eq 'preview') { "$releaseVersion-rc.1" } else { $releaseVersion }
Invoke-AirFlashDotnet -Context $dotnetContext -Arguments @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
    '-p:EnableCompressionInSingleFile=true', '-p:RestoreLockedMode=true',
    '-p:DebugType=None', '-p:DebugSymbols=false', "-p:PathMap=$repoRoot=/_/",
    "-p:AirFlashConsole=$consoleFlag", "-p:Version=$releaseVersion",
    "-p:CompatibilityArtifacts=$compatibility",
    "-p:InformationalVersion=$informationalVersion", "-p:FileVersion=$releaseVersion.0",
    "-p:AssemblyVersion=$releaseVersion.0", '-o', $publish
)
if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed; do not use residual dist files' }
$builtExe = Resolve-AirFlashReleasePath $repoRoot (Join-Path $publish 'AirFlash.exe')
if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw 'Published executable missing' }

$output = Join-Path $stage 'delivery'
$auditPath = Resolve-AirFlashReleasePath $repoRoot (Join-Path $compatibility 'bundle-audit.json')
if (-not (Test-Path -LiteralPath $auditPath)) { throw 'Missing bundle compatibility audit.' }
$audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
if (-not $audit.ok) { throw 'Bundle compatibility audit failed.' }
$audit | Add-Member -NotePropertyName published_exe_sha256 -NotePropertyValue (Get-FileHash -LiteralPath $builtExe -Algorithm SHA256).Hash.ToLowerInvariant() -Force
$audit | Add-Member -NotePropertyName published_version -NotePropertyValue $releaseVersion -Force
$audit | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $stage 'bundle-audit.json') -Encoding utf8
$buildInstaller = Join-Path $PSScriptRoot 'build-installer.ps1'
& $buildInstaller -PublishPath $publish -OutputPath $output -DotnetPath $dotnetContext.Path
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Copy-Item -LiteralPath $builtExe -Destination (Join-Path $output 'AirFlash.exe')
Copy-Item -LiteralPath (Join-Path $stage 'bundle-audit.json') -Destination (Join-Path $output 'bundle-audit.json')

$msiName = "AirFlash-$releaseVersion.msi"
$expected = @('AirFlash.exe', $msiName, 'bundle-audit.json')
$actual = @(Get-ChildItem -LiteralPath $output -Force | Select-Object -ExpandProperty Name)
$actualSorted = @($actual | Sort-Object)
$expectedSorted = @($expected | Sort-Object)
if (($actualSorted -join '|') -cne ($expectedSorted -join '|')) {
    throw "Unexpected release outputs: $($actual -join ', ')"
}
$null = Resolve-AirFlashReleasePath $repoRoot $delivery
New-Item -ItemType Directory -Path (Split-Path $delivery -Parent) -Force | Out-Null
# Directory.Move fails if a concurrent publisher has already claimed this name.
[IO.Directory]::Move($output, $delivery)
Get-Item -LiteralPath (Join-Path $delivery 'AirFlash.exe'), (Join-Path $delivery $msiName) | Select-Object Name, Length, LastWriteTime

} finally { $releaseLock.Dispose() }
