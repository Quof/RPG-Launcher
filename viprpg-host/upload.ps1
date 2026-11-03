<#
.SYNOPSIS
  Rebuilds manifest.json and uploads the bucket folder to Cloudflare R2 with rclone.

.DESCRIPTION
  Only new or changed files are sent. Files in R2 that are gone from the bucket folder are kept,
  unless -Delete is given. The manifest goes up last, so it never lists a zip that isn't there yet.

  Needs rclone with a remote set up for R2 (see README.md).

.EXAMPLE
  .\upload.ps1 -Catalog "F:\ISO 2\MTL Madness\RPG Launcher\LauncherSource\Assets\vip-catalog.json"
#>
param(
    [string]$Bucket = (Join-Path $PSScriptRoot 'bucket'),
    # rclone remote and R2 bucket name.
    [string]$Remote = 'r2:viprpg',
    [string]$Catalog = '',
    # Also remove files from R2 that are no longer in the bucket folder.
    [switch]$Delete,
    # Show what would be uploaded without sending anything.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command rclone -ErrorAction SilentlyContinue)) {
    throw "rclone isn't installed or isn't on PATH (winget install Rclone.Rclone, then open a new terminal)"
}

& (Join-Path $PSScriptRoot 'build-manifest.ps1') -Bucket $Bucket -Catalog $Catalog
if ($LASTEXITCODE) { throw 'build-manifest.ps1 reported problems; fix them first' }

# rclone writes progress to stderr; Windows PowerShell would turn that into errors under 'Stop'.
$ErrorActionPreference = 'Continue'

$common = @('--s3-no-check-bucket', '--transfers', '8', '--progress', '--stats-one-line')
if ($DryRun) { $common += '--dry-run' }

$verb = if ($Delete) { 'sync' } else { 'copy' }
rclone $verb (Join-Path $Bucket 'games') "$Remote/games" @common `
    --header-upload 'Content-Disposition: attachment' `
    --header-upload 'Cache-Control: public, max-age=86400'
if ($LASTEXITCODE) { throw "rclone $verb failed ($LASTEXITCODE)" }

rclone copyto (Join-Path $Bucket 'manifest.json') "$Remote/manifest.json" @common `
    --header-upload 'Content-Type: application/json; charset=utf-8' `
    --header-upload 'Cache-Control: public, max-age=300'
if ($LASTEXITCODE) { throw "rclone copyto manifest.json failed ($LASTEXITCODE)" }

Write-Host $(if ($DryRun) { 'Dry run done; nothing was sent.' } else { 'Uploaded.' })
