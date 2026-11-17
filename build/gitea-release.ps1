# Creates (or completes) a Gitea release for a tag and attaches files to it.
#
# Used by .gitea/workflows/build.yml. Works with Windows PowerShell 5.1 and PowerShell 7.
#   -ServerUrl   Gitea base URL, e.g. https://nas.local:3000
#   -Repository  owner/name
#   -Token       token allowed to write to the repository
#   -Tag         e.g. v2.0.57 (created on -Commit if it doesn't exist yet)
#   -Commit      commit the tag points at when it has to be created
#   -Files       files to attach; files already attached under the same name are skipped
param(
    [Parameter(Mandatory)] [string]$ServerUrl,
    [Parameter(Mandatory)] [string]$Repository,
    [Parameter(Mandatory)] [string]$Token,
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Commit,
    [Parameter(Mandatory)] [string[]]$Files,
    [string]$Title = "viddl $($Tag.TrimStart('v'))"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$api = "$($ServerUrl.TrimEnd('/'))/api/v1/repos/$Repository"
$http = [System.Net.Http.HttpClient]::new()
$http.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('token', $Token)
$http.DefaultRequestHeaders.Accept.ParseAdd('application/json')

function Send([System.Net.Http.HttpRequestMessage]$request) {
    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $(if ($text) { $text | ConvertFrom-Json } else { $null }); Text = $text }
}

function Fail($what, $result) { throw "$what failed (HTTP $($result.Status)): $($result.Text)" }

# Release notes: the commits since the previous tag (or all commits for the first release).
function Get-ReleaseNotes {
    # git reports "no tags yet" on stderr; Windows PowerShell treats that as a terminating error under 'Stop'.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $previous = git describe --tags --abbrev=0 --match 'v*' "$Commit^" 2>$null
        if ($LASTEXITCODE -ne 0) { $previous = $null }
        $range = if ($previous) { "$previous..$Commit" } else { $Commit }
        $lines = git log $range --pretty=format:'- %s (%h)' 2>$null
    }
    finally {
        $ErrorActionPreference = $saved
    }
    $header = if ($previous) { "## Changes since $previous" } else { '## Changes' }
    if (-not $lines) { $lines = '- No changes recorded.' }
    ($header, '', ($lines -join "`n")) -join "`n"
}

# Find or create the release.
$existing = Send ([System.Net.Http.HttpRequestMessage]::new('GET', "$api/releases/tags/$Tag"))
if ($existing.Status -eq 200) {
    $release = $existing.Body
    Write-Host "Release $Tag already exists (id $($release.id))."
}
elseif ($existing.Status -eq 404) {
    $payload = @{
        tag_name         = $Tag
        target_commitish = $Commit
        name             = $Title
        body             = Get-ReleaseNotes
        draft            = $false
        prerelease       = $false
    } | ConvertTo-Json
    $request = [System.Net.Http.HttpRequestMessage]::new('POST', "$api/releases")
    $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
    $created = Send $request
    if ($created.Status -ne 201) { Fail 'Creating the release' $created }
    $release = $created.Body
    Write-Host "Created release $Tag (id $($release.id))."
}
else {
    Fail 'Looking up the release' $existing
}

# Attach files that aren't attached yet.
$attached = @($release.assets | ForEach-Object { $_.name })
foreach ($file in $Files) {
    $name = Split-Path $file -Leaf
    if ($attached -contains $name) { Write-Host "  $name is already attached."; continue }
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $stream = [System.IO.File]::OpenRead((Resolve-Path $file))
    try {
        $content = [System.Net.Http.StreamContent]::new($stream)
        $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new($(if ($name -like "*.msi") { "application/x-msi" } else { "application/zip" }))
        $form.Add($content, 'attachment', $name)
        $request = [System.Net.Http.HttpRequestMessage]::new('POST', "$api/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($name))")
        $request.Content = $form
        $uploaded = Send $request
        if ($uploaded.Status -ne 201) { Fail "Uploading $name" $uploaded }
        Write-Host "  Attached $name."
    }
    finally {
        $stream.Dispose()
    }
}

Write-Host "Release: $($ServerUrl.TrimEnd('/'))/$Repository/releases/tag/$Tag"
