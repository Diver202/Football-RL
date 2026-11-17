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
    .\train_3v3.ps1 -Stage spaced         # stage 2: continue each 3M model with the spacing penalty
#>
param(
    [ValidateSet('NoPass', 'HalfPass', 'AllPass')]
    [string[]] $Teams = @('NoPass', 'HalfPass', 'AllPass'),
    # Training stage. '' = the original runs (config\3v3_<Team>.yaml -> results\3v3_<Team>).
    # Any other stage X = config\3v3_<Team>_X.yaml -> results\3v3_<Team>_X:
    #   spaced = first spacing attempt;  roles = coach phase 1;  coach = coach phase 2;
    #   coach2 / coach2nopress = phase 2 continued (with / without Press) - trained with the farmable role rewards;
    #   coach3 = phase 2 redone with clean rewards; coach3b / coach3nopress = coach3 continued (with / without Press).
    [ValidateSet('', 'spaced', 'roles', 'coach', 'coach2', 'coach2nopress', 'coach3', 'coach3b', 'coach3nopress')]
    [string] $Stage = '',
    [double] $TimeScale = 20,
    [int] $Retries = 2,
    [switch] $Build,
    # Train to this many steps instead of max_steps in the YAML (e.g. 2000000 to extend a finished
    # 1M run by another 1M). The config files are not changed; a copy goes to logs\configs\.
    [int] $MaxSteps = 0,
    # Wait until a training that is already running has finished, then start.
    [switch] $WaitForRunningTraining,
    # Port for the game connection. Give each run its own (e.g. 5005, 5105, 5205) to train several teams
    # at the same time in separate windows; when -BasePort is given the "already running" check is skipped.
    [int] $BasePort = 5005
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$Learn = Join-Path $PSScriptRoot '.venv\Scripts\mlagents-learn.exe'
$Suffix = $(if ($Stage) { "_$Stage" } else { '' })
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
    $config = "config\3v3_$Team$Suffix.yaml"
    if (-not (Test-Path $config)) { throw "Config $config not found." }
    if ($MaxSteps -le 0) { return $config }
    $dir = Join-Path $LogDir 'configs'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir "3v3_$Team${Suffix}_$MaxSteps.yaml"
    # Only the first max_steps (the players' FootballAgent behavior, always listed first).
    $text = ([regex]'(?m)^(\s*max_steps:\s*)\d+').Replace((Get-Content $config -Raw), "`${1}$MaxSteps", 1)
    [IO.File]::WriteAllText($out, $text)   # UTF-8 without BOM, readable by ML-Agents
    return $out
}

function Get-MaxSteps([string]$Config) {
    $m = Select-String -Path $Config -Pattern '^\s*max_steps:\s*(\d+)' | Select-Object -First 1
    return [int]$m.Matches[0].Groups[1].Value
}

