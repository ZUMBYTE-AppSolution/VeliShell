[CmdletBinding()]
param(
    [string]$Version,
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $projectRoot 'Directory.Build.props'
$installerProject = Join-Path $projectRoot 'packaging\VeliShell.Installer\VeliShell.Installer.wixproj'
$generatedDirectory = Join-Path $projectRoot 'out\installer-generated'
$generatedPayload = Join-Path $generatedDirectory 'Payload.wxs'

if (-not $Version) {
    [xml]$properties = Get-Content -LiteralPath $versionFile -Raw
    $Version = [string]$properties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Installer releases require a stable SemVer version (for example 0.3.0), got '$Version'."
}
if (-not $PublishDirectory) {
    $PublishDirectory = Join-Path $projectRoot 'out\portable'
}
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$mainExecutable = Join-Path $PublishDirectory 'VeliShell.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "The self-contained VeliShell payload is missing: $mainExecutable"
}
$serviceRelativePath = 'VeliShell.UpdateService.exe'
$serviceExecutable = Join-Path $PublishDirectory $serviceRelativePath
if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    throw "The optional update-service payload is missing: $serviceExecutable. Run tools\Build.ps1 -Portable first."
}

function New-DeterministicGuid([string]$Seed) {
    $seedBytes = [Text.Encoding]::UTF8.GetBytes($Seed)
    $hash = [Security.Cryptography.SHA256]::HashData($seedBytes)
    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($hash, $guidBytes, 16)
    return ([Guid]::new($guidBytes)).ToString('D').ToUpperInvariant()
}

