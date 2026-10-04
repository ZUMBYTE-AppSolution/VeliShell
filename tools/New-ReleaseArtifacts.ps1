[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PortableDirectory,
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'out'

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

[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$runtimeNoticeVersion = [string]$properties.Project.PropertyGroup.VeliShellRuntimeNoticeVersion
if ($runtimeNoticeVersion -notmatch '^10\.0\.\d+$') {
    throw "Invalid or missing .NET notice version in Directory.Build.props: '$runtimeNoticeVersion'"
}
if (-not $PortableDirectory) { $PortableDirectory = Join-Path $out 'portable' }
if (-not $InstallerPath) { $InstallerPath = Join-Path $out "installer\VeliShell-$Version-win-x64.msi" }
$PortableDirectory = [IO.Path]::GetFullPath($PortableDirectory)
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Release version must be stable SemVer, got '$Version'."
}
if (-not (Test-Path -LiteralPath (Join-Path $PortableDirectory 'VeliShell.exe') -PathType Leaf)) {
    throw 'The portable VeliShell payload is missing.'
}
if (Test-Path -LiteralPath (Join-Path $PortableDirectory 'UpdateService')) {
    throw 'The portable payload still contains the removed Windows service. Run tools\Build.ps1 -Portable before creating release artifacts.'
}
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "Installer not found: $InstallerPath" }
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
    $publishedLegalFile = Join-Path $PortableDirectory $relativeLegalFile
    if (-not (Test-Path -LiteralPath $publishedLegalFile -PathType Leaf)) {
        throw "A required license or notice file is missing from the portable release payload: $publishedLegalFile. Run tools\Build.ps1 -Portable first."
    }
}
$runtimeVersionPattern = '^' + [Regex]::Escape($runtimeNoticeVersion) + '(?:[-+]|$)'
foreach ($runtimeBinary in @('System.Private.CoreLib.dll', 'PresentationFramework.dll')) {
    $runtimeBinaryPath = Join-Path $PortableDirectory $runtimeBinary
    if (-not (Test-Path -LiteralPath $runtimeBinaryPath -PathType Leaf)) {
        throw "The self-contained runtime is incomplete: $runtimeBinaryPath"
    }
    $publishedRuntimeVersion = (Get-Item -LiteralPath $runtimeBinaryPath).VersionInfo.ProductVersion
    if ($publishedRuntimeVersion -notmatch $runtimeVersionPattern) {
        throw "The bundled runtime '$runtimeBinary' ($publishedRuntimeVersion) does not match the license notices for $runtimeNoticeVersion. Update THIRD-PARTY-LICENSES and VeliShellRuntimeNoticeVersion together."
    }
}

$releaseDirectory = Join-Path $out 'release'
$stagingDirectory = Join-Path $out ('release-staging-' + [Guid]::NewGuid().ToString('N'))
$outFull = [IO.Path]::GetFullPath($out).TrimEnd([IO.Path]::DirectorySeparatorChar)
foreach ($candidate in @($releaseDirectory, $stagingDirectory)) {
    $candidateFull = [IO.Path]::GetFullPath($candidate).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($candidateFull) -ne $outFull) { throw "Unsafe release path: $candidateFull" }
}
if (Test-Path -LiteralPath $releaseDirectory) { Remove-Item -LiteralPath $releaseDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDirectory, $stagingDirectory -Force | Out-Null

try {
    $portableFiles = [string[]]@(
        [IO.Directory]::EnumerateFiles($PortableDirectory, '*', [IO.SearchOption]::AllDirectories) |
            Where-Object {
                $extension = [IO.Path]::GetExtension($_)
                $name = [IO.Path]::GetFileName($_)
                $extension -notin @('.pdb', '.wixpdb') -and $name -ne 'SHA256SUMS.txt'
            }
    )
    [Array]::Sort($portableFiles, [StringComparer]::Ordinal)
    foreach ($file in $portableFiles) {
        $relative = Get-VeliShellRelativePath $PortableDirectory $file
        $destination = Join-Path $stagingDirectory $relative
        $destinationParent = Split-Path -Parent $destination
        if ($destinationParent) { New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null }
        Copy-Item -LiteralPath $file -Destination $destination -Force
    }
    foreach ($document in @('LICENSE', 'README.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'SECURITY.md', 'SUPPORT.md')) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination (Join-Path $stagingDirectory $document) -Force
    }

    Add-Type -AssemblyName System.IO.Compression
    $zipPath = Join-Path $releaseDirectory "VeliShell-$Version-win-x64-portable.zip"
    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $files = [string[]]@([IO.Directory]::EnumerateFiles($stagingDirectory, '*', [IO.SearchOption]::AllDirectories))
            [Array]::Sort($files, [StringComparer]::Ordinal)
            foreach ($file in $files) {
                $relative = (Get-VeliShellRelativePath $stagingDirectory $file).Replace('\', '/')
                $entry = $archive.CreateEntry("VeliShell-$Version/$relative", [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                try {
                    $source = [IO.File]::OpenRead($file)
                    try { $source.CopyTo($entryStream) } finally { $source.Dispose() }
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $zipStream.Dispose() }

    $releaseInstaller = Join-Path $releaseDirectory ([IO.Path]::GetFileName($InstallerPath))
    Copy-Item -LiteralPath $InstallerPath -Destination $releaseInstaller -Force
    # Stable aliases make the README's /releases/latest/download links durable,
    # while versioned names remain available to the in-app updater and archives.
    $stableZip = Join-Path $releaseDirectory 'VeliShell-Portable-win-x64.zip'
    $stableInstaller = Join-Path $releaseDirectory 'VeliShell-Setup-win-x64.msi'
    Copy-Item -LiteralPath $zipPath -Destination $stableZip -Force
    Copy-Item -LiteralPath $releaseInstaller -Destination $stableInstaller -Force
    $artifacts = @($zipPath, $releaseInstaller, $stableZip, $stableInstaller)
    $checksumLines = foreach ($artifact in $artifacts) {
        $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($artifact))"
    }
    [IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksumLines, [Text.UTF8Encoding]::new($false))

    $signature = Get-AuthenticodeSignature -LiteralPath $releaseInstaller
    $signatureDescription = if ($signature.Status -eq 'Valid') {
        "Windows publisher signature: valid ($($signature.SignerCertificate.Subject))"
    } else {
        'Windows publisher signature: not present. Windows SmartScreen may warn before installation.'
    }
    $releaseInfo = @(
        "VeliShell $Version",
        'Repository: https://github.com/ZUMBYTE-AppSolution/VeliShell',
        $signatureDescription,
        'Verify package hashes with SHA256SUMS.txt before manual installation.'
    )
    [IO.File]::WriteAllLines((Join-Path $releaseDirectory 'RELEASE-INFO.txt'), $releaseInfo, [Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) { Remove-Item -LiteralPath $stagingDirectory -Recurse -Force }
}

Write-Host "Release artifacts created in $releaseDirectory" -ForegroundColor Green
