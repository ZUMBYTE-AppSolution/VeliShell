[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$Version,
    [switch]$AllowMachineChanges
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $projectRoot 'Directory.Build.props'
$outputDirectory = Join-Path $projectRoot 'out'
$previousVersion = '0.3.1'
$legacyProductCode = '{F1AA00A2-F2D6-296C-B526-A1BBFE01C287}'
$expectedUpgradeCode = '{B5C35798-4912-4765-97FC-279D22D1127B}'
$legacyServiceName = 'VeliShell.UpdateService'
$legacyFeature = 'UpdateServiceFeature'
$legacyServiceControl = 'VeliShellUpdateServiceControl'
$legacyServiceControlEvent = 163
$legacyServiceControlWithoutStart = 160
$legacyMsiName = "VeliShell-$previousVersion-win-x64.msi"
$legacyMsiSha256 = '695b2ceb461f46b6e0ed4fc5d8386b9edea3b843c8828fe626fad475d7c2197a'
$legacyReleaseBase = "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/download/v$previousVersion"

if (-not $IsWindows) {
    throw 'The installer lifecycle test requires Windows.'
}
if (-not $AllowMachineChanges) {
    throw 'This test installs and removes per-machine MSI packages. Re-run with -AllowMachineChanges on a disposable Windows test machine.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The installer lifecycle test requires an elevated administrator session.'
}

[xml]$properties = Get-Content -LiteralPath $versionFile -Raw
if (-not $Version) {
    $Version = [string]$properties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "The current installer version is not stable SemVer: '$Version'."
}
if ([Version]$Version -le [Version]$previousVersion) {
    throw "The current version $Version must be newer than the upgrade baseline $previousVersion."
}
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot "out\installer\VeliShell-$Version-win-x64.msi"
}
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "The current installer was not found: $InstallerPath"
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$installDirectory = Join-Path $env:ProgramFiles 'Zumbyte\VeliShell'
$legacyPayloadDirectory = Join-Path $installDirectory 'UpdateService'
$legacyProgramDataDirectory = Join-Path $env:ProgramData 'VeliShell'
$commonPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
$startMenuDirectory = Join-Path $commonPrograms 'VeliShell'
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('VeliShell-InstallerLifecycle-' + [Guid]::NewGuid().ToString('N'))
$officialLegacyMsi = Join-Path $tempDirectory $legacyMsiName
$legacyChecksums = Join-Path $tempDirectory 'SHA256SUMS.txt'
$patchedLegacyMsi = Join-Path $tempDirectory "VeliShell-$previousVersion-service-no-start.msi"
$currentProductCode = $null
$ownsMachineState = $false
$capturedFailure = $null

if (-not ('VeliShell.InstallerLifecycle.WindowsInstallerNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VeliShell.InstallerLifecycle
{
    public static class WindowsInstallerNative
    {
        private const uint ErrorSuccess = 0;
        private const uint ErrorMoreData = 234;
        private const uint ErrorUnknownProduct = 1605;
        private const uint MachineContext = 4;

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoExW")]
        private static extern uint MsiGetProductInfoEx(
            string productCode,
            string userSid,
            uint context,
            string property,
            StringBuilder value,
            ref uint valueLength);

        public static string GetMachineProductProperty(string productCode, string property)
        {
            uint length = 0;
            uint result = MsiGetProductInfoEx(
                productCode, null, MachineContext, property, null, ref length);
            if (result == ErrorUnknownProduct)
            {
                return null;
            }
            if (result != ErrorSuccess && result != ErrorMoreData)
            {
                ThrowIfFailed(result, property);
            }

            var value = new StringBuilder(checked((int)length + 1));
            uint capacity = (uint)value.Capacity;
            result = MsiGetProductInfoEx(
                productCode, null, MachineContext, property, value, ref capacity);
            if (result == ErrorUnknownProduct)
            {
                return null;
            }
            ThrowIfFailed(result, property);
            return value.ToString();
        }

        private static void ThrowIfFailed(uint result, string property)
        {
            if (result == ErrorSuccess)
            {
                return;
            }
            string detail = new Win32Exception((int)result).Message;
            throw new InvalidOperationException(
                $"Windows Installer could not read '{property}' (error {result}: {detail}).");
        }
    }
}
'@
}

$msiInstallStateDefault = 5

function Release-ComObject([object]$Value) {
    if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
    }
}

