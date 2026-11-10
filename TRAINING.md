# Training Guide: 1v1 Self-Play

The same neural network plays both sides: Team Red (Team Id 0) against Team Blue (Team Id 1).
ML-Agents' self-play keeps a pool of older snapshots of the policy as opponents and tracks ELO.

**Base model:** training starts from the model that beats TestBrain (`saved_models/football_vs_bot_01`, 576k steps).
The config loads it automatically through `init_path`, so no extra flags are needed.

---

## 1. One-time setup

1. **Unity:** install **Unity 6000.3.23f1** (exact version) via Unity Hub. Open the cloned repo folder from Hub.
   The first import takes a while.
2. **Python 3.10.11** (3.11+ will not work with ML-Agents 1.1.0). In the repo folder, in PowerShell:
   ```powershell
   py -3.10 -m venv .venv
   .\.venv\Scripts\Activate.ps1
   pip install -r requirements.txt
   mlagents-learn --help
   ```
   If activation is blocked: `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`, then activate again.
   If `mlagents-learn --help` prints its help text, the install worked.

---

## 2. Start training

1. In Unity open **`Assets/Scenes/SinglePlayerTesting.unity`**. Do not change anything.
   - You should see a red player (`RL Bot`) and a blue player (`RL Bot Blue`). "Heuristic Bot" is disabled.
2. In PowerShell (venv activated), from the repo root:
   ```powershell
   mlagents-learn config/selfplay_1v1.yaml --run-id=selfplay_1v1_01 --time-scale=20
   ```
   The startup log should mention initializing from `saved_models/football_vs_bot_01/FootballAgent/checkpoint.pt`.
3. When the terminal says `Listening on port 5004 ... press the Play button`, press **Play** in Unity.
   - The Console should show `MatchController: 1v1, lambda=0, shaping_scale=1`.
   - A red and a blue player kick off from opposite halves. From the first second both play like the vs-bot model.

**To stop:** press **Ctrl+C once** in the PowerShell window and wait for it to finish. This exports the `.onnx`.
Do not stop with Unity's Stop button or by closing the window.

**To resume after stopping:**
```powershell
mlagents-learn config/selfplay_1v1.yaml --run-id=selfplay_1v1_01 --resume --time-scale=20
```

**If physics looks broken at 20x** (players tunnelling through the ball, ball flying off), use `--time-scale=10`.

---

## 3. Monitoring

In a second PowerShell window (venv activated):
```powershell
tensorboard --logdir results
```
Open http://localhost:6006. What to watch:
- **Self-play/ELO:** this should rise steadily. It is the main progress signal in self-play.
  Cumulative reward hovers around zero in a symmetric game, so it is less useful here.
- **Environment/Episode Length:** 1200 steps = a 24 s draw (same match length as the vs-bot run).
- **Policy/Entropy:** this should decrease slowly, not collapse to 0 early.

---

## 4. Saving and backing up

Everything for a run lives in `results/<run-id>/`:
- `FootballAgent/checkpoint.pt`: the latest weights, used by `--resume`.
- `FootballAgent/FootballAgent-<step>.onnx/.pt`: periodic snapshots (every 250k steps).
- `FootballAgent.onnx`: the final export, written on Ctrl+C or when `max_steps` is reached.

**`results/` is gitignored.** To share a run, copy it into `saved_models/` and commit that:
```powershell
Copy-Item -Recurse results\selfplay_1v1_01 saved_models\selfplay_1v1_01
```
To restore it on another machine, copy it back into `results\` and use `--resume`.

---

## 5. Do NOT change (or the base model stops loading)

- `trainer_type: ppo` and `network_settings` (normalize true, 128 units, 2 layers) in `config/selfplay_1v1.yaml`.
- The 21 observations in `FootballAgentML.CollectObservations` and `Space Size: 21` in Behavior Parameters.

`players_per_team` must stay at 1. The 21-observation agent cannot see other players.

---

## 6. What is happening under the hood

- **`MatchController`** (on the `GameManager` object) is the referee. Both players are set up by hand in the scene and listed in it.
  It resets the pitch and handles goals and the 24 s timeout. It also gives out the goal rewards (as normal per-agent rewards):
  - the scorer gets `goal + speed bonus`;
  - the conceding team gets `-5`.

  These are the same values as the vs-bot run.
- **`FootballAgentML`** keeps the exact 21 observations of the vs-bot run.
  Team Blue's observations and movement are mirrored 180°, so Blue sees the game exactly as Red does.
  That lets one policy play both sides.
- The scorer is now credited by **last touch**. Possession is released when shooting, so the old possession check missed the scorer.
