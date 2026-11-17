# Builds the release files for one platform:
#   viddl-<version>-<rid>.zip   portable, self-contained app folder
#   viddl-<version>-<rid>.msi   per-user installer
# The app's own files and the installer are code-signed when a certificate is configured (see build/sign.ps1).
#
# Usage (from the repository root):
#   powershell -ExecutionPolicy Bypass -File build/package.ps1 -Platform x64 -Version 2.0.0
param(
    [Parameter(Mandatory)] [ValidateSet('x64', 'x86', 'ARM64')] [string]$Platform,
    [Parameter(Mandatory)] [string]$Version,
    [string]$OutputDir = '.'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rid = @{ x64 = 'win-x64'; x86 = 'win-x86'; ARM64 = 'win-arm64' }[$Platform]
$publish = Join-Path $root "publish/$rid"
$OutputDir = (New-Item -ItemType Directory -Force $OutputDir).FullName
$baseName = "viddl-$Version-$rid"

function Run([string]$what, [scriptblock]$command) {
    Write-Host "== $what"
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

# 1. Self-contained app: the .NET runtime and the Windows App SDK are included.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Run "Publish $rid" {
    dotnet publish (Join-Path $root 'src/viddl.App/viddl.App.csproj') -c Release -p:Platform=$Platform -r $rid `
        --self-contained true -p:Version=$Version -o $publish
}

# 2. Sign viddl's own files (third-party files are already signed by their publishers).
$own = @('viddl.exe', 'viddl.dll', 'viddl.Core.dll', 'viddl.Downloads.dll') | ForEach-Object { Join-Path $publish $_ }
& (Join-Path $PSScriptRoot 'sign.ps1') -Files $own

# 3. Portable zip.
$zip = Join-Path $OutputDir "$baseName.zip"
Compress-Archive -Path "$publish/*" -DestinationPath $zip -Force

# 4. Installer.
$wixPlatform = @{ x64 = 'x64'; x86 = 'x86'; ARM64 = 'arm64' }[$Platform]
$msiOut = Join-Path $root "installer/bin/$wixPlatform"
Run "Build installer for $rid" {
    dotnet build (Join-Path $root 'installer/viddl.Installer.wixproj') -c Release -p:Platform=$wixPlatform `
        "-p:PublishDir=$publish\" -p:Version=$Version -o $msiOut
}
$msi = Join-Path $OutputDir "$baseName.msi"
Copy-Item (Join-Path $msiOut 'viddl.msi') $msi -Force
& (Join-Path $PSScriptRoot 'sign.ps1') -Files $msi -Description 'viddl installer'

Write-Host "Created $zip"
Write-Host "Created $msi"
