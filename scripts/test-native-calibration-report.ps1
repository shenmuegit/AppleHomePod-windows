param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
$result = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
if (-not $result.ok -or $null -eq $result.proposal) { throw 'A successful real calibration report is required.' }
$half = $result.proposal.session_half
if ($null -eq $half -or -not [double]::IsFinite($half.rms) -or -not [double]::IsFinite($half.peak) -or
    $half.rms -lt .0135 -or $half.rms -gt .0148 -or $half.peak -lt .019 -or $half.peak -gt .021) {
    throw 'The half-volume measurement still contains buffered samples from the previous gain.'
}
if (@($result.proposal.capture_sample_counts).Count -ne 7 -or
    @($result.proposal.capture_sample_counts | Select-Object -First 5 | Where-Object { $_ -lt 9600 }).Count -gt 0) {
    throw 'Calibration did not capture each bounded measurement window.'
}
if ($result.reference_device_volume -lt 1 -or $result.reference_device_volume -gt 100) {
    throw 'Calibration has no nonzero receiver reference.'
}
[pscustomobject]@{ ok=$true; checks=3; report=[IO.Path]::GetFullPath($Report) } | ConvertTo-Json
