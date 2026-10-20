# Validate every existing ancestor before creating delivery data.
function Resolve-AirFlashReleasePath([string]$RepoRoot, [string]$Path) {
    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $root $Path }))
    if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path is outside the project: $target"
    }
    if ($target.Substring([IO.Path]::GetPathRoot($target).Length) -match '[:*?]') {
        throw "Invalid release path: $target"
    }
    $ancestor = $target
    while ($ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction SilentlyContinue
        if ($item -and $item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing linked release path: $ancestor"
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $target
}
