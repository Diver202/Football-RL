<#
.SYNOPSIS
    Round-robin tournament between the four teams (4C2 = 6 pairings), N matches per pairing, then the report.

.DESCRIPTION
    Uses the exported game (Builds/FinalGame/PassOrDribble.exe) in headless tournament mode: no graphics,
    faster than real time (the physics step is fixed, so the speed does not change the simulation), full
    5-minute matches, sides swapped every match. The six pairings run in parallel, one process each.

    Writes tournament/raw/<A>_vs_<B>.csv (one row per match, flushed after every match). A pairing that stops
    early is resumed from where it stopped. Then runs tools/tournament_report.py, which writes
    tournament/matches.csv, goals.csv, summary.json and RESULTS.md.

.EXAMPLE
    .\tools\run_tournament.ps1                    # 100 matches per pairing, 200x speed
    .\tools\run_tournament.ps1 -Matches 10        # quick run
#>
param(
    [int] $Matches = 100,
    [double] $TimeScale = 200,
    [int] $Retries = 2,
    [switch] $Fresh   # delete earlier logs and start over
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$exe = Join-Path $root 'Builds\FinalGame\PassOrDribble.exe'
if (-not (Test-Path $exe)) { throw "Game not built: $exe (Unity: Football > Build Final Game)" }
$raw = Join-Path $root 'tournament\raw'
$logs = Join-Path $root 'logs\tournament'
if ($Fresh) { Remove-Item $raw -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $raw, $logs | Out-Null

$pairs = @(
    @('NoPass', 'HalfPass'), @('NoPass', 'AllPass'), @('NoPass', 'TestBrain'),
    @('HalfPass', 'AllPass'), @('HalfPass', 'TestBrain'), @('AllPass', 'TestBrain'))

function Done([string]$csv) {
    if (-not (Test-Path $csv)) { return 0 }
    return [Math]::Max(0, (Get-Content $csv | Measure-Object -Line).Lines - 1)
}

$start = Get-Date
for ($attempt = 0; $attempt -le $Retries; $attempt++) {
    $procs = @()
    foreach ($p in $pairs) {
        $name = "$($p[0])_vs_$($p[1])" -replace 'TestBrain$', 'TestBrainBots'
        $csv = Join-Path $raw "$name.csv"
        $done = Done $csv
        if ($done -ge $Matches) { continue }
        Write-Host ("[{0:HH:mm:ss}] {1}: matches {2}-{3}" -f (Get-Date), $name, $done, ($Matches - 1))
        $procs += Start-Process $exe -PassThru -WindowStyle Hidden -ArgumentList @(
            '-batchmode', '-nographics', '-tournament', "`"$csv`"", '-pair', "$($p[0]),$($p[1])",
            '-matches', "$Matches", '-start', "$done", '-timescale', "$TimeScale",
            '-logFile', "`"$(Join-Path $logs "$name.log")`"")
    }
    if ($procs.Count -eq 0) { break }
    while (($procs | Where-Object { -not $_.HasExited }).Count -gt 0) {
        Start-Sleep -Seconds 15
        $status = foreach ($p in $pairs) {
            $name = "$($p[0])_vs_$($p[1])" -replace 'TestBrain$', 'TestBrainBots'
            "{0} {1}/{2}" -f $name, (Done (Join-Path $raw "$name.csv")), $Matches
        }
        Write-Host ("[{0:HH:mm:ss}] " -f (Get-Date)) ($status -join ' | ')
    }
}

$missing = foreach ($p in $pairs) {
    $name = "$($p[0])_vs_$($p[1])" -replace 'TestBrain$', 'TestBrainBots'
    if ((Done (Join-Path $raw "$name.csv")) -lt $Matches) { $name }
}
Write-Host ("Tournament finished in {0:mm\:ss}." -f ((Get-Date) - $start))
if ($missing) { Write-Host "INCOMPLETE: $($missing -join ', ') (see logs\tournament)" -ForegroundColor Red }

& (Join-Path $root '.venv\Scripts\python.exe') (Join-Path $root 'tools\tournament_report.py')