function Escape-Xml([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

New-Item -ItemType Directory -Path $generatedDirectory -Force | Out-Null
$files = [IO.Directory]::EnumerateFiles($PublishDirectory, '*', [IO.SearchOption]::AllDirectories) |
    Where-Object {
        $extension = [IO.Path]::GetExtension($_)
        $name = [IO.Path]::GetFileName($_)
        $extension -notin @('.pdb', '.wixpdb') -and $name -ne 'SHA256SUMS.txt'
    } |
    ForEach-Object { [IO.Path]::GetRelativePath($PublishDirectory, $_).Replace('/', '\') }
$files = [string[]]@($files)
[Array]::Sort($files, [StringComparer]::Ordinal)
if ($files.Count -eq 0) { throw 'The publish directory does not contain installer payload files.' }
$applicationFiles = [string[]]@($files | Where-Object { -not $_.StartsWith('VeliShell.UpdateService.', [StringComparison]::OrdinalIgnoreCase) })
$serviceFiles = [string[]]@($files | Where-Object { $_.StartsWith('VeliShell.UpdateService.', [StringComparison]::OrdinalIgnoreCase) })
if ($applicationFiles.Count -eq 0) { throw 'The publish directory does not contain an application payload.' }
if (-not ($serviceFiles -contains $serviceRelativePath)) {
    throw "The optional update-service executable is not in the generated payload: $serviceRelativePath"
}

$xml = [Text.StringBuilder]::new()
[void]$xml.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$xml.AppendLine('  <Fragment>')
$directories = [string[]]@($files | ForEach-Object { [IO.Path]::GetDirectoryName($_) } | Where-Object { -not [string]::IsNullOrEmpty($_) } | Select-Object -Unique)
[Array]::Sort($directories, [StringComparer]::Ordinal)
if ($directories.Count -gt 0) {
    [void]$xml.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
    foreach ($directory in $directories) {
        $directoryIdentity = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($directory.ToLowerInvariant())))).Substring(0, 24)
        [void]$xml.AppendLine(('      <Directory Id="dir_{0}" Name="{1}" />' -f $directoryIdentity, (Escape-Xml $directory)))
    }
    [void]$xml.AppendLine('    </DirectoryRef>')
}
$firstInDirectory = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-PayloadGroup([string]$groupId, [string[]]$groupFiles) {
    [void]$xml.AppendLine(('    <ComponentGroup Id="{0}">' -f $groupId))
    foreach ($relative in $groupFiles) {
        $identity = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant())))).Substring(0, 24)
        $directory = [IO.Path]::GetDirectoryName($relative)
        $directoryIdentity = if ([string]::IsNullOrEmpty($directory)) { '' } else { ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($directory.ToLowerInvariant())))).Substring(0, 24) }
        $directoryId = if ([string]::IsNullOrEmpty($directory)) { 'INSTALLFOLDER' } else { 'dir_' + $directoryIdentity }
        $source = '!(bindpath.publish)' + $relative
        $componentGuid = New-DeterministicGuid ('VeliShell/component/' + $relative.ToLowerInvariant())
        $isExecutable = [IO.Path]::GetExtension($relative).Equals('.exe', [StringComparison]::OrdinalIgnoreCase)
        $checksum = if ($isExecutable) { ' Checksum="yes"' } else { '' }
        [void]$xml.AppendLine(('      <Component Id="cmp_{0}" Guid="{1}" Directory="{2}">' -f $identity, $componentGuid, $directoryId))
        [void]$xml.AppendLine(('        <File Id="fil_{0}" Source="{1}" KeyPath="yes"{2} />' -f $identity, (Escape-Xml $source), $checksum))
        if ($relative.Equals($serviceRelativePath, [StringComparison]::OrdinalIgnoreCase)) {
            [void]$xml.AppendLine('        <ServiceInstall')
            [void]$xml.AppendLine('            Id="VeliShellUpdateServiceInstall"')
            [void]$xml.AppendLine('            Name="VeliShell.UpdateService"')
            [void]$xml.AppendLine('            DisplayName="VeliShell Hintergrund-Updateprüfung"')
            [void]$xml.AppendLine('            Description="Prüft alle 12 Stunden ausschließlich die Metadaten offizieller VeliShell-Releases. Lädt keine Updates herunter und installiert nichts."')
            [void]$xml.AppendLine('            Type="ownProcess"')
            [void]$xml.AppendLine('            Start="auto"')
            [void]$xml.AppendLine('            ErrorControl="normal"')
            [void]$xml.AppendLine('            Account="[WIX_ACCOUNT_LOCALSERVICE]"')
            [void]$xml.AppendLine('            Interactive="no" />')
            [void]$xml.AppendLine('        <ServiceControl')
            [void]$xml.AppendLine('            Id="VeliShellUpdateServiceControl"')
            [void]$xml.AppendLine('            Name="VeliShell.UpdateService"')
            [void]$xml.AppendLine('            Start="install"')
            [void]$xml.AppendLine('            Stop="both"')
            [void]$xml.AppendLine('            Remove="uninstall"')
            [void]$xml.AppendLine('            Wait="yes" />')
        }
        if ($firstInDirectory.Add($directoryId)) {
            [void]$xml.AppendLine(('        <RemoveFolder Id="rm_{0}" Directory="{1}" On="uninstall" />' -f $identity, $directoryId))
        }
        [void]$xml.AppendLine('      </Component>')
    }
    [void]$xml.AppendLine('    </ComponentGroup>')
}
Add-PayloadGroup 'VeliShellApplicationPayload' $applicationFiles
Add-PayloadGroup 'VeliShellUpdateServicePayload' $serviceFiles
[void]$xml.AppendLine('  </Fragment>')
[void]$xml.AppendLine('</Wix>')
[IO.File]::WriteAllText($generatedPayload, $xml.ToString(), [Text.UTF8Encoding]::new($false))

$productCode = New-DeterministicGuid ("VeliShell/product/$Version/win-x64")

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$arguments = @(
    'build', $installerProject,
    '--configuration', 'Release',
    "-p:ProductVersion=$Version",
    "-p:ProductCode=$productCode",
    "-p:PublishDirectory=$PublishDirectory",
    "-p:GeneratedPayloadFile=$generatedPayload",
    '--nologo'
)
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "WiX installer build failed with exit code $LASTEXITCODE." }

$installer = Join-Path $projectRoot "out\installer\VeliShell-$Version-win-x64.msi"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "The installer was not created at the expected path: $installer"
}
Write-Host "Installer created: $installer" -ForegroundColor Green
