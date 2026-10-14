param(
    [Parameter(Mandatory = $true)][string]$ProductId,
    [Parameter(Mandatory = $true)][string]$MsixPath,
    [Parameter(Mandatory = $true)][string]$ScreenshotPath,
    [Parameter(Mandatory = $true)][string]$GermanReleaseNotesPath,
    [Parameter(Mandatory = $true)][string]$EnglishReleaseNotesPath,
    [Parameter(Mandatory = $true)][string]$TenantId,
    [Parameter(Mandatory = $true)][string]$ClientId,
    [Parameter(Mandatory = $true)][string]$ClientSecret
)

$ErrorActionPreference = 'Stop'

foreach ($path in @($MsixPath, $ScreenshotPath, $GermanReleaseNotesPath, $EnglishReleaseNotesPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Store submission file is missing: $path"
    }
}

$releaseNotes = @{
    'de-de' = (Get-Content -LiteralPath $GermanReleaseNotesPath -Raw -Encoding utf8).Trim()
    'en-us' = (Get-Content -LiteralPath $EnglishReleaseNotesPath -Raw -Encoding utf8).Trim()
}
foreach ($entry in $releaseNotes.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($entry.Value) -or $entry.Value.Length -gt 1500) {
        throw "The $($entry.Key) Store release notes must contain 1 to 1500 characters."
    }
}

$screenshot = Get-Item -LiteralPath $ScreenshotPath
if ($screenshot.Extension -ne '.png' -or $screenshot.Length -gt 50MB) {
    throw 'The Store screenshot must be a PNG smaller than 50 MB.'
}

$rawSubmission = & msstore submission get $ProductId
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($rawSubmission | Out-String))) {
    throw 'The Store draft could not be read after uploading the MSIX.'
}
$submission = ($rawSubmission | Out-String) | ConvertFrom-Json -AsHashtable
if ($submission['Status'] -ne 'PendingCommit') {
    throw "Expected a pending Store draft, got '$($submission['Status'])'."
}
if ([string]::IsNullOrWhiteSpace([string]$submission['FileUploadUrl'])) {
    throw 'The Store draft did not supply a file upload URL.'
}

$msixName = [IO.Path]::GetFileName($MsixPath)
$matchingPackage = @($submission['ApplicationPackages'] | Where-Object {
    [IO.Path]::GetFileName([string]$_['FileName']) -eq $msixName -and $_['FileStatus'] -ne 'PendingDelete'
})
if ($matchingPackage.Count -ne 1) {
    throw "Expected exactly one draft package named $msixName."
}
$packageEntryName = [string]$matchingPackage[0]['FileName']
if ($packageEntryName -match '(^[\\/]|(^|[\\/])\.\.([\\/]|$))') {
    throw 'The Store draft package path is unsafe.'
}

$listings = $submission['Listings']
if (-not $listings -or $listings.Count -eq 0) {
    throw 'The Store draft has no localized listings for the screenshot.'
}
if ($submission['Pricing']['PriceId'] -notmatch '^(Free|NotAvailable|Tier[0-9]+)$') {
    throw 'The Store draft has no valid base price ID; leaving it uncommitted for review.'
}

