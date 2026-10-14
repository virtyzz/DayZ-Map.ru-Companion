[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')]
    [string]$Version,
    [string]$TesseractDirectory = "$env:ProgramFiles\Tesseract-OCR",
    [string]$MakeAppxPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'CrosshairMarker.csproj') -Raw
    $Version = ([string]$project.Project.PropertyGroup[0].Version) + '.0'
}
$outputDirectory = Join-Path $repoRoot 'artifacts\store'
$workDirectory = Join-Path $outputDirectory ('work\' + [Guid]::NewGuid().ToString('N'))
$layoutDirectory = Join-Path $workDirectory 'layout'
New-Item -ItemType Directory -Force -Path $layoutDirectory | Out-Null

if (-not $MakeAppxPath) {
    $sdkLocations = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $repoRoot 'artifacts\store-tools\sdk\bin')
    )
    $sdkTools = foreach ($sdkLocation in $sdkLocations) {
        if (Test-Path -LiteralPath $sdkLocation) {
            Get-ChildItem -LiteralPath $sdkLocation -Filter makeappx.exe -Recurse |
                Where-Object { $_.Directory.Name -eq 'x64' }
        }
    }
    $MakeAppxPath = $sdkTools | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $MakeAppxPath -or -not (Test-Path -LiteralPath $MakeAppxPath)) {
    throw 'Install Windows SDK (MSIX Packaging Tools), or pass -MakeAppxPath with the path to makeappx.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $TesseractDirectory 'tesseract.exe'))) {
    throw 'Pass -TesseractDirectory pointing to the extracted/installed x64 Tesseract OCR runtime.'
}
foreach ($language in @('eng', 'rus')) {
    $model = Join-Path $repoRoot "installer\third_party\$language.traineddata"
    $expectedHash = if ($language -eq 'eng') { '7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2' } else { 'E16E5E036CCE1D9EC2B00063CF8B54472625B9E14D893A169E2B0DEDEB4DF225' }
    if ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -ne $expectedHash) { throw "Invalid OCR language asset: $language" }
}

$appVersion = ([version]$Version).ToString(3)
Push-Location $repoRoot
try {
    & dotnet publish .\CrosshairMarker.csproj --configuration Release --runtime win-x64 --self-contained true `
        -p:StoreBuild=true -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false `
        "-p:Version=$appVersion" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" `
        -p:NuGetAudit=false --ignore-failed-sources --output $layoutDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Store publish failed.' }
} finally { Pop-Location }

$ocrDirectory = Join-Path $layoutDirectory 'ocr'
$tessdataDirectory = Join-Path $ocrDirectory 'tessdata'
New-Item -ItemType Directory -Force -Path $tessdataDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $TesseractDirectory 'tesseract.exe') -Destination $ocrDirectory
Get-ChildItem -LiteralPath $TesseractDirectory -Filter '*.dll' | Copy-Item -Destination $ocrDirectory
Copy-Item -LiteralPath (Join-Path $TesseractDirectory 'doc') -Destination (Join-Path $ocrDirectory 'doc') -Recurse
foreach ($language in @('eng', 'rus')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "installer\third_party\$language.traineddata") -Destination $tessdataDirectory
}
$ocrLanguages = & (Join-Path $ocrDirectory 'tesseract.exe') --list-langs 2>&1
if ($LASTEXITCODE -ne 0 -or $ocrLanguages -notcontains 'eng' -or $ocrLanguages -notcontains 'rus') {
    throw "Bundled OCR validation failed: $ocrLanguages"
}
$ocrLanguages | Out-Host
Copy-Item -LiteralPath (Join-Path $repoRoot 'PRIVACY.ru.md') -Destination $layoutDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $layoutDirectory

[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Save((Join-Path $layoutDirectory 'AppxManifest.xml'))

Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $layoutDirectory 'StoreAssets'
New-Item -ItemType Directory -Force -Path $assetDirectory | Out-Null
$sourceImage = [Drawing.Image]::FromFile((Join-Path $repoRoot 'assets\dayz-map-companion-icon.png'))
try {
    foreach ($asset in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
        $size = [int]$asset[1]
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $scale = [Math]::Min($size / $sourceImage.Width, $size / $sourceImage.Height)
            $width = [int][Math]::Round($sourceImage.Width * $scale)
            $height = [int][Math]::Round($sourceImage.Height * $scale)
            $graphics.DrawImage($sourceImage, [int](($size - $width) / 2), [int](($size - $height) / 2), $width, $height)
            $bitmap.Save((Join-Path $assetDirectory $asset[0]), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $sourceImage.Dispose() }

$packagePath = Join-Path $outputDirectory "DayZ-Map-Companion_${Version}_x64.msix"
$packLog = Join-Path $workDirectory 'makeappx.log'
& $MakeAppxPath pack /d $layoutDirectory /p $packagePath /o *> $packLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $packLog -Tail 30; throw 'MSIX packaging/validation failed.' }
Get-FileHash -LiteralPath $packagePath -Algorithm SHA256 | Format-List
Write-Host "Unsigned Store upload package: $packagePath"
Write-Host 'Local installation requires development signing. Do not change the Store Publisher identity.'
