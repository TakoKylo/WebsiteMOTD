# Fetch latest uBlock Origin Chromium build and unpack into
#   native/x64/extensions/ublock0/
# so the mod's MOTDWebView.TryLoadBundledExtensions picks it up.
#
# Run from the repo root or this folder:
#   pwsh native/fetch-ublock.ps1
#
# Idempotent: nukes the old extension folder before extracting so version
# upgrades don't leave stale files behind. Requires PowerShell 5+ and a working
# internet connection. The release ZIP is fetched directly from gorhill/uBlock's
# GitHub releases; no API key needed.

[CmdletBinding()]
param(
  [string] $Repo = "gorhill/uBlock",
  # Asset pattern in the GitHub release. Chromium build ships as
  # "uBlock0_<version>.chromium.zip" — that's the unpacked-extension flavor
  # WebView2 can load via AddBrowserExtensionAsync.
  [string] $AssetPattern = "uBlock0_*.chromium.zip"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$extDir    = Join-Path $scriptDir "x64\extensions\ublock0"
$tmpZip    = Join-Path $env:TEMP "ublock0.chromium.zip"
$tmpUnzip  = Join-Path $env:TEMP "ublock0-unzip"

Write-Host "Querying GitHub for latest $Repo release..."
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "User-Agent" = "MOTD-fetch-ublock" }
$asset = $rel.assets | Where-Object { $_.name -like $AssetPattern } | Select-Object -First 1
if (-not $asset) {
  throw "No asset matching '$AssetPattern' in release $($rel.tag_name)"
}

Write-Host "Latest release: $($rel.tag_name)"
Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size/1MB,1)) MB)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmpZip -UseBasicParsing

# Clean previous extract + target
if (Test-Path $tmpUnzip) { Remove-Item -Recurse -Force $tmpUnzip }
if (Test-Path $extDir)   { Remove-Item -Recurse -Force $extDir }

Write-Host "Unpacking..."
Expand-Archive -Path $tmpZip -DestinationPath $tmpUnzip -Force

# The chromium zip contains a single top-level folder (uBlock0.chromium/).
# Move its CONTENTS into our target so manifest.json sits directly at extDir.
$inner = Get-ChildItem -Path $tmpUnzip -Directory | Select-Object -First 1
if (-not $inner) { throw "Unexpected zip layout: no top-level folder under $tmpUnzip" }
if (-not (Test-Path (Join-Path $inner.FullName "manifest.json"))) {
  throw "manifest.json missing in $($inner.FullName) — release layout changed?"
}

New-Item -ItemType Directory -Force -Path $extDir | Out-Null
Get-ChildItem -Path $inner.FullName -Force | Move-Item -Destination $extDir -Force

# Clean up temp
Remove-Item -Recurse -Force $tmpUnzip -ErrorAction SilentlyContinue
Remove-Item -Force $tmpZip -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "uBlock Origin $($rel.tag_name) extracted to:"
Write-Host "  $extDir"
Write-Host ""
Write-Host "The mod will load it automatically on next WebView spawn. Rebuild"
Write-Host "MOTD.csproj if you haven't already — extension support requires the"
Write-Host "v1.1.0 native plugin (WebView.dll) and runtime ≥ Edge 119."
