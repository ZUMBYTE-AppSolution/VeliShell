[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true, ParameterSetName = 'Store')][string]$IdentityName,
    [Parameter(Mandatory = $true, ParameterSetName = 'Store')][string]$Publisher,
    [Parameter(ParameterSetName = 'Store')][string]$PublisherDisplayName = 'Zumbyte AppSolution',
    [Parameter(Mandatory = $true, ParameterSetName = 'Development')][switch]$DevelopmentIdentity,
    [string]$PublishDirectory,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'out'
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "MSIX version must be stable SemVer, got '$Version'."
}
if ($DevelopmentIdentity) {
    $IdentityName = 'Zumbyte.VeliShell.Development'
    # Windows 11 permits this special OID form for an unsigned local test package.
    # It is never used for a Partner Center submission.
    $Publisher = 'CN=Zumbyte AppSolution, OID.2.25.311729368913984317654407730594956997722=1'
    $PublisherDisplayName = 'Zumbyte AppSolution'
}
foreach ($requiredValue in @($IdentityName, $Publisher, $PublisherDisplayName)) {
    if ([string]::IsNullOrWhiteSpace($requiredValue)) { throw 'MSIX identity values cannot be empty.' }
}

if (-not $PublishDirectory) { $PublishDirectory = Join-Path $out 'portable' }
if (-not $OutputPath) { $OutputPath = Join-Path $out "store\VeliShell-$Version-win-x64.msix" }
$publishFull = [IO.Path]::GetFullPath($PublishDirectory)
$outputFull = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath (Join-Path $publishFull 'VeliShell.exe') -PathType Leaf)) {
    throw "The published VeliShell application was not found in $publishFull."
}

$outFull = [IO.Path]::GetFullPath($out).TrimEnd([IO.Path]::DirectorySeparatorChar)
$staging = Join-Path $out ('.msix-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$inspection = Join-Path $out ('.msix-check-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
foreach ($temporaryPath in @($staging, $inspection)) {
    $temporaryFull = [IO.Path]::GetFullPath($temporaryPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($temporaryFull) -ne $outFull) { throw "Unsafe MSIX temporary path: $temporaryFull" }
}

function Find-WindowsSdkTool([string]$Name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem -LiteralPath $kits -Filter $Name -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\$([Regex]::Escape($Name))$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $tool) { throw "$Name from the Windows SDK was not found." }
    return $tool
}

function Escape-Xml([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function Write-SquarePng([string]$Source, [string]$Destination, [int]$Side) {
    $sourceStream = [IO.File]::OpenRead($Source)
    try {
        $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
            $sourceStream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $decoder.Frames[0]
    }
    finally { $sourceStream.Dispose() }

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try { $drawing.DrawImage($frame, [System.Windows.Rect]::new(0, 0, $Side, $Side)) }
    finally { $drawing.Close() }
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Side, $Side, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $destinationStream = [IO.File]::Create($Destination)
    try { $encoder.Save($destinationStream) }
    finally { $destinationStream.Dispose() }
}

New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    foreach ($file in [IO.Directory]::EnumerateFiles($publishFull, '*', [IO.SearchOption]::AllDirectories)) {
        if ([IO.Path]::GetExtension($file) -in @('.pdb', '.wixpdb')) { continue }
        $relative = [IO.Path]::GetRelativePath($publishFull, $file)
        $destination = Join-Path $staging $relative
        $parent = Split-Path -Parent $destination
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $file -Destination $destination -Force
    }

    $assets = Join-Path $staging 'Assets'
    New-Item -ItemType Directory -Path $assets -Force | Out-Null
    Add-Type -AssemblyName PresentationCore
    $iconSource = Join-Path $root 'src\VeliShell.Desktop\Assets\VeliShellApp.png'
    Write-SquarePng $iconSource (Join-Path $assets 'StoreLogo.png') 50
    Write-SquarePng $iconSource (Join-Path $assets 'Square44x44Logo.png') 44
    Write-SquarePng $iconSource (Join-Path $assets 'Square150x150Logo.png') 150

    $templatePath = Join-Path $root 'packaging\msix\AppxManifest.xml.template'
    $manifest = (Get-Content -LiteralPath $templatePath -Raw).
        Replace('__IDENTITY_NAME__', (Escape-Xml $IdentityName)).
        Replace('__PUBLISHER__', (Escape-Xml $Publisher)).
        Replace('__PUBLISHER_DISPLAY_NAME__', (Escape-Xml $PublisherDisplayName)).
        Replace('__VERSION__', "$Version.0")
    [xml]$manifestDocument = $manifest
    [IO.File]::WriteAllText(
        (Join-Path $staging 'AppxManifest.xml'),
        $manifestDocument.OuterXml,
        [Text.UTF8Encoding]::new($false))

    $outputParent = Split-Path -Parent $outputFull
    if ($outputParent) { New-Item -ItemType Directory -Path $outputParent -Force | Out-Null }
    if (Test-Path -LiteralPath $outputFull) { Remove-Item -LiteralPath $outputFull -Force }
    $makeAppx = Find-WindowsSdkTool 'makeappx.exe'
    $packOutput = & $makeAppx pack /o /h SHA256 /d $staging /p $outputFull 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed to create the Store package.`n$packOutput" }

    New-Item -ItemType Directory -Path $inspection -Force | Out-Null
    $unpackOutput = & $makeAppx unpack /o /p $outputFull /d $inspection 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx could not verify the generated Store package.`n$unpackOutput" }
    foreach ($required in @(
        'AppxManifest.xml',
        'VeliShell.exe',
        'Assets\StoreLogo.png',
        'Assets\Square44x44Logo.png',
        'Assets\Square150x150Logo.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $inspection $required) -PathType Leaf)) {
            throw "Generated MSIX is missing $required."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    if (Test-Path -LiteralPath $inspection) { Remove-Item -LiteralPath $inspection -Recurse -Force }
}

Write-Host "Store MSIX created: $outputFull" -ForegroundColor Green
