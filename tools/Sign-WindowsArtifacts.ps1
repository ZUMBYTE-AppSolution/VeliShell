[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [Parameter(Mandatory = $true)][string]$CertificateBase64,
    [Parameter(Mandatory = $true)][string]$CertificatePassword
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CertificateBase64) -or [string]::IsNullOrWhiteSpace($CertificatePassword)) {
    throw 'Both Windows signing certificate and password are required for signing.'
}
$resolved = @($Files | ForEach-Object {
    $path = [IO.Path]::GetFullPath($_)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Signing input not found: $path" }
    $path
})

$temporaryPfx = Join-Path ([IO.Path]::GetTempPath()) ('velishell-signing-' + [Guid]::NewGuid().ToString('N') + '.pfx')
$certificate = $null
try {
    [IO.File]::WriteAllBytes($temporaryPfx, [Convert]::FromBase64String($CertificateBase64))
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $temporaryPfx,
        $CertificatePassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    )
    if (-not $certificate.HasPrivateKey) { throw 'The signing certificate has no private key.' }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signTool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -File -Recurse |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $signTool) { throw 'signtool.exe from the Windows SDK was not found.' }

    foreach ($file in $resolved) {
        & $signTool sign /f $temporaryPfx /p $CertificatePassword /fd SHA256 /td SHA256 /tr 'https://timestamp.digicert.com' $file
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for $file." }
        & $signTool verify /pa /all $file
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $file." }
    }
}
finally {
    if ($certificate) { $certificate.Dispose() }
    Remove-Item -LiteralPath $temporaryPfx -Force -ErrorAction SilentlyContinue
}
