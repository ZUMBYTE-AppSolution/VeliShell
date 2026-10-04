[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$serviceName = 'VeliShell.UpdateService'
$installer = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "Installer not found: $installer"
}
if (-not $LogPath) {
    $LogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'out\installed-service-test.log'
}
$LogPath = [IO.Path]::GetFullPath($LogPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The installed-service integration test requires an elevated Windows runner.'
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Refusing to replace an existing $serviceName service."
}

function Invoke-Msi([string[]]$Arguments) {
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $Arguments -Wait -PassThru
    return $process.ExitCode
}

function Write-ServiceDiagnostics {
    $diagnosticPath = [IO.Path]::ChangeExtension($LogPath, '.events.log')
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("Captured: $([DateTimeOffset]::Now.ToString('O'))")
    $lines.Add('')
    foreach ($command in @('queryex', 'qc')) {
        $lines.Add("> sc.exe $command $serviceName")
        $lines.AddRange([string[]]@(& "$env:SystemRoot\System32\sc.exe" $command $serviceName 2>&1 | Out-String))
        $global:LASTEXITCODE = 0
    }
    $lines.Add('Recent Service Control Manager events:')
    $events = Get-WinEvent -FilterHashtable @{
        LogName = 'System'
        ProviderName = 'Service Control Manager'
        StartTime = (Get-Date).AddMinutes(-10)
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.Id -in @(7000, 7009, 7011, 7023, 7024, 7031, 7034, 7045) -or
        $_.Message -match 'VeliShell|UpdateService'
    } | Select-Object TimeCreated, Id, LevelDisplayName, Message
    $lines.Add(($events | Format-List | Out-String))
    [IO.File]::WriteAllLines($diagnosticPath, $lines, [Text.UTF8Encoding]::new($false))
}

$installed = $false
$testError = $null
try {
    $installCode = Invoke-Msi @(
        '/i', ('"{0}"' -f $installer),
        '/qn', '/norestart', 'REBOOT=ReallySuppress', 'ADDLOCAL=ALL',
        '/L*v', ('"{0}"' -f $LogPath)
    )
    if ($installCode -notin @(0, 3010)) {
        Write-ServiceDiagnostics
        throw "MSI installation with the optional service failed with exit code $installCode. Log: $LogPath"
    }
    $installed = $true

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service) { $service.Refresh() }
        if ($service -and $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $service -or $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        $state = if ($service) { $service.Status } else { 'missing' }
        Write-ServiceDiagnostics
        throw "The installed update service did not reach Running state (state: $state). Log: $LogPath"
    }

    $configuration = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if (-not $configuration) { throw 'The installed service configuration could not be read.' }
    if ($configuration.StartName -notmatch 'LocalService|Lokaler Dienst') {
        throw "The installed service account is unexpected: $($configuration.StartName)"
    }
    if ($configuration.PathName -notmatch '[\\/]UpdateService[\\/]VeliShell\.UpdateService\.exe') {
        throw "The service is not using its isolated runtime directory: $($configuration.PathName)"
    }

    $statusPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'VeliShell\update-status.json'
    $statusDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $statusPath -PathType Leaf) -and [DateTime]::UtcNow -lt $statusDeadline) {
        $service.Refresh()
        if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Write-ServiceDiagnostics
            throw "The update service stopped before publishing its status (state: $($service.Status))."
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
        throw "The running update service did not publish its status file: $statusPath"
    }
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$status.installedVersion)) {
        throw 'The update-service status file does not contain an installed version.'
    }
    Write-Host "Installed service integration test passed: $($configuration.PathName)" -ForegroundColor Green
}
catch {
    $testError = $_
}
finally {
    if ($installed -or (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
        $uninstallLog = [IO.Path]::ChangeExtension($LogPath, '.uninstall.log')
        $uninstallCode = Invoke-Msi @(
            '/x', ('"{0}"' -f $installer),
            '/qn', '/norestart', 'REBOOT=ReallySuppress',
            '/L*v', ('"{0}"' -f $uninstallLog)
        )
        if ($uninstallCode -notin @(0, 1605, 3010) -and $null -eq $testError) {
            $testError = [InvalidOperationException]::new(
                "MSI cleanup failed with exit code $uninstallCode. Log: $uninstallLog")
        }
    }
    $global:LASTEXITCODE = 0
}
if ($testError) { throw $testError }
