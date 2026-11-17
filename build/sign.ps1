# Signs files with an Authenticode code-signing certificate, if one is configured.
#
# Signing is what stops the "Windows protected your PC" (SmartScreen) and "unknown publisher" warnings when the
# installer or app is run: Windows shows "viddl" as the verified publisher instead.
#
# Two ways to provide the certificate (in CI, as repository secrets). The first one configured is used:
#
# 1. Azure Trusted Signing (recommended - Microsoft's signing service, no certificate file to look after)
#      AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET   an app registration with the
#                                                            "Trusted Signing Certificate Profile Signer" role
#      TRUSTED_SIGNING_ENDPOINT    e.g. https://weu.codesigning.azure.net
#      TRUSTED_SIGNING_ACCOUNT     the Trusted Signing account name
#      TRUSTED_SIGNING_PROFILE     the certificate profile name
#
# 2. A .pfx certificate from a certificate authority (DigiCert, Sectigo, ...)
#      SIGNING_CERT_BASE64    the .pfx file, base64-encoded:  [Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))
#      SIGNING_CERT_PASSWORD  its password
#
# Without either, the script does nothing (and says so), so unsigned builds still work.
param(
    [Parameter(Mandatory)] [string[]]$Files,
    [string]$Description = 'viddl',
    [string]$Url = 'https://github.com/edgeofsanity76/viddl'
)

$ErrorActionPreference = 'Stop'
$trusted = $env:TRUSTED_SIGNING_ACCOUNT -and $env:TRUSTED_SIGNING_PROFILE -and $env:TRUSTED_SIGNING_ENDPOINT
$pfxConfigured = [bool]$env:SIGNING_CERT_BASE64
if (-not $trusted -and -not $pfxConfigured) {
    $message = 'No code-signing certificate is configured, so these files are unsigned and Windows will warn when they are run. See build/sign.ps1.'
    if ($env:GITHUB_ACTIONS -or $env:GITEA_ACTIONS) { Write-Host "::warning title=Unsigned build::$message" } else { Write-Host $message }
    return
}

function Find-SignTool {
    # Trusted Signing's client library is 64-bit, so prefer the x64 signtool on 64-bit Windows.
    $arch = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
    $candidates = @()
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

# Downloads Microsoft's Trusted Signing client (the signtool plug-in) once per machine.
function Get-TrustedSigningDlib {
    $version = '1.0.95'
    $root = Join-Path ([IO.Path]::GetTempPath()) "viddl-trusted-signing-$version"
    $arch = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
    $dlib = Join-Path $root "bin\$arch\Azure.CodeSigning.Dlib.dll"
    if (-not (Test-Path $dlib)) {
        $nupkg = "$root.zip"
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.trusted.signing.client/$version/microsoft.trusted.signing.client.$version.nupkg" -OutFile $nupkg
        Expand-Archive $nupkg $root -Force
        Remove-Item $nupkg -Force
    }
    $dlib
}

$signtool = Find-SignTool
$temp = @()
try {
    if ($trusted) {
        $dlib = Get-TrustedSigningDlib
        $metadata = Join-Path ([IO.Path]::GetTempPath()) "viddl-signing-$([guid]::NewGuid().ToString('N')).json"
        $temp += $metadata
        @{ Endpoint = $env:TRUSTED_SIGNING_ENDPOINT; CodeSigningAccountName = $env:TRUSTED_SIGNING_ACCOUNT;
           CertificateProfileName = $env:TRUSTED_SIGNING_PROFILE } | ConvertTo-Json | Set-Content $metadata -Encoding utf8
        $certArgs = @('/dlib', $dlib, '/dmdf', $metadata)
        $timestamp = 'http://timestamp.acs.microsoft.com'
        Write-Host 'Signing with Azure Trusted Signing.'
    }
    else {
        $pfx = Join-Path ([IO.Path]::GetTempPath()) "viddl-signing-$([guid]::NewGuid().ToString('N')).pfx"
        $temp += $pfx
        [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGNING_CERT_BASE64))
        $certArgs = @('/f', $pfx, '/p', $env:SIGNING_CERT_PASSWORD)
        $timestamp = 'http://timestamp.digicert.com'
        Write-Host 'Signing with the configured .pfx certificate.'
    }

    foreach ($file in $Files) {
        # /d and /du are what Windows shows as the program name and "more information" link in its prompts.
        & $signtool sign /fd SHA256 /tr $timestamp /td SHA256 /d $Description /du $Url @certArgs $file
        if ($LASTEXITCODE -ne 0) { throw "Signing $file failed." }
        $sig = Get-AuthenticodeSignature $file
        if (-not $sig.SignerCertificate) { throw "$file has no signature after signing ($($sig.Status))." }
        if ($sig.Status -ne 'Valid') { Write-Host "::warning title=Signature not trusted::$file is signed, but Windows doesn't trust the certificate ($($sig.Status): $($sig.StatusMessage))." }
    }
    Write-Host "Signed $($Files.Count) file(s)."
}
finally {
    foreach ($t in $temp) { Remove-Item $t -Force -ErrorAction SilentlyContinue }
}
