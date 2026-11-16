<#
.SYNOPSIS
    Trains the three 3v3 teams one after another: NoPass (lambda 0), HalfPass (0.5), AllPass (1).

.DESCRIPTION
    Unattended. Uses the standalone builds in Builds\ (no Editor, no pressing Play), keeps the PC
    awake while it runs, resumes a run that crashed, and backs up every finished run to saved_models\.

    Safe to run again at any time: finished teams are skipped, interrupted teams resume from their
    last checkpoint. Stop with Ctrl+C (once); the next run of this script resumes where it stopped.

    Console output of each run is also written to logs\3v3_<Team>.log.

.EXAMPLE
    .\train_3v3.ps1                       # all three teams at 20x
    .\train_3v3.ps1 -Teams HalfPass       # only one team
    .\train_3v3.ps1 -Build                # rebuild the game first (Unity must be closed)
#>
param(
    [ValidateSet('NoPass', 'HalfPass', 'AllPass')]
    [string[]] $Teams = @('NoPass', 'HalfPass', 'AllPass'),
    [double] $TimeScale = 20,
    [int] $Retries = 2,
    [switch] $Build,
    # Train to this many steps instead of max_steps in the YAML (e.g. 2000000 to extend a finished
    # 1M run by another 1M). The config files are not changed; a copy goes to logs\configs\.
    [int] $MaxSteps = 0,
    # Wait until a training that is already running has finished, then start.
    [switch] $WaitForRunningTraining
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$Learn = Join-Path $PSScriptRoot '.venv\Scripts\mlagents-learn.exe'
$BaseCheckpoint = 'results\3v3_base\FootballAgent\checkpoint.pt'
$BaseBackup = 'saved_models\3v3_base\FootballAgent\checkpoint.pt'
$LogDir = Join-Path $PSScriptRoot 'logs'
$Lambda = @{ NoPass = '0'; HalfPass = '0.5'; AllPass = '1' }

function Say([string]$Message, [string]$Color = 'Gray') {
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message) -ForegroundColor $Color
}

# Highest step saved for a run, read from ML-Agents' own training_status.json (-1 = never started).
function Get-Steps([string]$RunId) {
    $file = "results\$RunId\run_logs\training_status.json"
    if (-not (Test-Path $file)) { return -1 }
    try { $status = Get-Content $file -Raw | ConvertFrom-Json } catch { return 0 }
    $agent = $status.FootballAgent
    if ($null -eq $agent) { return 0 }
    $max = 0
    foreach ($c in @($agent.checkpoints) + @($agent.final_checkpoint)) {
        if ($null -ne $c -and [int]$c.steps -gt $max) { $max = [int]$c.steps }
    }
    return $max
}

function Get-Elo([string]$RunId) {
    $file = "results\$RunId\run_logs\training_status.json"
    if (-not (Test-Path $file)) { return '' }
    try { return '{0:0}' -f (Get-Content $file -Raw | ConvertFrom-Json).FootballAgent.elo } catch { return '' }
}

# The team's config, or (with -MaxSteps) a copy of it with only max_steps changed.
function Get-Config([string]$Team) {
    $config = "config\3v3_$Team.yaml"
    if ($MaxSteps -le 0) { return $config }
    $dir = Join-Path $LogDir 'configs'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir "3v3_${Team}_$MaxSteps.yaml"
    $text = [regex]::Replace((Get-Content $config -Raw), '(?m)^(\s*max_steps:\s*)\d+', "`${1}$MaxSteps")
    [IO.File]::WriteAllText($out, $text)   # UTF-8 without BOM, readable by ML-Agents
    return $out
}

function Get-MaxSteps([string]$Config) {
    $m = Select-String -Path $Config -Pattern '^\s*max_steps:\s*(\d+)' | Select-Object -First 1
    return [int]$m.Matches[0].Groups[1].Value
}

# Unity keeps Temp\UnityLockfile locked while the project is open in the Editor.
function Test-ProjectOpenInUnity {
    $lock = Join-Path $PSScriptRoot 'Temp\UnityLockfile'
    if (-not (Test-Path $lock)) { return $false }
    try { $fs = [IO.File]::Open($lock, 'Open', 'ReadWrite', 'None'); $fs.Close(); return $false }
    catch { return $true }
}

