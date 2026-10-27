#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$NoCompression
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'LauncherSource\RPGLauncher.csproj'
$outDir = Join-Path $root 'LauncherSource\bin\publish\win-x64'
$exeName = 'RPG Launcher.exe'

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

$publishArgs = @(
    $project,
    '-c', 'Release',
    '-r', 'win-x64',
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

Write-Host 'Publishing RPG Launcher (win-x64)...' -ForegroundColor Cyan
& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$built = Join-Path $outDir $exeName
if (-not (Test-Path $built)) {
    throw "Expected output not found: $built"
}

$target = Join-Path $root $exeName
Copy-Item $built $target -Force

$size = [Math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "Done: $target ($size MB)" -ForegroundColor Green