$newImages = @()
foreach ($locale in $listings.Keys) {
    $baseListing = $listings[$locale]['BaseListing']
    if (-not $baseListing) {
        throw "The Store listing for $locale has no base listing."
    }
    $localeKey = $locale.ToLowerInvariant()
    if (-not $releaseNotes.ContainsKey($localeKey)) {
        throw "There are no release notes for Store listing locale $locale."
    }
    $baseListing['ReleaseNotes'] = $releaseNotes[$localeKey]

    $existingImages = @($baseListing['Images'] | Where-Object { $null -ne $_ })
    $imageName = "velishell-windows-desktop-$localeKey.png"
    $matchingImage = @($existingImages | Where-Object {
        $_['FileName'] -eq $imageName -and $_['FileStatus'] -ne 'PendingDelete'
    })
    if ($matchingImage.Count -gt 1) {
        throw "The Store screenshot occurs more than once in the $locale listing."
    }
    if ($matchingImage.Count -eq 1) {
        if ($matchingImage[0]['FileStatus'] -eq 'PendingUpload') {
            $newImages += $imageName
        }
        continue
    }
    $desktopScreenshots = @($existingImages | Where-Object {
        $_['ImageType'] -eq 'Screenshot' -and $_['FileStatus'] -ne 'PendingDelete'
    })
    if ($desktopScreenshots.Count -ge 10) {
        throw "The Store listing for $locale already has ten desktop screenshots."
    }

    $caption = if ($locale -match '^de([_-]|$)') {
        'VeliShell-Menüleiste und Dock auf einem Windows-Desktop neben den Windows-Einstellungen.'
    } else {
        'VeliShell menu bar and dock on a Windows desktop beside Windows Settings.'
    }
    $image = [ordered]@{
        FileName = $imageName
        FileStatus = 'PendingUpload'
        Description = $caption
        ImageType = 'Screenshot'
    }
    $baseListing['Images'] = @($image) + $existingImages
    $newImages += $imageName
}

$certificationNote = 'The first desktop screenshot shows VeliShell running on Windows next to the native Windows Settings app. VeliShell customizes the Windows desktop; it is not a macOS application.'
$previousNote = [string]$submission['NotesForCertification']
if (-not $previousNote.Contains($certificationNote, [StringComparison]::Ordinal)) {
    $submission['NotesForCertification'] = ($previousNote.Trim() + "`n`n" + $certificationNote).Trim()
}

$editableFields = @(
    'ApplicationCategory', 'Pricing', 'Visibility', 'TargetPublishMode', 'TargetPublishDate',
    'Listings', 'HardwarePreferences', 'AutomaticBackupEnabled', 'CanInstallOnRemovableMedia',
    'IsGameDvrEnabled', 'GamingOptions', 'HasExternalInAppProducts', 'MeetAccessibilityGuidelines',
    'NotesForCertification', 'ApplicationPackages', 'PackageDeliveryOptions',
    'EnterpriseLicensing', 'AllowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies',
    'AllowTargetFutureDeviceFamilies', 'Trailers'
)
$payload = [ordered]@{}
foreach ($field in $editableFields) {
    if ($submission.Contains($field)) {
        $payload[$field] = $submission[$field]
    }
}

$tokenBody = @{
    grant_type = 'client_credentials'
    client_id = $ClientId
    client_secret = $ClientSecret
    resource = 'https://manage.devcenter.microsoft.com'
}
try {
    $tokenResponse = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/token" -Body $tokenBody
} catch {
    throw 'Could not authenticate the Store listing update with Microsoft Entra ID.'
}
$headers = @{ Authorization = "Bearer $($tokenResponse.access_token)" }
$submissionId = [string]$submission['Id']
$apiUri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$ProductId/submissions/$submissionId"
$payloadJson = $payload | ConvertTo-Json -Depth 100 -Compress
try {
    Invoke-RestMethod -Method Put -Uri $apiUri -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $payloadJson | Out-Null
} catch {
    throw 'Partner Center rejected the updated screenshot metadata; the draft was not submitted.'
}

$zipPath = Join-Path $env:RUNNER_TEMP 'velishell-store-package-and-screenshot.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $MsixPath, $packageEntryName, [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    foreach ($imageName in $newImages) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $ScreenshotPath, $imageName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $archive.Dispose()
}

try {
    Invoke-WebRequest -Method Put -Uri ([string]$submission['FileUploadUrl']) -InFile $zipPath `
        -ContentType 'application/zip' -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } | Out-Null
} catch {
    throw 'The combined MSIX and screenshot archive could not be uploaded; the draft was not submitted.'
}

Write-Host "Updated localized release notes in $($listings.Count) Store listing(s); uploaded $($newImages.Count) Windows screenshot(s)."
