[CmdletBinding()]
param([switch]$Run, [switch]$Portable, [switch]$TestsOnly)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root 'out'
$script:Dotnet = $null
$script:LastLog = Join-Path $Out 'build.log'

function Invoke-Dotnet {
    param([string[]]$Arguments, [string]$Log)
    $script:LastLog = $Log
    Write-Host ('> dotnet ' + ($Arguments -join ' ')) -ForegroundColor Cyan
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $script:Dotnet @Arguments 2>&1 | Tee-Object -FilePath $Log
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $oldPreference }
    if ($code -ne 0) { throw "dotnet ist mit Code $code fehlgeschlagen. Protokoll: $Log" }
}

function Get-VeliShellRelativePath([string]$BasePath, [string]$TargetPath) {
    $baseFull = [IO.Path]::GetFullPath($BasePath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $targetFull = [IO.Path]::GetFullPath($TargetPath)
    $baseUri = New-Object Uri($baseFull)
    $targetUri = New-Object Uri($targetFull)
    if ($baseUri.Scheme -ne $targetUri.Scheme) {
        throw "The paths are on different volumes: '$baseFull' and '$targetFull'."
    }
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

Push-Location $Root
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Dieses Startskript bitte innerhalb der Windows-VM ausfuehren.'
    }
    New-Item -ItemType Directory -Path $Out -Force | Out-Null
    $found = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($found) { $script:Dotnet = $found.Source }
    if (-not $script:Dotnet) {
        $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if (Test-Path $candidate) { $script:Dotnet = $candidate }
    }
    if (-not $script:Dotnet) {
        throw 'Das .NET 10 SDK fehlt. In einem Terminal installieren: winget install --id Microsoft.DotNet.SDK.10 --exact --source winget'
    }
    $sdks = (& $script:Dotnet --list-sdks 2>&1 | Out-String)
    if ($sdks -notmatch '(?m)^10\.0\.\d+\s') {
        throw 'Es wird ein stabiles .NET 10 SDK benoetigt. Bitte installieren: winget install --id Microsoft.DotNet.SDK.10 --exact --source winget'
    }

    # Only this process inherits these variables; no machine settings are changed.
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $rid = switch ($arch) {
        'ARM64' { 'win-arm64' }
        'AMD64' { 'win-x64' }
        default { throw "Nicht unterstuetzte Architektur: $arch. Fuer diese Vorschau wird Windows x64 oder ARM64 benoetigt." }
    }
    [xml]$versionProps = Get-Content (Join-Path $Root 'Directory.Build.props') -Raw
    $productVersion = [string]$versionProps.Project.PropertyGroup.Version
    $runtimeNoticeVersion = [string]$versionProps.Project.PropertyGroup.VeliShellRuntimeNoticeVersion
    if ($runtimeNoticeVersion -notmatch '^10\.0\.\d+$') {
        throw "Ungueltige oder fehlende .NET-Hinweisversion in Directory.Build.props: '$runtimeNoticeVersion'"
    }
    Write-Host "VeliShell $productVersion | $rid" -ForegroundColor Green
    Write-Host 'Keine Systemdateien, Taskleisten-Einstellungen oder Autostart-Eintraege werden veraendert.'

    Invoke-Dotnet -Arguments @('run', '--project', 'tests/VeliShell.Core.Tests/VeliShell.Core.Tests.csproj', '-c', 'Release') -Log (Join-Path $Out 'tests.log')
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/VeliShell.UpdateService.Tests/VeliShell.UpdateService.Tests.csproj', '-c', 'Release') -Log (Join-Path $Out 'service-tests.log')
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/VeliShell.Desktop.Updater.Tests/VeliShell.Desktop.Updater.Tests.csproj', '-c', 'Release') -Log (Join-Path $Out 'desktop-updater-tests.log')
    if ($TestsOnly) { Write-Host 'Tests abgeschlossen.' -ForegroundColor Green; exit 0 }

    $Destination = Join-Path $Out $(if ($Portable) { 'portable' } else { 'app' })
    $outFull = [IO.Path]::GetFullPath($Out).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $destinationFull = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($destinationFull) -ne $outFull) {
        throw "Unsicheres Ausgabeziel: $destinationFull"
    }
    # Keep staging names short: the longest versioned notice path otherwise
    # exceeds the legacy MAX_PATH boundary used by some Windows file APIs.
    $Staging = Join-Path $Out ('.vp-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    $ServiceStaging = Join-Path $Out ('.vs-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    foreach ($stagingPath in @($Staging, $ServiceStaging)) {
        $stagingFull = [IO.Path]::GetFullPath($stagingPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::GetDirectoryName($stagingFull) -ne $outFull) {
            throw "Unsicheres Staging-Ziel: $stagingFull"
        }
    }
    $Exe = Join-Path $Destination 'VeliShell.exe'
    $running = Get-Process -Name 'VeliShell' -ErrorAction SilentlyContinue
    if ($running) { throw 'VeliShell laeuft noch. Bitte ueber sein Dock-Menue beenden und START.cmd erneut ausfuehren. Das Skript beendet keine Prozesse automatisch.' }
    $contained = if ($Portable) { 'true' } else { 'false' }
    try {
        Invoke-Dotnet -Arguments @('publish', 'src/VeliShell.Desktop/VeliShell.Desktop.csproj', '-c', 'Release', '-r', $rid, '--self-contained', $contained, '-o', $Staging, '-p:UseAppHost=true') -Log (Join-Path $Out 'build.log')
        $stagedExe = Join-Path $Staging 'VeliShell.exe'
        if (-not (Test-Path $stagedExe)) { throw "Die EXE wurde nicht erstellt: $stagedExe" }
        $requiredLegalFiles = @(
            'LICENSE',
            'THIRD-PARTY-NOTICES.md',
            'THIRD-PARTY-LICENSES\README.md',
            "THIRD-PARTY-LICENSES\Microsoft.NETCore.App.Runtime.win-x64-$runtimeNoticeVersion-LICENSE.txt",
            "THIRD-PARTY-LICENSES\Microsoft.NETCore.App.Runtime.win-x64-$runtimeNoticeVersion-THIRD-PARTY-NOTICES.txt",
            "THIRD-PARTY-LICENSES\Microsoft.WindowsDesktop.App.Runtime.win-x64-$runtimeNoticeVersion-LICENSE.txt",
            "THIRD-PARTY-LICENSES\Microsoft.WindowsDesktop.App.Runtime.win-x64-$runtimeNoticeVersion-THIRD-PARTY-NOTICES.txt"
        )
        foreach ($relativeLegalFile in $requiredLegalFiles) {
            $publishedLegalFile = Join-Path $Staging $relativeLegalFile
            if (-not (Test-Path -LiteralPath $publishedLegalFile -PathType Leaf)) {
                throw "Ein erforderlicher Lizenz- oder Hinweistext fehlt im Publish: $publishedLegalFile"
            }
        }
        if ($Portable) {
            $runtimeVersionPattern = '^' + [Regex]::Escape($runtimeNoticeVersion) + '(?:[-+]|$)'
            foreach ($runtimeBinary in @('System.Private.CoreLib.dll', 'PresentationFramework.dll')) {
                $runtimeBinaryPath = Join-Path $Staging $runtimeBinary
                if (-not (Test-Path -LiteralPath $runtimeBinaryPath -PathType Leaf)) {
                    throw "Die Self-contained-Runtime ist unvollstaendig: $runtimeBinaryPath"
                }
                $publishedRuntimeVersion = (Get-Item -LiteralPath $runtimeBinaryPath).VersionInfo.ProductVersion
                if ($publishedRuntimeVersion -notmatch $runtimeVersionPattern) {
                    throw "Die mitgelieferte Runtime '$runtimeBinary' ($publishedRuntimeVersion) passt nicht zu den Lizenztexten fuer $runtimeNoticeVersion. Bitte THIRD-PARTY-LICENSES und VeliShellRuntimeNoticeVersion gemeinsam aktualisieren."
                }
            }
        }
        if ($Portable) {
            Invoke-Dotnet -Arguments @(
                'publish', 'src/VeliShell.UpdateService/VeliShell.UpdateService.csproj',
                '-c', 'Release',
                '-r', $rid,
                '--self-contained', 'true',
                '-o', $ServiceStaging,
                '-p:UseAppHost=true',
                '-p:DebugType=None',
                '-p:DebugSymbols=false'
            ) -Log (Join-Path $Out 'service-build.log')
            $servicePayload = @(
                'VeliShell.UpdateService.exe',
                'VeliShell.UpdateService.dll',
                'VeliShell.UpdateService.deps.json',
                'VeliShell.UpdateService.runtimeconfig.json'
            )
            $sharedRelativePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($sharedFile in (Get-ChildItem -LiteralPath $Staging -File -Recurse)) {
                $sharedRelativePath = Get-VeliShellRelativePath $Staging $sharedFile.FullName
                [void]$sharedRelativePaths.Add($sharedRelativePath)
            }
            $missingSharedDependencies = @(
                Get-ChildItem -LiteralPath $ServiceStaging -File -Recurse |
                    Where-Object {
                        $serviceRelativePath = Get-VeliShellRelativePath $ServiceStaging $_.FullName
                        $_.Extension -ne '.pdb' -and
                        $serviceRelativePath -notin $servicePayload -and
                        -not $sharedRelativePaths.Contains($serviceRelativePath)
                    }
            )
            if ($missingSharedDependencies.Count -gt 0) {
                $missingNames = ($missingSharedDependencies | ForEach-Object Name | Sort-Object -Unique) -join ', '
                throw "Der Updatepruefdienst benoetigt Dateien, die im gemeinsamen Laufzeitordner fehlen: $missingNames"
            }
            foreach ($serviceFile in $servicePayload) {
                $stagedServiceFile = Join-Path $ServiceStaging $serviceFile
                if (-not (Test-Path -LiteralPath $stagedServiceFile -PathType Leaf)) {
                    throw "Der optionale Updatepruefdienst wurde nicht vollstaendig erstellt: $stagedServiceFile"
                }
                Copy-Item -LiteralPath $stagedServiceFile -Destination (Join-Path $Staging $serviceFile)
            }
            & (Join-Path $Staging 'VeliShell.UpdateService.exe')
            $serviceSmokeExit = $LASTEXITCODE
            if ($serviceSmokeExit -ne 1063) {
                throw "Der Updatepruefdienst konnte im gemeinsamen Laufzeitordner nicht geladen werden (SCM-Code $serviceSmokeExit statt 1063)."
            }
            # 1063 is the expected SCM-only startup result. Do not leak that
            # successful smoke-test code as the PowerShell script exit code.
            $global:LASTEXITCODE = 0
        }
        if (Test-Path -LiteralPath $Destination) {
            Remove-Item -LiteralPath $Destination -Recurse -Force
        }
        Move-Item -LiteralPath $Staging -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $Staging) {
            Remove-Item -LiteralPath $Staging -Recurse -Force
        }
        if (Test-Path -LiteralPath $ServiceStaging) {
            Remove-Item -LiteralPath $ServiceStaging -Recurse -Force
        }
    }
    Write-Host "Fertig: $Exe" -ForegroundColor Green
    if ($Run) { Start-Process -FilePath $Exe -WorkingDirectory $Destination }
    if ($Portable) { Write-Host 'Den gesamten Ordner out\portable kopieren, nicht nur die EXE.' }
    $global:LASTEXITCODE = 0
}
catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ('Protokoll, sofern erstellt: ' + $script:LastLog)
    Write-Host 'Nicht wahllos weitere Tools installieren. Die Meldung oder die Logdatei zur Fehleranalyse weitergeben.'
    exit 1
}
finally { Pop-Location }