function Get-MsiProperty([string]$Path, [string]$Name) {
    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $safeName = $Name.Replace("'", "''")
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path), 0)
        $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$safeName'")
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "MSI property '$Name' is missing from '$Path'."
        }
        return [string]$record.StringData(1)
    }
    finally {
        Release-ComObject $record
        Release-ComObject $view
        Release-ComObject $database
        Release-ComObject $installer
    }
}

function Get-MsiServiceControlEvent([string]$Path, [string]$ControlId) {
    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $safeId = $ControlId.Replace("'", "''")
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path), 0)
        $view = $database.OpenView("SELECT ``Event`` FROM ``ServiceControl`` WHERE ``ServiceControl`` = '$safeId'")
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "ServiceControl row '$ControlId' is missing from '$Path'."
        }
        return [int]$record.IntegerData(1)
    }
    finally {
        Release-ComObject $record
        Release-ComObject $view
        Release-ComObject $database
        Release-ComObject $installer
    }
}

function Disable-LegacyServiceStart([string]$Path) {
    $before = Get-MsiServiceControlEvent $Path $legacyServiceControl
    if ($before -ne $legacyServiceControlEvent) {
        throw "Unexpected legacy ServiceControl event value $before; expected $legacyServiceControlEvent. The baseline MSI may have changed."
    }

    $installer = $null
    $database = $null
    $view = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path), 1)
        $view = $database.OpenView(
            "UPDATE ``ServiceControl`` SET ``Event`` = $legacyServiceControlWithoutStart WHERE ``ServiceControl`` = '$legacyServiceControl'")
        $view.Execute()
        $database.Commit()
    }
    finally {
        Release-ComObject $view
        Release-ComObject $database
        Release-ComObject $installer
    }

    $after = Get-MsiServiceControlEvent $Path $legacyServiceControl
    if ($after -ne $legacyServiceControlWithoutStart) {
        throw "Could not suppress the legacy service start action. ServiceControl event is $after instead of $legacyServiceControlWithoutStart."
    }
}

