<#
.SYNOPSIS
  Builds bucket/manifest.json, the list of hosted VIPRPG zips the launcher reads.

.DESCRIPTION
  Every .zip under bucket/games is listed with its size and SHA-256. The bucket folder mirrors the
  R2 bucket; upload.ps1 sends it there.

  games/matsuri/<matsuri-id>/<No>.zip   a matsuri work, named by the launcher catalog's id and entry number
  games/thread/**/*.zip                 anything else (thread games and so on), any ASCII name and subfolders

  Titles, authors and the rest come from games.csv, one row per file. Zips under games/thread that have
  no row yet get one added, with a title guessed from RPG_RT.ini's GameTitle or the zip's top folder,
  so it can be checked in a spreadsheet. A row can also set matsuri (and no) for a matsuri work that
  has no entry number.

  Hashes are cached in .manifest-cache.json, so only new or changed zips are read again.

.EXAMPLE
  .\build-manifest.ps1 -Catalog "F:\ISO 2\MTL Madness\RPG Launcher\LauncherSource\Assets\vip-catalog.json"
#>
param(
    [string]$Bucket = (Join-Path $PSScriptRoot 'bucket'),
    [string]$Csv = (Join-Path $PSScriptRoot 'games.csv'),
    # The launcher's vip-catalog.json: checks matsuri ids and numbers, and fills in their titles.
    [string]$Catalog = '',
    # Where the launcher's built-in copy of the list goes ('' to skip).
    [string]$BuiltIn = (Join-Path $PSScriptRoot '..\LauncherSource\Assets\vip-server.json.gz'),
    # Folders kept in the bucket for preservation but left out of the manifest (the download Worker refuses them too):
    # the WOLF RPG Editor contest games. Their games.csv rows are kept.
    [string[]]$Withheld = @('games/wolf/')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# area and section group thread games in the launcher's 保管庫 list: one entry per area, under its section's heading.
$Columns = 'path', 'title', 'author', 'thread', 'year', 'engine', 'matsuri', 'no', 'note', 'area', 'section'

if ($PSVersionTable.PSEdition -eq 'Core') {
    [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
}
$ShiftJis = [Text.Encoding]::GetEncoding(932)
$Utf8NoBom = New-Object Text.UTF8Encoding $false
$Utf8Bom = New-Object Text.UTF8Encoding $true

$gamesDir = Join-Path $Bucket 'games'
if (-not (Test-Path $gamesDir)) { throw "No games folder at $gamesDir" }
$bucketRoot = (Resolve-Path $Bucket).Path.TrimEnd('\', '/')

function Get-RelativePath([string]$full) {
    $full.Substring($bucketRoot.Length + 1).Replace('\', '/')
}

function Get-TitleGuess([string]$zipPath) {
    # VIPRPG zips are usually Shift-JIS without the UTF-8 flag; .NET still honours the flag when set.
    try {
        $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Read', $ShiftJis)
    } catch { return $null }
    try {
        $ini = $zip.Entries | Where-Object { $_.Name -ieq 'RPG_RT.ini' } |
            Sort-Object { ($_.FullName -split '/').Count } | Select-Object -First 1
        if ($ini -and $ini.Length -lt 64KB) {
            # A damaged entry only costs the guess; the folder name below can still give one.
            $text = $null
            try {
                $reader = New-Object IO.StreamReader($ini.Open(), $ShiftJis)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            } catch { $text = $null }
            if ($text -match '(?im)^\s*GameTitle\s*=\s*(.+?)\s*$') { return $Matches[1] }
        }
        $tops = @($zip.Entries | ForEach-Object { ($_.FullName -split '/')[0] } | Where-Object { $_ } | Select-Object -Unique)
        if ($tops.Count -eq 1 -and $zip.Entries.Count -gt 1) { return $tops[0] }
        return $null
    } finally { $zip.Dispose() }
}

# Some matsuri number works 001, 002…; 1.zip still matches 001, and the manifest uses the catalog's spelling.
function Get-NoKey([string]$no) {
    ($no -replace '(?<!\d)0+(?=\d)', '').ToUpperInvariant()
}

# --- catalog (optional) ---
$catalogWorks = @{}
$catalogMatsuri = @{}
if ($Catalog) {
    $cat = Get-Content $Catalog -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($m in $cat.matsuri) { $catalogMatsuri[$m.id] = $m }
    foreach ($w in $cat.games) { if ($w.no) { $catalogWorks["$($w.matsuri)/$(Get-NoKey $w.no)"] = $w } }
}

# --- games.csv ---
$rows = [ordered]@{}
if (Test-Path $Csv) {
    foreach ($r in [IO.File]::ReadAllText($Csv, $Utf8NoBom).TrimStart([char]0xFEFF) | ConvertFrom-Csv) {
        if ($r.path) { $rows[$r.path.Trim().Replace('\', '/')] = $r }
    }
}

# --- hash cache ---
$cachePath = Join-Path $PSScriptRoot '.manifest-cache.json'
$cache = @{}
if (Test-Path $cachePath) {
    foreach ($p in (Get-Content $cachePath -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties) { $cache[$p.Name] = $p.Value }
}

$warnings = New-Object Collections.Generic.List[string]
$problems = New-Object Collections.Generic.List[string]
$games = New-Object Collections.Generic.List[object]
$newCache = @{}
$added = 0

$zips = @(Get-ChildItem $gamesDir -Recurse -File -Filter *.zip | Sort-Object FullName)
$i = 0
foreach ($file in $zips) {
    $i++
    $path = Get-RelativePath $file.FullName
    Write-Progress -Activity 'Listing games' -Status $path -PercentComplete ($i * 100 / $zips.Count)

    if ($path -notmatch '^[A-Za-z0-9._/-]+$') { $warnings.Add("Non-ASCII or unusual characters in name: $path") }

    $stamp = $file.LastWriteTimeUtc.Ticks
    $cached = $cache[$path]
    if ($cached -and $cached.size -eq $file.Length -and $cached.modified -eq $stamp) {
        $hash = $cached.sha256
        $guess = $cached.guess
    } else {
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $guess = Get-TitleGuess $file.FullName
    }
    $newCache[$path] = [ordered]@{ size = $file.Length; modified = $stamp; sha256 = $hash; guess = $guess }

    $row = $rows[$path]
    $entry = [ordered]@{ path = $path; size = $file.Length; sha256 = $hash }

    if ($path -match '^games/matsuri/([^/]+)/([^/]+)\.zip$') {
        $matsuri = $Matches[1]; $no = $Matches[2]
    } elseif ($path -match '^games/matsuri/') {
        $problems.Add("Matsuri works go at games/matsuri/<matsuri-id>/<No>.zip: $path")
        continue
    } else {
        $matsuri = $null; $no = $null
        if (-not $row) {
            $row = [pscustomobject]([ordered]@{ path = $path; title = $guess; author = ''; thread = ''; year = ''; engine = ''; matsuri = ''; no = ''; note = ''; area = ''; section = '' })
            $rows[$path] = $row
            $added++
        }
    }
    if ($row -and $row.matsuri) { $matsuri = $row.matsuri.Trim() }
    if ($row -and $row.no) { $no = $row.no.Trim() }

    $work = $null
    if ($matsuri) {
        if ($Catalog) {
            if (-not $catalogMatsuri.ContainsKey($matsuri)) { $warnings.Add("Matsuri id not in the catalog: $matsuri ($path)") }
            elseif ($no) {
                $work = $catalogWorks["$matsuri/$(Get-NoKey $no)"]
                if ($work) { $no = $work.no }
                else { $warnings.Add("Entry $no isn't in $matsuri in the catalog ($path)") }
            }
        }
        $entry.source = 'matsuri'
        $entry.matsuri = $matsuri
        if ($no) { $entry.no = $no }
    } else {
        $entry.source = 'thread'
    }

    foreach ($col in 'title', 'author', 'thread', 'year', 'engine', 'note', 'area', 'section') {
        $value = $null
        if ($row -and $row.$col) { $value = $row.$col.Trim() }
        elseif ($work -and $col -in 'title', 'author', 'engine' -and $work.$col) { $value = $work.$col }
        elseif ($col -eq 'title' -and $guess) { $value = $guess }
        if ($value) {
            if ($col -eq 'year' -and $value -match '^\d+$') { $value = [int]$value }
            $entry[$col] = $value
        }
    }
    if (-not $entry.title) { $warnings.Add("No title: $path") }
    # A replacement character means the title was decoded with the wrong code page when it was staged.
    elseif ($entry.title.Contains([char]0xFFFD)) { $warnings.Add("Garbled title (fix it in games.csv): $path") }
    $games.Add($entry)
}
Write-Progress -Activity 'Listing games' -Completed

foreach ($p in @($rows.Keys)) {
    if (-not (Test-Path -LiteralPath (Join-Path $bucketRoot $p))) { $warnings.Add("games.csv lists a file that isn't there: $p") }
}

# --- write ---
$withheldCount = 0
if ($Withheld) {
    $kept = New-Object Collections.Generic.List[object]
    foreach ($g in $games) {
        $p = [string]$g.path
        if (@($Withheld | Where-Object { $p.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count) { $withheldCount++ }
        else { $kept.Add($g) }
    }
    $games = $kept
}
$manifest = [ordered]@{
    version   = 1
    generated = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    count     = $games.Count
    games     = $games
}
$manifestJson = ConvertTo-Json $manifest -Depth 5 -Compress
[IO.File]::WriteAllText((Join-Path $Bucket 'manifest.json'), $manifestJson, $Utf8NoBom)
[IO.File]::WriteAllText($cachePath, (ConvertTo-Json $newCache -Depth 3 -Compress), $Utf8NoBom)

# The same list, gzipped, built into the launcher: its lists show even before the server answers.
if ($BuiltIn) {
    $bytes = $Utf8NoBom.GetBytes($manifestJson)
    $out = [IO.File]::Create($BuiltIn)
    try {
        $gzip = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
        $gzip.Write($bytes, 0, $bytes.Length)
        $gzip.Dispose()
    } finally { $out.Dispose() }
    Write-Host ("Launcher's built-in list: {0} ({1:N1} MB); rebuild the launcher to include it" -f $BuiltIn, ((Get-Item $BuiltIn).Length / 1MB))
}

$csvRows = foreach ($r in $rows.Values) { $r | Select-Object -Property $Columns }
$csvText = if ($csvRows) { ($csvRows | ConvertTo-Csv -NoTypeInformation) -join "`r`n" } else { ($Columns | ForEach-Object { "`"$_`"" }) -join ',' }
# With a BOM so Excel opens the Japanese text correctly.
[IO.File]::WriteAllText($Csv, $csvText + "`r`n", $Utf8Bom)

Write-Host ("manifest.json: {0} games ({1} matsuri, {2} thread)" -f $games.Count,
    @($games | Where-Object { $_.source -eq 'matsuri' }).Count, @($games | Where-Object { $_.source -eq 'thread' }).Count)
if ($withheldCount) { Write-Host ("withheld (kept in the bucket, not listed): {0} games under {1}" -f $withheldCount, ($Withheld -join ', ')) }
if ($added) { Write-Host "games.csv: added $added new rows to check" }
foreach ($w in $warnings) { Write-Host "warning: $w" -ForegroundColor Yellow }
foreach ($p in $problems) { Write-Host "problem: $p" -ForegroundColor Red }
if ($problems.Count) { exit 1 }
