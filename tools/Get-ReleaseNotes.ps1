[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$changelog = Join-Path $root 'CHANGELOG.md'
$lines = Get-Content -LiteralPath $changelog
$headingPattern = '^## \[' + [Regex]::Escape($Version) + '\](?:\s+-\s+\d{4}-\d{2}-\d{2})?\s*$'
$start = -1
for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match $headingPattern) { $start = $index + 1; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no release section for $Version." }

$end = $lines.Count
for ($index = $start; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match '^## \[') { $end = $index; break }
}
$notes = ($lines[$start..($end - 1)] -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($notes)) { throw "The changelog section for $Version is empty." }
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $notes + "`n", [Text.UTF8Encoding]::new($false))
