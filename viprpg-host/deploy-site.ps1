<#
.SYNOPSIS
  Builds the index website (festival and 保管庫 pages, search, pictures, manifest.json) and publishes it to
  Cloudflare (static files on Workers, the current form of Pages).

.DESCRIPTION
  Run it after build-manifest.ps1, whenever the manifest or the launcher's catalog or pictures change. The site is
  only the index: its download buttons point at the download Worker, which keeps its per-IP limits. The launcher
  reads its list (manifest.json) from the site, so Pages answers launcher starts and the Worker only downloads.

  Needs wrangler signed in (npx wrangler login, in the worker folder), as for deploying the Worker.

.EXAMPLE
  .\deploy-site.ps1
  .\deploy-site.ps1 -NoDownloads    # the works without download buttons
#>
param(
    # The real bucket folder (its manifest.json is what the site and the launcher list).
    [string]$Bucket = 'E:\VIPRPG Host\bucket',
    [switch]$NoDownloads,
    # Build only; don't publish.
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'site-out'
$build = @((Join-Path $PSScriptRoot 'site\build-site.py'), '--bucket', $Bucket, '--out', $out)
if ($NoDownloads) { $build += '--no-downloads' }
& python @build
if ($LASTEXITCODE -ne 0) { throw "build-site.py failed with exit code $LASTEXITCODE" }
if ($BuildOnly) { return }

Push-Location (Join-Path $PSScriptRoot 'worker')
try {
    # Static files on Workers (site\wrangler.toml), the current form of Cloudflare Pages: free, no request limit.
    & npx.cmd wrangler deploy --config '..\site\wrangler.toml'
    if ($LASTEXITCODE -ne 0) { throw "wrangler deploy failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }
