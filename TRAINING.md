# Training Guide: 3v3 Self-Play (three λ teams)

Three teams are trained separately with self-play. Each team plays against copies of itself, and **λ is the only difference** between them:

| Team | λ | A teammate's goal is worth | Scene | Config | Run |
|---|---|---|---|---|---|
| NoPass | 0 | nothing | `Assets/Scenes/TeamNoPass.unity` | `config/3v3_NoPass.yaml` | `results/3v3_NoPass` |
| HalfPass | 0.5 | half the goal value | `Assets/Scenes/TeamHalfPass.unity` | `config/3v3_HalfPass.yaml` | `results/3v3_HalfPass` |
| AllPass | 1 | the full goal value | `Assets/Scenes/TeamAllPass.unity` | `config/3v3_AllPass.yaml` | `results/3v3_AllPass` |

The reward is R_i = (1-λ)·r_ind + λ·R_team (proposal §3.3). The scorer, who is the last player to touch the ball, always gets the full goal value. λ decides what the teammates get.

**Lineage:**
- The base starts with vs-TestBrain (576k steps), then 1v1 self-play (`saved_models/selfplay_1v1_01`, 769k steps).
- `tools/convert_to_3v3.py` expands that model to 46 observations, giving `results/3v3_base` with a backup in `saved_models/3v3_base`. The new inputs start with zero weight, so at step 0 it plays exactly like the 1v1 model. The script checks this.

---

## 1. Train all three, unattended

```powershell
cd E:\Football_RL
.\train_3v3.ps1
```
- It runs NoPass, then HalfPass, then AllPass, 1M steps each, at 20x, using the standalone builds in `Builds/`. The Editor isn't needed.
- 1M steps takes about 30 min per team.
- The PC is kept awake while the script runs. Keep the laptop plugged in, with the lid open.
- **It's safe to run again at any time.** Finished teams are skipped, and stopped teams resume from their last checkpoint.
- Stop with **Ctrl+C once**.
- Options:
  - `-Teams HalfPass` trains only one team.
  - `-Build` rebuilds the game first. Unity must be closed.
  - `-MaxSteps 2000000` extends finished runs to 2M steps. It uses a copy of the config, so the YAML files stay unchanged.
  - `-WaitForRunningTraining` waits for a training that is already running to finish before starting.
- Console output goes to `logs/3v3_<Team>.log`. Unity's own log is `results/3v3_<Team>/run_logs/Player-0.log`.
- Each finished run is copied to `saved_models/3v3_<Team>`, because `results/` isn't in git.

**After changing a script or a team scene, rebuild the game**, either from the Unity menu **Football > Build Training Players**, or with `.\train_3v3.ps1 -Build` while Unity is closed.

## 2. Train one team in the Editor (to watch or record)

1. Open the team's scene in Unity.
2. Run this, then press **Play** when it says it's listening:
   ```powershell
   mlagents-learn config/3v3_NoPass.yaml --time-scale=20            # first time
   mlagents-learn config/3v3_NoPass.yaml --time-scale=20 --resume   # continue
   ```
   The Editor and `train_3v3.ps1` use the same run folders, so you can switch between them freely.
   `--resume` continues the run. It does not restart from the base model, because `initialize_from` is ignored on resume.

## 3. Monitoring

```powershell
tensorboard --logdir results
```
Open http://localhost:6006.
- **Match/** shows the proposal's secondary metrics, as averages per match:
  - **Passes:** a teammate gets the ball after it was kicked or released.
  - **Interceptions:** an opponent gets a released ball.
  - **Snatches:** an opponent takes the ball straight off the carrier.
  - **Teammate Steals:** a player takes the ball straight off a teammate.
  - **Goal Scored:** the share of matches with a goal.
- **Self-play/ELO** is relative to each team's own past versions, so don't compare it between teams.
- **Environment/Episode Length:** 600 decisions means a 60 s draw.

---

## 4. What's in a match

- **6 players** (Red: Team Id 0; Blue: Team Id 1). Each player's place in the scene is its kick-off spot. Max Step is 0 and Space Size is 46.
- **`MatchController`** on GameManager is the referee:
  - it holds the team lists, goals, ball and all reward settings;
  - a match lasts 60 s (3000 physics steps) or until a goal;
  - if a scene is set up wrong, it logs the problem and stops play instead of training wrongly.
- **Observations (46):**
  - the original 21, unchanged;
  - 2 teammates plus 3 opponents, nearest first, with relative position x/z, velocity x/z and a has-ball flag for each.

  Blue's observations are mirrored, so one policy can play both sides.
- **Shaping**, with the same weights as the earlier runs:
  - Only the teammate closest to the ball is paid for approaching it, so the team doesn't swarm the ball.
  - Carrying the ball towards goal pays the carrier, and pays λ × that to each teammate.
  - `shaping_scale` is 1.0 for the first 500k steps and 0.5 after that, so goals, and therefore λ, count for more later.
- **Network:** PPO, 128×2, normalized. Don't change `network_settings`, or the base model will no longer load.

## 5. One-time setup on a new machine

1. Install **Unity 6000.3.23f1** and **Python 3.10.11**.
2. Set up the environment:
   ```powershell
   py -3.10 -m venv .venv
   .\.venv\Scripts\Activate.ps1
   pip install -r requirements.txt
   ```
3. Build once with **Football > Build Training Players**. `train_3v3.ps1` restores `results/3v3_base` from `saved_models` on its own.

The old 1v1 stage (`config/selfplay_1v1.yaml`, `SinglePlayerTesting.unity`) has been retired. Its scene still uses 21 observations.