function Invoke-TrustedDownload([string]$Uri, [string]$Destination) {
    $source = [Uri]$Uri
    if ($source.Scheme -ne 'https' -or $source.Host -ne 'github.com' -or
        -not $source.AbsolutePath.StartsWith('/ZUMBYTE-AppSolution/VeliShell/releases/download/', [StringComparison]::Ordinal)) {
        throw "Refusing a download outside the official VeliShell GitHub release path: $Uri"
    }

    $lastError = $null
    foreach ($attempt in 1..3) {
        try {
            $response = Invoke-WebRequest -Uri $source -Headers @{ 'User-Agent' = 'VeliShell-InstallerLifecycle-Test' } `
                -MaximumRedirection 10 -OutFile $Destination -PassThru
            $finalUri = $response.BaseResponse.RequestMessage.RequestUri
            $finalHost = $finalUri.Host
            if ($finalUri.Scheme -ne 'https' -or
                ($finalHost -ne 'github.com' -and -not $finalHost.EndsWith('.githubusercontent.com', [StringComparison]::OrdinalIgnoreCase))) {
                Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
                throw "GitHub redirected the release asset to an unexpected host: $finalHost"
            }
            return
        }
        catch {
            $lastError = $_
            Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
            if ($attempt -lt 3) { Start-Sleep -Seconds (2 * $attempt) }
        }
    }
    throw "Could not download '$Uri' after three attempts: $($lastError.Exception.Message)"
}

function Get-MsiProductInfoValue([string]$ProductCode, [string]$Property, [switch]$AllowUnknownProduct) {
    $value = [VeliShell.InstallerLifecycle.WindowsInstallerNative]::GetMachineProductProperty(
        $ProductCode, $Property)
    if ($null -eq $value -and -not $AllowUnknownProduct) {
        throw "Windows Installer product $ProductCode is not registered in the per-machine context."
    }
    return $value
}

function Get-ProductRegistration([string]$ProductCode) {
    $productState = Get-MsiProductInfoValue $ProductCode 'State' -AllowUnknownProduct
    if ($null -eq $productState) {
        return $null
    }

    return [pscustomobject]@{
        ProductCode = $ProductCode
        ProductState = [int]$productState
        DisplayName = Get-MsiProductInfoValue $ProductCode 'InstalledProductName'
        DisplayVersion = Get-MsiProductInfoValue $ProductCode 'VersionString'
        InstallContext = 'Machine'
    }
}

function Assert-ProductInstalled([string]$ProductCode, [string]$ExpectedVersion) {
    $registration = Get-ProductRegistration $ProductCode
    if ($null -eq $registration) {
        throw "MSI product $ProductCode is not registered after installation."
    }
    if ($registration.ProductState -ne $msiInstallStateDefault) {
        throw "MSI product $ProductCode has unexpected Windows Installer state $($registration.ProductState); expected $msiInstallStateDefault (installed locally)."
    }
    if ($registration.DisplayName -ne 'VeliShell' -or $registration.DisplayVersion -ne $ExpectedVersion) {
        throw "MSI product $ProductCode has unexpected Windows Installer registration: '$($registration.DisplayName)' '$($registration.DisplayVersion)'."
    }
}

function Assert-ProductAbsent([string]$ProductCode) {
    if ($null -ne (Get-ProductRegistration $ProductCode)) {
        throw "MSI product $ProductCode is still registered."
    }
}

function Wait-Condition([scriptblock]$Condition, [string]$FailureMessage, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ([bool](& $Condition)) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $FailureMessage
}

function Invoke-Msi([ValidateSet('Install', 'Uninstall')][string]$Operation,
                    [string]$Target,
                    [string]$LogName,
                    [string[]]$Properties = @()) {
    $msiExe = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $logPath = Join-Path $outputDirectory $LogName
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue

    $arguments = [Collections.Generic.List[string]]::new()
    [void]$arguments.Add($(if ($Operation -eq 'Install') { '/i' } else { '/x' }))
    [void]$arguments.Add($Target)
    [void]$arguments.Add('/qn')
    [void]$arguments.Add('/norestart')
    [void]$arguments.Add('REBOOT=ReallySuppress')
    foreach ($property in $Properties) { [void]$arguments.Add($property) }
    [void]$arguments.Add('/l*v')
    [void]$arguments.Add($logPath)

    Write-Host "$Operation MSI: $Target"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $msiExe
    $startInfo.UseShellExecute = $false
    foreach ($argument in $arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -notin @(0, 3010)) {
        $tail = if (Test-Path -LiteralPath $logPath) {
            (Get-Content -LiteralPath $logPath -Tail 60 -ErrorAction SilentlyContinue) -join [Environment]::NewLine
        } else { '(no MSI log was created)' }
        throw "msiexec $Operation failed with exit code $exitCode. Log: $logPath`n$tail"
    }
}

function Assert-CurrentPayloadPresent {
    $executable = Join-Path $installDirectory 'VeliShell.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The installed application executable is missing: $executable"
    }
    $shortcut = Join-Path $startMenuDirectory 'VeliShell.lnk'
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) {
        throw "The installed Start menu shortcut is missing: $shortcut"
    }
}

function Assert-LegacyArtifactsAbsent {
    if ($null -ne (Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue)) {
        throw "The removed legacy service still exists: $legacyServiceName"
    }
    if (Test-Path -LiteralPath $legacyPayloadDirectory) {
        throw "The removed legacy UpdateService payload still exists: $legacyPayloadDirectory"
    }
    if (Test-Path -LiteralPath $legacyProgramDataDirectory) {
        throw "The removed legacy service data directory still exists: $legacyProgramDataDirectory"
    }
}

function Assert-InstallRemoved {
    Wait-Condition { -not (Test-Path -LiteralPath $installDirectory) } `
        "The install directory remains after uninstall: $installDirectory"
    Wait-Condition { -not (Test-Path -LiteralPath $startMenuDirectory) } `
        "The Start menu directory remains after uninstall: $startMenuDirectory"
}

