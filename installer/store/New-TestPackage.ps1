# Run in PowerShell 7. Creates files only; does not add trust or install an app.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'CrosshairMarker.csproj') -Raw
    $Version = ([string]$project.Project.PropertyGroup[0].Version) + '.0'
}
$storeDirectory = Join-Path $repoRoot 'artifacts\store'
$sourcePackage = Join-Path $storeDirectory "DayZ-Map-Companion_${Version}_x64.msix"
$testDirectory = Join-Path $storeDirectory 'local-test'
if (-not (Test-Path -LiteralPath $sourcePackage)) { throw "Package not found: $sourcePackage" }
if (-not $SignToolPath) {
    $toolRoots = @((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'), (Join-Path $repoRoot 'artifacts\store-tools\sdk\bin'))
    $toolsFound = foreach ($toolRoot in $toolRoots) {
        if (Test-Path -LiteralPath $toolRoot) {
            Get-ChildItem -LiteralPath $toolRoot -Filter signtool.exe -Recurse | Where-Object { $_.Directory.Name -eq 'x64' }
        }
    }
    $SignToolPath = $toolsFound | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SignToolPath) { throw 'Windows SDK signtool.exe was not found.' }
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
$testPackage = Join-Path $testDirectory "DayZ-Map-Companion_${Version}_x64_TEST.msix"
$publicCertificatePath = Join-Path $testDirectory 'Companion-LocalTest.cer'
$temporaryKeyPath = Join-Path $testDirectory ([Guid]::NewGuid().ToString('N') + '.pfx')
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=19150DDA-3930-4E74-B112-0BB4B6A6461B', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $usages = [Security.Cryptography.OidCollection]::new()
    [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(30))
    try {
        [IO.File]::WriteAllBytes($publicCertificatePath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        [IO.File]::WriteAllBytes($temporaryKeyPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ''))
        Copy-Item -LiteralPath $sourcePackage -Destination $testPackage -Force
        & $SignToolPath sign /fd SHA256 /a /f $temporaryKeyPath $testPackage
        if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
        Write-Host "Test package: $testPackage"
        Write-Host "Public certificate: $publicCertificatePath"
        Write-Host "Certificate SHA-1 thumbprint: $($certificate.Thumbprint)"
        Write-Host "Expires: $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
        [IO.File]::WriteAllText((Join-Path $testDirectory 'certificate-thumbprint.txt'), $certificate.Thumbprint)
    } finally { if ($certificate) { $certificate.Dispose() } }
} finally {
    $rsa.Dispose()
    if (Test-Path -LiteralPath $temporaryKeyPath) { Remove-Item -LiteralPath $temporaryKeyPath -Force }
}