# The checkpoint files a config starts from: checkpoint_settings > initialize_from (a run), or
# per-behavior init_path (a file; behaviors without one, like a new Coordinator, start fresh).
function Get-InitCheckpoints([string]$Config) {
    $m = Select-String -Path $Config -Pattern '^\s*initialize_from:\s*(\S+)' | Select-Object -First 1
    if ($null -ne $m) { return @("results\$($m.Matches[0].Groups[1].Value)\FootballAgent\checkpoint.pt") }
    $paths = @(Select-String -Path $Config -Pattern '^\s*init_path:\s*(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '/', '\' })
    if ($paths.Count -eq 0) { throw "$Config has neither initialize_from nor init_path." }
    return $paths
}

# ML-Agents 1.1.0 bug: a YAML init_path wins over --resume and would silently restart the run from the
# init model. For a resume, use a copy of the config without init_path.
function Get-ResumeConfig([string]$Config) {
    $text = Get-Content $Config -Raw
    if ($text -notmatch '(?m)^\s*init_path:') { return $Config }
    $dir = Join-Path $LogDir 'configs'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir ([IO.Path]::GetFileNameWithoutExtension($Config) + '_resume.yaml')
    [IO.File]::WriteAllText($out, [regex]::Replace($text, '(?m)^\s*init_path:.*\r?\n', ''))
    return $out
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
elseif (-not $PSBoundParameters.ContainsKey('BasePort') -and (Get-Process mlagents-learn -ErrorAction SilentlyContinue)) {
    throw 'mlagents-learn is already running. To train another team at the same time, give this run its own -BasePort (e.g. 5105).'
}

# Game processes of one team's build only (so parallel runs of other teams are left alone).
function Stop-TeamGames([string]$Team) {
    Get-Process Football -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "*\Builds\Team$Team\*" } | Stop-Process -Force
}

# Every team needs the model(s) it starts from. results\ is not in git, so restore a missing one from
# the saved_models\ backup. (Teams already trained or partly trained resume and do not need them.)
foreach ($team in $Teams) {
    if (Test-Path "results\3v3_$team$Suffix\FootballAgent\checkpoint.pt") { continue }
    foreach ($checkpoint in Get-InitCheckpoints "config\3v3_$team$Suffix.yaml") {
        if (Test-Path $checkpoint) { continue }
        $backup = $checkpoint -replace '^results\\', 'saved_models\'
        if (Test-Path $backup) {
            New-Item -ItemType Directory -Force (Split-Path $checkpoint) | Out-Null
            Copy-Item $backup $checkpoint
            Say "Restored $checkpoint from $backup"
        }
        elseif ($checkpoint -like '*3v3_base*') { throw 'No 3v3 base model. Run:  .venv\Scripts\python.exe tools\convert_to_3v3.py' }
        elseif ($checkpoint -like '*_coachbase*') { throw 'No coach base model. Run:  .venv\Scripts\python.exe tools\expand_for_coach.py' }
        else { throw "Team $team starts from $checkpoint, which does not exist. Train that stage first." }
    }
}

$missing = @($Teams | Where-Object { -not (Test-Path "Builds\Team$_\Football.exe") })
if ($Build -or $missing.Count -gt 0) { Invoke-UnityBuild }

# Stop if a script changed after the game was built: an old build would silently train without the
# change (e.g. without the spacing penalty, since it ignores parameters it does not know).
# A newer team scene only warns: Unity also re-saves scenes for harmless reasons (e.g. assigning a
# model for viewing).
$newestScript = Get-ChildItem 'Assets\Scripts\*.cs' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$newestScene = Get-ChildItem 'Assets\Scenes\Team*.unity' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
foreach ($team in $Teams) {
    # (Football.exe keeps the date of Unity's player template, so compare with files every build rewrites.)
    $scripts = Get-Item "Builds\Team$team\Football_Data\Managed\Assembly-CSharp.dll"
    $scene = Get-Item "Builds\Team$team\Football_Data\level0"
    if ($newestScript.LastWriteTime -gt $scripts.LastWriteTime) {
        throw "$($newestScript.Name) changed after Builds\Team$team was built. Rebuild first: Unity menu Football > Build Training Players, or close Unity and add -Build."
    }
    if ($newestScene.LastWriteTime -gt $scene.LastWriteTime) {
        Say "WARNING: $($newestScene.Name) changed after Builds\Team$team was built. Rebuild with -Build if that change matters." Yellow
    }
}

$env:PYTHONUNBUFFERED = '1'
$results = @()
try {
    [void][Football.Power]::SetThreadExecutionState($ES_CONTINUOUS -bor $ES_SYSTEM_REQUIRED)
    Say "Training $($Teams -join ', ')$(if ($Stage) { " (stage: $Stage)" }) at ${TimeScale}x. The PC will not go to sleep while this runs." Green

    foreach ($team in $Teams) {
        $runId = "3v3_$team$Suffix"
        $config = Get-Config $team
        $initFrom = (Get-InitCheckpoints $config) -join ', '
        $exe = "Builds\Team$team\Football.exe"
        $maxSteps = Get-MaxSteps $config
        $log = Join-Path $LogDir "$runId.log"
        $started = Get-Date

        for ($attempt = 0; $attempt -le $Retries; $attempt++) {
            $steps = Get-Steps $runId
            if ($steps -ge $maxSteps) { break }

            if (Test-Path "results\$runId\FootballAgent\checkpoint.pt") { $flag = '--resume'; $what = "resuming from step $steps" }
            elseif (Test-Path "results\$runId") { $flag = '--force'; $what = 'starting over (the previous attempt saved nothing)' }
            else { $flag = ''; $what = "starting from $initFrom" }

            Say "Team $team (lambda $($Lambda[$team])): $what. Goal: $maxSteps steps." Cyan
            Add-Content -LiteralPath $log -Encoding UTF8 -Value "==== $(Get-Date -Format s)  $what ===="

            # A game left over from a crashed attempt would block the port.
            Stop-TeamGames $team

            $runConfig = $(if ($flag -eq '--resume') { Get-ResumeConfig $config } else { $config })
            $cmd = "`"$Learn`" $runConfig --env=$exe --no-graphics --time-scale=$TimeScale --base-port=$BasePort $flag 2>&1"
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
    foreach ($team in $Teams) { Stop-TeamGames $team }
}

Write-Host ''
Say 'Summary' Green
$results | Format-Table -AutoSize | Out-String | Write-Host
