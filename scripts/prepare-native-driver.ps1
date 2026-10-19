$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = [IO.Path]::GetFullPath((Join-Path $project 'artifacts/vendor/VBCABLE_Driver_Pack45.zip'))
$expected = 'B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB'
if (-not $target.StartsWith($project + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Driver input must remain inside the project.' }
for ($parent = [IO.DirectoryInfo]([IO.Path]::GetDirectoryName($target)); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Driver input paths must not traverse directory links.' }
}
if (Test-Path -LiteralPath $target) {
    if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Driver input must not be a file link.' }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw 'Existing driver archive does not match the original Pack45 package; it was preserved.' }
} else {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    $staging = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Invoke-WebRequest -Uri 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip' -OutFile $staging
        if ((Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash -ne $expected) { throw 'Downloaded driver archive is not the pinned original Pack45 package.' }
        [IO.File]::Move($staging, $target)
    } finally { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging } }
}
Write-Output "Verified original VB-CABLE archive: $target"
Write-Output 'Redistribution includes VB-Audio origin, Donationware notice, vendor readme and license/donation links; review vendor terms before public distribution.'
