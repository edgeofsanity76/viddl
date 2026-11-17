# Signs files with an Authenticode code-signing certificate, if one is configured.
#
# Signed apps and installers are what stop the "Windows protected your PC" (SmartScreen) warning: Windows trusts
# files signed with a certificate from a trusted certificate authority once the publisher has built up reputation.
#
# The certificate comes from two environment variables (in CI, repository secrets):
#   SIGNING_CERT_BASE64    the .pfx file, base64-encoded:  [Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))
#   SIGNING_CERT_PASSWORD  its password
# Without them the script does nothing, so unsigned builds still work.
param(
    [Parameter(Mandatory)] [string[]]$Files,
    [string]$Description = 'viddl',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
if (-not $env:SIGNING_CERT_BASE64) {
    Write-Host 'No signing certificate configured (SIGNING_CERT_BASE64) - files are left unsigned.'
    return
}

function Find-SignTool {
    $arch = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
    $candidates = @()
    # Windows SDK installations
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) { $candidates += Get-ChildItem $kits -Recurse -Filter signtool.exe | Where-Object { $_.Directory.Name -eq $arch } }
    # The Windows SDK build tools package the app already restores from NuGet
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { "$env:USERPROFILE\.nuget\packages" }
    $nuget = Join-Path $packages 'microsoft.windows.sdk.buildtools'
    if (Test-Path $nuget) { $candidates += Get-ChildItem $nuget -Recurse -Filter signtool.exe | Where-Object { $_.Directory.Name -eq $arch } }
    $tool = $candidates | Sort-Object { $_.FullName } -Descending | Select-Object -First 1
    if (-not $tool) { throw 'signtool.exe was not found. Install the Windows SDK or restore the app project first.' }
    $tool.FullName
}

$signtool = Find-SignTool
$pfx = Join-Path ([IO.Path]::GetTempPath()) "viddl-signing-$([guid]::NewGuid().ToString('N')).pfx"
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGNING_CERT_BASE64))
    foreach ($file in $Files) {
        & $signtool sign /fd SHA256 /f $pfx /p $env:SIGNING_CERT_PASSWORD /tr $TimestampUrl /td SHA256 /d $Description $file
        if ($LASTEXITCODE -ne 0) { throw "Signing $file failed." }
    }
    Write-Host "Signed $($Files.Count) file(s)."
}
finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
