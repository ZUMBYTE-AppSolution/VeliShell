[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not (Test-Path -LiteralPath '.git')) {
        Write-Host 'Public repository check skipped: this exported working folder has no .git metadata.'
        return
    }

    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Could not list tracked repository files.' }
    $forbidden = @(
        '(^|/)AGENTS\.md$',
        '(^|/)(AGENT|AGENTS|ROADMAP|PLAN|DEVELOPER|DEV-NOTES|INTERNAL-NOTES)([-_.][^/]*)?\.(md|txt|json|ya?ml)$',
        '(^|/)(work|out|backups|outputs)/',
        '^BUILD-INFO\.txt$',
        '^docs/(ROADMAP|VERIFICATION|TEST-CHECKLIST|DESIGN-CONTRACT|SOURCES|RELEASING)\.md$',
        '^docs/static-checks\.json$',
        '^LIES-MICH-ZUERST\.txt$',
        '^WINDOWS-BUILD-LESEN\.md$',
        '^packaging/LIES-MICH\.txt$'
    )
    $violations = @($tracked | Where-Object {
        $path = $_.Replace('\', '/')
        $forbidden | Where-Object { $path -match $_ }
    })
    if ($violations.Count -gt 0) {
        throw "Internal-only files are tracked in the public repository:`n$($violations -join "`n")"
    }
    Write-Host 'Public repository boundary passed.' -ForegroundColor Green
}
finally { Pop-Location }
