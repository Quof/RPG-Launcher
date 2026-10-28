#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$NoCompression
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'LauncherSource\RPGLauncher.csproj'
$outDir = Join-Path $root "LauncherSource\bin\publish\$Runtime"
$windows = $Runtime -like 'win-*'
$exeName = if ($windows) { 'RPG Launcher.exe' } else { 'RPG Launcher' }

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

$publishArgs = @(
    $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-p:PublishSingleFile=true',
    '-p:DebugType=None',
    '-o', $outDir,
    '-v', 'minimal'
)

if ($FrameworkDependent) {
    $publishArgs += '--self-contained', 'false'
} else {
    $publishArgs += '--self-contained', 'true'
    $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    if (-not $NoCompression) {
        $publishArgs += '-p:EnableCompressionInSingleFile=true'
    }
}

Write-Host "Publishing RPG Launcher ($Runtime)..." -ForegroundColor Cyan
& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$built = Join-Path $outDir $exeName
if (-not (Test-Path $built)) {
    throw "Expected output not found: $built"
}

# The Windows build replaces the exe next to this script; other builds stay in the publish folder.
$target = if ($windows) { Join-Path $root $exeName } else { $built }
if ($windows) { Copy-Item $built $target -Force }

$size = [Math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "Done: $target ($size MB)" -ForegroundColor Green
