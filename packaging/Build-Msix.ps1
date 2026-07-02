<#
.SYNOPSIS
    Publish md-editor as a self-contained win-x64 build, pack it as MSIX,
    and sign it with the dev certificate.

.DESCRIPTION
    1. dotnet publish -c Release -r win-x64 --self-contained
    2. Copy publish output + Package.appxmanifest into a staging folder
    3. makeappx pack -d <staging> -p md-editor.msix
    4. signtool sign /f md-editor.pfx /p <pw> /fd SHA256 md-editor.msix

.PARAMETER PfxPath
    Path to the signing certificate (.pfx).

.PARAMETER PfxPassword
    SecureString password for the PFX.

.PARAMETER OutputDirectory
    Where to drop the final .msix.

.EXAMPLE
    $pw = Read-Host 'Cert pw' -AsSecureString
    .\Build-Msix.ps1 -PfxPath .\certs\md-editor.pfx -PfxPassword $pw
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PfxPath,
    [Parameter(Mandatory)] [SecureString]$PfxPassword,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'out')
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..')
$projectCsproj = Join-Path $repoRoot 'src\md-editor\md-editor.csproj'
$publishDir = Join-Path $repoRoot 'src\md-editor\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$stagingDir = Join-Path $OutputDirectory 'staging'
$manifest   = Join-Path $PSScriptRoot 'Package.appxmanifest'
$msixPath   = Join-Path $OutputDirectory 'md-editor.msix'

# Resolve the PFX to an absolute path up front. signtool's "Store IsDiskFile() failed"
# (0x80070003) is the symptom when this path is relative and doesn't resolve from the
# current shell location.
if (-not (Test-Path $PfxPath)) {
    throw "PFX not found at '$PfxPath'. Provide an absolute path or one that resolves from your current directory ($(Get-Location))."
}
$PfxPath = (Resolve-Path $PfxPath).Path

if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null }
if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

Write-Host "[1/4] dotnet publish ..." -ForegroundColor Cyan
dotnet publish $projectCsproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[2/4] Staging files ..." -ForegroundColor Cyan
if (-not (Test-Path $publishDir)) {
    throw "Publish output not found at $publishDir. Did the publish step succeed?"
}
Copy-Item -Path (Join-Path $publishDir '*') -Destination $stagingDir -Recurse
Copy-Item -Path $manifest -Destination (Join-Path $stagingDir 'AppxManifest.xml')

# Copy MSIX tile assets (referenced by Package.appxmanifest) into <staging>\Assets\
$tileSource = Join-Path $PSScriptRoot 'Assets'
$tileDest   = Join-Path $stagingDir   'Assets'
if (-not (Test-Path $tileSource)) {
    throw "Tile assets missing at $tileSource. Run packaging\New-Assets.ps1 first."
}
$requiredTiles = @(
    'StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png',
    'Wide310x150Logo.png', 'LargeTile.png', 'SmallTile.png', 'SplashScreen.png'
)
$missing = $requiredTiles | Where-Object { -not (Test-Path (Join-Path $tileSource $_)) }
if ($missing) {
    throw "Tile asset(s) missing in $tileSource :`n  $($missing -join "`n  ")`nRun packaging\New-Assets.ps1."
}
New-Item -ItemType Directory -Force -Path $tileDest | Out-Null
Copy-Item -Path (Join-Path $tileSource '*.png') -Destination $tileDest -Force

# Look for makeappx / signtool on PATH or in the Windows SDK
function Find-SdkTool {
    param([string]$Name)
    $found = Get-Command $Name -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sdkRoot) {
        $hit = Get-ChildItem -Path $sdkRoot -Recurse -Filter "$Name.exe" -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match 'x64' } |
               Sort-Object FullName -Descending |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "Could not find $Name.exe. Install the Windows 10/11 SDK."
}

$makeappx = Find-SdkTool 'makeappx'
$signtool = Find-SdkTool 'signtool'

Write-Host "[3/4] makeappx pack ..." -ForegroundColor Cyan
& $makeappx pack /d $stagingDir /p $msixPath /overwrite
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
if (-not (Test-Path $msixPath)) {
    throw "makeappx reported success but no MSIX was written at '$msixPath'."
}

Write-Host "[4/4] signtool sign ..." -ForegroundColor Cyan
Write-Host "  Cert : $PfxPath"
Write-Host "  Pkg  : $msixPath"
$plainPw = [System.Net.NetworkCredential]::new('', $PfxPassword).Password
& $signtool sign /fd SHA256 /f $PfxPath /p $plainPw $msixPath
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }

Write-Host ""
Write-Host "Done. Signed MSIX: $msixPath" -ForegroundColor Green
Write-Host "Make sure the publisher cert is trusted on the install machine:" -ForegroundColor Yellow
Write-Host "  Import-Certificate -FilePath certs\md-editor.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