function Uninstall-ProductIfPresent([string]$ProductCode, [string]$LogName) {
    if ($null -ne (Get-ProductRegistration $ProductCode)) {
        Invoke-Msi -Operation Uninstall -Target $ProductCode -LogName $LogName
        Wait-Condition { $null -eq (Get-ProductRegistration $ProductCode) } `
            "MSI product $ProductCode is still registered after cleanup uninstall."
    }
}

function Remove-OwnedDirectory([string]$Path, [string]$RequiredParent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $parent = [IO.Path]::GetFullPath($RequiredParent).TrimEnd('\')
    if (-not $fullPath.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase) -or $fullPath -eq $parent) {
        throw "Refusing to clean an unexpected path: $fullPath"
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

try {
    $currentProductCode = Get-MsiProperty $InstallerPath 'ProductCode'
    $currentMsiVersion = Get-MsiProperty $InstallerPath 'ProductVersion'
    $currentUpgradeCode = Get-MsiProperty $InstallerPath 'UpgradeCode'
    if ($currentMsiVersion -ne $Version) {
        throw "Installer ProductVersion '$currentMsiVersion' does not match requested version '$Version'."
    }
    if ($currentUpgradeCode -ne $expectedUpgradeCode) {
        throw "Installer UpgradeCode '$currentUpgradeCode' does not match VeliShell's stable UpgradeCode '$expectedUpgradeCode'."
    }
    if ($currentProductCode -eq $legacyProductCode) {
        throw 'The current and legacy MSI packages unexpectedly share a ProductCode.'
    }

    Assert-ProductAbsent $currentProductCode
    Assert-ProductAbsent $legacyProductCode
    if ($null -ne (Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue)) {
        throw "The test machine already contains the legacy service '$legacyServiceName'."
    }
    foreach ($path in @($installDirectory, $legacyProgramDataDirectory, $startMenuDirectory)) {
        if (Test-Path -LiteralPath $path) {
            throw "The test requires a clean machine, but this path already exists: $path"
        }
    }
    if ($null -ne (Get-Process -Name 'VeliShell' -ErrorAction SilentlyContinue)) {
        throw 'A VeliShell process is already running. Close it before running the installer lifecycle test.'
    }
    $ownsMachineState = $true

    New-Item -ItemType Directory -Path $tempDirectory | Out-Null
    Invoke-TrustedDownload "$legacyReleaseBase/SHA256SUMS.txt" $legacyChecksums
    Invoke-TrustedDownload "$legacyReleaseBase/$legacyMsiName" $officialLegacyMsi

    $checksumPattern = '^(?<hash>[a-fA-F0-9]{64})\s+\*?' + [Regex]::Escape($legacyMsiName) + '$'
    $checksumMatches = @(Get-Content -LiteralPath $legacyChecksums | ForEach-Object {
        if ($_ -match $checksumPattern) { $Matches['hash'].ToLowerInvariant() }
    })
    if ($checksumMatches.Count -ne 1) {
        throw "The official v$previousVersion SHA256SUMS.txt does not contain exactly one checksum for $legacyMsiName."
    }
    if ($checksumMatches[0] -ne $legacyMsiSha256) {
        throw "The published checksum for $legacyMsiName changed from the pinned v$previousVersion value."
    }
    $downloadHash = (Get-FileHash -LiteralPath $officialLegacyMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadHash -ne $checksumMatches[0]) {
        throw "The downloaded v$previousVersion MSI failed SHA-256 verification. Expected $($checksumMatches[0]), got $downloadHash."
    }
    if ((Get-MsiProperty $officialLegacyMsi 'ProductCode') -ne $legacyProductCode -or
        (Get-MsiProperty $officialLegacyMsi 'ProductVersion') -ne $previousVersion -or
        (Get-MsiProperty $officialLegacyMsi 'UpgradeCode') -ne $expectedUpgradeCode) {
        throw "The verified v$previousVersion MSI has unexpected Windows Installer identity metadata."
    }
    Write-Host "Verified official VeliShell $previousVersion MSI: $downloadHash" -ForegroundColor Green

    Write-Host '--- Fresh install and uninstall of the current MSI ---' -ForegroundColor Cyan
    Invoke-Msi -Operation Install -Target $InstallerPath -LogName 'installer-smoke-fresh-install.log'
    Assert-ProductInstalled $currentProductCode $Version
    Assert-CurrentPayloadPresent
    Assert-LegacyArtifactsAbsent
    Invoke-Msi -Operation Uninstall -Target $currentProductCode -LogName 'installer-smoke-fresh-uninstall.log'
    Assert-ProductAbsent $currentProductCode
    Assert-InstallRemoved

    Write-Host "--- Normal upgrade from $previousVersion to $Version ---" -ForegroundColor Cyan
    Invoke-Msi -Operation Install -Target $officialLegacyMsi -LogName 'installer-smoke-upgrade-old-install.log'
    Assert-ProductInstalled $legacyProductCode $previousVersion
    Assert-ProductAbsent $currentProductCode
    Assert-LegacyArtifactsAbsent
    Invoke-Msi -Operation Install -Target $InstallerPath -LogName 'installer-smoke-upgrade-current-install.log'
    Assert-ProductAbsent $legacyProductCode
    Assert-ProductInstalled $currentProductCode $Version
    Assert-CurrentPayloadPresent
    Assert-LegacyArtifactsAbsent
    Invoke-Msi -Operation Uninstall -Target $currentProductCode -LogName 'installer-smoke-upgrade-current-uninstall.log'
    Assert-ProductAbsent $currentProductCode
    Assert-InstallRemoved

    Write-Host "--- Upgrade cleanup for the retired v$previousVersion service feature ---" -ForegroundColor Cyan
    Copy-Item -LiteralPath $officialLegacyMsi -Destination $patchedLegacyMsi
    # 163 starts the service during install. 160 retains only stop/delete during uninstall.
    Disable-LegacyServiceStart $patchedLegacyMsi
    Invoke-Msi -Operation Install -Target $patchedLegacyMsi -LogName 'installer-smoke-service-old-install.log' `
        -Properties @("ADDLOCAL=VeliShellComplete,$legacyFeature")
    Assert-ProductInstalled $legacyProductCode $previousVersion
    $legacyService = Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue
    if ($null -eq $legacyService) {
        throw "The patched legacy MSI did not install service '$legacyServiceName'."
    }
    if ($legacyService.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw "The patched legacy service unexpectedly started; status is '$($legacyService.Status)'."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $legacyPayloadDirectory 'VeliShell.UpdateService.exe') -PathType Leaf)) {
        throw 'The patched legacy MSI did not install its UpdateService payload.'
    }
    if (-not (Test-Path -LiteralPath $legacyProgramDataDirectory -PathType Container)) {
        throw 'The patched legacy MSI did not create its ProgramData directory.'
    }

    Invoke-Msi -Operation Install -Target $InstallerPath -LogName 'installer-smoke-service-upgrade.log'
    Assert-ProductAbsent $legacyProductCode
    Assert-ProductInstalled $currentProductCode $Version
    Assert-CurrentPayloadPresent
    Wait-Condition { $null -eq (Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue) } `
        "The v$previousVersion service remained registered after the upgrade." 30
    Assert-LegacyArtifactsAbsent
    Invoke-Msi -Operation Uninstall -Target $currentProductCode -LogName 'installer-smoke-service-current-uninstall.log'
    Assert-ProductAbsent $currentProductCode
    Assert-InstallRemoved

    Write-Host "Installer lifecycle passed: fresh install/uninstall, $previousVersion upgrade, and retired-service cleanup." -ForegroundColor Green
}
catch {
    $capturedFailure = $_
}
finally {
    if ($ownsMachineState) {
        try { if ($currentProductCode) { Uninstall-ProductIfPresent $currentProductCode 'installer-smoke-cleanup-current.log' } }
        catch { Write-Warning "Could not clean up current MSI product $currentProductCode`: $($_.Exception.Message)" }
        try { Uninstall-ProductIfPresent $legacyProductCode 'installer-smoke-cleanup-legacy.log' }
        catch { Write-Warning "Could not clean up legacy MSI product $legacyProductCode`: $($_.Exception.Message)" }

        try {
            $remainingService = Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue
            if ($null -ne $remainingService) {
                if ($remainingService.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
                    & (Join-Path $env:SystemRoot 'System32\sc.exe') stop $legacyServiceName | Out-Null
                    Start-Sleep -Seconds 1
                }
                & (Join-Path $env:SystemRoot 'System32\sc.exe') delete $legacyServiceName | Out-Null
            }
        }
        catch { Write-Warning "Could not clean up legacy service '$legacyServiceName': $($_.Exception.Message)" }

        try { Remove-OwnedDirectory $installDirectory (Join-Path $env:ProgramFiles 'Zumbyte') }
        catch { Write-Warning "Could not clean up '$installDirectory': $($_.Exception.Message)" }
        try { Remove-OwnedDirectory $legacyProgramDataDirectory $env:ProgramData }
        catch { Write-Warning "Could not clean up '$legacyProgramDataDirectory': $($_.Exception.Message)" }
        try { Remove-OwnedDirectory $startMenuDirectory $commonPrograms }
        catch { Write-Warning "Could not clean up '$startMenuDirectory': $($_.Exception.Message)" }
    }

    if (Test-Path -LiteralPath $tempDirectory) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempDirectory)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if ($resolvedTemp.StartsWith($tempRoot + '\VeliShell-InstallerLifecycle-', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
        } else {
            Write-Warning "Refusing to clean an unexpected temporary path: $resolvedTemp"
        }
    }
}

if ($null -ne $capturedFailure) {
    throw $capturedFailure
}
