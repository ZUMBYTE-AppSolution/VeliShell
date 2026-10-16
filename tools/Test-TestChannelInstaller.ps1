[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [switch]$AllowMachineChanges)
$ErrorActionPreference = 'Stop'
if (-not $AllowMachineChanges) { throw 'Run this installer lifecycle test only on a disposable Windows runner or VM with -AllowMachineChanges.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskMsi = Join-Path $taskRoot "out\installer\VeliShell-$Version-win-x64.msi"
$taskTestExe = Join-Path $env:ProgramFiles 'Zumbyte\VeliShell Test\VeliShell.exe'
$taskStableExe = Join-Path $env:ProgramFiles 'Zumbyte\VeliShell\VeliShell.exe'
if (Test-Path -LiteralPath $taskTestExe) { throw 'An existing test installation must not be modified by this smoke test.' }
$taskStableHash = if (Test-Path -LiteralPath $taskStableExe) { (Get-FileHash -LiteralPath $taskStableExe -Algorithm SHA256).Hash } else { $null }
$taskMsiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
try {
    $taskInstallLog = Join-Path $taskRoot 'out\test-install.log'
    $taskInstall = Start-Process -FilePath $taskMsiexec -ArgumentList "/i `"$taskMsi`" /qn /norestart /l*v `"$taskInstallLog`"" -PassThru -Wait -WindowStyle Hidden
    if ($taskInstall.ExitCode -notin @(0,3010)) { throw "Test installation failed: $($taskInstall.ExitCode)" }
    foreach ($taskFile in @('VeliShell.exe','VeliShell.NativeFrame.dll','WINDOW-RECOVERY.cmd')) {
        if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $taskTestExe) $taskFile))) { throw "Test payload missing: $taskFile" }
    }
    if ((Get-Item -LiteralPath $taskTestExe).VersionInfo.ProductVersion -notmatch '-test\.') { throw 'Installer did not contain the test build.' }
} finally {
    if (Test-Path -LiteralPath $taskTestExe) {
        $taskUninstallLog = Join-Path $taskRoot 'out\test-uninstall.log'
        $taskUninstall = Start-Process -FilePath $taskMsiexec -ArgumentList "/x `"$taskMsi`" /qn /norestart /l*v `"$taskUninstallLog`"" -PassThru -Wait -WindowStyle Hidden
        if ($taskUninstall.ExitCode -notin @(0,3010)) { throw "Test uninstall failed: $($taskUninstall.ExitCode)" }
    }
    $taskStableAfter = if (Test-Path -LiteralPath $taskStableExe) { (Get-FileHash -LiteralPath $taskStableExe -Algorithm SHA256).Hash } else { $null }
    if ($taskStableAfter -ne $taskStableHash) { throw 'Stable installation changed during test lifecycle.' }
}
if (Test-Path -LiteralPath $taskTestExe) { throw 'Test application remained after uninstall.' }
Write-Host 'Test MSI install/uninstall passed; stable installation unchanged.'
