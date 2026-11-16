# Publishes one addon so it can be installed through Dalamud's Custom Plugin Repositories:
#   1. builds a Release,
#   2. uploads the zip as a GitHub release named <Plugin>-v<version>,
#   3. rewrites <Plugin>\repo.json, the file that addon's custom repository link points at.
#
# Afterwards, commit and push that repo.json. Friends only see the new version once that push
# lands, by which time the zip it points to already exists.
#
# Before running: bump <Version> in the addon's .csproj, commit, and push.
#
#   powershell -ExecutionPolicy Bypass -File publish.ps1
#   powershell -ExecutionPolicy Bypass -File publish.ps1 -Plugin HuntingLogPlus

param(
    [string]$Plugin = "HuntingLogPlus"
)

$ErrorActionPreference = "Stop"

$repo = "jakeywunder/ffxiv-addons"
$root = $PSScriptRoot

$gh = "gh"
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    $gh = "C:\Program Files\GitHub CLI\gh.exe"
}

dotnet build "$root\$Plugin\$Plugin.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# DalamudPackager writes the finished manifest and latest.zip here.
$outDir = "$root\$Plugin\bin\Release\$Plugin"
$manifest = Get-Content "$outDir\$Plugin.json" -Raw | ConvertFrom-Json
$version = $manifest.AssemblyVersion
$tag = "$Plugin-v$version"

$zip = "$outDir\$Plugin.zip"
Copy-Item "$outDir\latest.zip" $zip -Force

$existingTags = & $gh release list --repo $repo --limit 200 --json tagName --jq ".[].tagName"
if ($LASTEXITCODE -ne 0) { throw "Could not list releases. Is the GitHub CLI signed in?" }

if ($existingTags -contains $tag) {
    Write-Host "Release $tag already exists; replacing its zip."
    & $gh release upload $tag $zip --repo $repo --clobber
}
else {
    & $gh release create $tag $zip --repo $repo --title "$($manifest.Name) $version" --notes "Install through Dalamud's Custom Plugin Repositories; see the README."
}
if ($LASTEXITCODE -ne 0) { throw "Uploading the release failed." }

# The repository entry is the built manifest plus where to download it.
$download = "https://github.com/$repo/releases/download/$tag/$Plugin.zip"
$entry = [ordered]@{}
foreach ($property in $manifest.PSObject.Properties) {
    $entry[$property.Name] = $property.Value
}
$entry["RepoUrl"] = "https://github.com/$repo"
$entry["IconUrl"] = "https://raw.githubusercontent.com/$repo/main/$Plugin/images/icon.png"
$entry["IsHide"] = $false
$entry["IsTestingExclusive"] = $false
$entry["DownloadCount"] = 0
$entry["LastUpdate"] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$entry["DownloadLinkInstall"] = $download
$entry["DownloadLinkUpdate"] = $download
$entry["DownloadLinkTesting"] = $download

# Each addon has its own link file in its own folder, so its link carries the addon's name:
#   https://raw.githubusercontent.com/jakeywunder/ffxiv-addons/main/<Plugin>/repo.json
# Dalamud expects a JSON array, and no byte-order mark.
$repoJson = "$root\$Plugin\repo.json"
$json = ConvertTo-Json -InputObject @([pscustomobject]$entry) -Depth 6
[System.IO.File]::WriteAllText($repoJson, $json + "`n", (New-Object System.Text.UTF8Encoding $false))

Write-Host ""
Write-Host "Published $tag."
Write-Host "Now commit and push $Plugin\repo.json so the new version shows up in Dalamud."
Write-Host "Link: https://raw.githubusercontent.com/$repo/main/$Plugin/repo.json"