function Invoke-UnityBuild {
    if (Test-ProjectOpenInUnity) {
        throw 'Unity has this project open. Close Unity (or use the menu Football > Build Training Players), then run this script again.'
    }
    $version = (Select-String -Path 'ProjectSettings\ProjectVersion.txt' -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    $unity = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
    if (-not (Test-Path $unity)) { throw "Unity $version not found at $unity" }
    $buildLog = Join-Path $LogDir 'build.log'
    Say "Building the three training players with Unity $version (a few minutes; log: logs\build.log)..." Cyan
    $p = Start-Process -FilePath $unity -Wait -PassThru -ArgumentList @(
        '-batchmode', '-quit', '-nographics', '-projectPath', "`"$PSScriptRoot`"",
        '-executeMethod', 'TrainingBuilds.BuildAllBatch', '-logFile', "`"$buildLog`"")
    if ($p.ExitCode -ne 0) { throw "Build failed (exit code $($p.ExitCode)). See logs\build.log." }
    Say 'Build finished.' Green
}

# ---------------------------------------------------------------- checks
New-Item -ItemType Directory -Force $LogDir | Out-Null

if (-not (Test-Path $Learn)) { throw "ML-Agents not found at $Learn. Set up the .venv first (see TRAINING.md)." }
Add-Type -Namespace Football -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint esFlags);
'@
$ES_CONTINUOUS = [uint32]2147483648
$ES_SYSTEM_REQUIRED = [uint32]1
[void][Football.Power]::SetThreadExecutionState($ES_CONTINUOUS -bor $ES_SYSTEM_REQUIRED)

if ($WaitForRunningTraining) {
    # Start once no training has run for 3 minutes in a row (covers the 30 s pause between retries).
    Say 'Waiting for the training that is already running to finish...' Cyan
    $idleChecks = 0
    while ($idleChecks -lt 18) {
        if (Get-Process mlagents-learn -ErrorAction SilentlyContinue) { $idleChecks = 0 } else { $idleChecks++ }
        Start-Sleep -Seconds 10
    }
}
elseif (Get-Process mlagents-learn -ErrorAction SilentlyContinue) {
    throw 'mlagents-learn is already running. Only one training can run at a time.'
}

if (-not (Test-Path $BaseCheckpoint)) {
    if (Test-Path $BaseBackup) {
        New-Item -ItemType Directory -Force (Split-Path $BaseCheckpoint) | Out-Null
        Copy-Item $BaseBackup $BaseCheckpoint
        Say "Restored the 3v3 base model from $BaseBackup"
    }
    else { throw 'No 3v3 base model. Run:  .venv\Scripts\python.exe tools\convert_to_3v3.py' }
}

$missing = @($Teams | Where-Object { -not (Test-Path "Builds\Team$_\Football.exe") })
if ($Build -or $missing.Count -gt 0) { Invoke-UnityBuild }

# Warn if a script or team scene changed after the game was built (the build would be out of date).
$newestSource = Get-ChildItem 'Assets\Scripts\*.cs', 'Assets\Scenes\Team*.unity' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
foreach ($team in $Teams) {
    $exe = Get-Item "Builds\Team$team\Football.exe"
    if ($newestSource.LastWriteTime -gt $exe.LastWriteTime) {
        Say "WARNING: $($newestSource.Name) changed after Builds\Team$team was built. Rebuild with -Build if that change matters." Yellow
    }
}

$env:PYTHONUNBUFFERED = '1'
$results = @()
try {
    [void][Football.Power]::SetThreadExecutionState($ES_CONTINUOUS -bor $ES_SYSTEM_REQUIRED)
    Say "Training $($Teams -join ', ') at ${TimeScale}x. The PC will not go to sleep while this runs." Green

    foreach ($team in $Teams) {
        $runId = "3v3_$team"
        $config = Get-Config $team
        $exe = "Builds\Team$team\Football.exe"
        $maxSteps = Get-MaxSteps $config
        $log = Join-Path $LogDir "$runId.log"
        $started = Get-Date

        for ($attempt = 0; $attempt -le $Retries; $attempt++) {
            $steps = Get-Steps $runId
            if ($steps -ge $maxSteps) { break }

            if (Test-Path "results\$runId\FootballAgent\checkpoint.pt") { $flag = '--resume'; $what = "resuming from step $steps" }
            elseif (Test-Path "results\$runId") { $flag = '--force'; $what = 'starting over (the previous attempt saved nothing)' }
            else { $flag = ''; $what = 'starting from the 3v3 base model' }

            Say "Team $team (lambda $($Lambda[$team])): $what. Goal: $maxSteps steps." Cyan
            Add-Content -LiteralPath $log -Encoding UTF8 -Value "==== $(Get-Date -Format s)  $what ===="

            # A game left over from a crashed attempt would block the port.
            Get-Process Football -ErrorAction SilentlyContinue | Stop-Process -Force

            $cmd = "`"$Learn`" $config --env=$exe --no-graphics --time-scale=$TimeScale $flag 2>&1"
            cmd /c $cmd | ForEach-Object {
                Write-Host $_
                Add-Content -LiteralPath $log -Encoding UTF8 -Value $_
            }

            if ((Get-Steps $runId) -lt $maxSteps -and $attempt -lt $Retries) {
                Say "Team $team stopped early at step $(Get-Steps $runId). Retrying in 30 s..." Yellow
                Start-Sleep -Seconds 30
            }
        }

        $steps = Get-Steps $runId
        $done = $steps -ge $maxSteps
        if ($done) {
            # results\ is not in git; keep a copy in saved_models\ that can be committed.
            robocopy "results\$runId" "saved_models\$runId" /E /NFL /NDL /NJH /NJS /NP | Out-Null
            Say ("Team {0} finished: {1} steps in {2:hh\:mm}. Backed up to saved_models\{3}" -f $team, $steps, ((Get-Date) - $started), $runId) Green
        }
        else {
            Say "Team $team did NOT finish (step $steps). See logs\$runId.log and results\$runId\run_logs\Player-0.log" Red
        }
        $results += [pscustomobject]@{
            Team = $team; Lambda = $Lambda[$team]; Steps = $steps; ELO = (Get-Elo $runId)
            Status = $(if ($done) { 'done' } else { 'NOT finished' })
            Model = $(if (Test-Path "results\$runId\FootballAgent.onnx") { "results\$runId\FootballAgent.onnx" } else { '' })
        }
    }
}
finally {
    [void][Football.Power]::SetThreadExecutionState($ES_CONTINUOUS)
    Get-Process Football -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host ''
Say 'Summary' Green
$results | Format-Table -AutoSize | Out-String | Write-Host
