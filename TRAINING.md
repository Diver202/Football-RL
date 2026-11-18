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

**Stage 2, "spaced":**
- After 3M steps the teams passed, but all six players huddled around the ball.
- Stage 2 continues each 3M model for 1M more steps with a **spacing penalty**:
  - Players who are not going for the ball are penalised for standing within `spacing_distance` (10 m) of a teammate. "Going for the ball" means the carrier, or the teammate closest to the ball.
  - The closer they are, the bigger the penalty. A whole match spent on top of a teammate costs `spacing_weight` (2). For comparison, a goal is worth 10–15.
  - The penalty is the same for all three teams, so λ is still the only difference between them.

| Team | Config | Starts from | Run |
|---|---|---|---|
| NoPass | `config/3v3_NoPass_spaced.yaml` | `results/3v3_NoPass` (3M) | `results/3v3_NoPass_spaced` |
| HalfPass | `config/3v3_HalfPass_spaced.yaml` | `results/3v3_HalfPass` (3M) | `results/3v3_HalfPass_spaced` |
| AllPass | `config/3v3_AllPass_spaced.yaml` | `results/3v3_AllPass` (3M) | `results/3v3_AllPass_spaced` |

Run it with `.\train_3v3.ps1 -Stage spaced`. Each run is a new folder, so the 3M models stay untouched for comparison. The step counter and learning-rate schedule start again from 0, and `shaping_scale` is fixed at 0.5, the value the 3M runs ended on.

**Result: the first attempt failed.**
- Players stayed about 2 m from a teammate, crowded 98% of the time.
- A test with weight 20 (`config/tests/test_AllPass_spacing20.yaml`) didn't help either.
- **Cause 1:** the exemption for the player closest to the ball meant crowded players could avoid the penalty by racing to be closest, which is the swarming itself.
- **Cause 2:** the inherited policy's movement outputs are mostly far outside ±3, and ML-Agents clips actions to ±3, so its movement exploration is almost zero.
- These configs now set `spacing_exempt_closest: 1`, so they still reproduce that attempt.

**Second attempt (testing):**
- Only the ball carrier is exempt. This is the default, `spacing_exempt_closest: 0`.
- `spacing_weight` is 10.
- `beta` is 0.02, raised from 0.005.
- Test config: `config/tests/test_AllPass_spaced2_w10_b02.yaml`.

**Stage 3, the coach (hierarchical MARL):**
- A coach per team gives each player a role. The players stay independent learners, each with its own policy and its own λ-weighted reward, so λ is still the only difference between the teams.
- **Coach** (`TeamCoach.cs`, `TeamCoachAgent.cs`, behaviour `Coordinator`):
  - it picks a formation (Wide, Attack, Balanced, Defend or Press) at kick-off, when possession switches between the teams, and otherwise every 3 s, never less than 1 s apart. That's about 15–22 decisions per match, and the formation is held in between;
  - every 0.5 s, the team's ball carrier, or else the player closest to the ball, becomes the **chaser**;
  - the other two get **slots** relative to the ball.
  - When learning, it sees the whole match: 43 numbers, including possession.
  - Its reward is zero-sum between the two coaches: ±goal value, plus `coach_territory_weight` (0.1) per metre the ball moves towards the opponent goal, whoever has it.
- **Coach v1 (16 Nov, kept as `results/3v3_<Team>_coachv1`) failed:**
  - it decided every 0.5 s while still near-random, so formations flipped constantly;
  - role adherence fell from 20% to 9%, and the coach learned nothing (flat reward and ELO).
- **Diagnostic** (`config/tests/test_diag_<Team>.yaml`): a random coach at the v2 decision points. The **CoachDiag/** stats show what each formation achieves, by possession.
  - Formation effects are modest, about 1.5–3 m of ground per decision, and possession dominates.
  - Defend is consistently worst on loose balls.
  - Goals per decision are too rare to separate formations, which is why the coach is also rewarded for territory.
- **Players** (49 observations):
  - 3 new inputs: am I the chaser, and the vector to my slot (zero for the chaser);
  - in position = within `role_radius` (5 m) of the slot; this is used for the role-adherence stat;
  - approaching the ball pays only the chaser;
  - the role rewards (`role_hold_weight`, `role_progress_weight`) were 6 / 0.1 in `roles` and `coach`, and are **0 from `coach3` on** (see below).
- **Starting models** (`tools/expand_for_coach.py`): the 3M models, with three changes.
  - **Movement outputs halved.** The game doubles movement (`move_gain: 2`), so play is the same, but the outputs are no longer stuck beyond the ±3 clip, which is what kept the spacing attempts from working.
  - **Slot prior.** The slot-vector inputs start as 1.5 × the network's "run to the ball" weights, so a supporting player treats its slot like a second ball and moves about 60% of the way there.
  - **Chaser unchanged.** The chaser input starts at zero, so the chaser plays exactly as before.
- **Phase 1, `roles`** (300k): a scripted coach picks random formations, so the players learn to follow roles.
- **Phase 2, `coach`:** the learning coach picks formations, and the players continue from phase 1.
  - The players train for 1M steps, and the coach makes about 11k decisions in that time.
  - The coach continues alone to 20k decisions, with the players fixed.
  - Train the three teams in parallel, one window each:
    ```powershell
    .\train_3v3.ps1 -Stage coach -Teams NoPass   -BasePort 5005
    .\train_3v3.ps1 -Stage coach -Teams HalfPass -BasePort 5105
    .\train_3v3.ps1 -Stage coach -Teams AllPass  -BasePort 5205
    ```
- **Configs:** `config/3v3_<Team>_roles.yaml` and `config/3v3_<Team>_coach.yaml`, generated by `tools/make_coach_configs.py`.
- **Run:** `.\train_3v3.ps1 -Stage roles`, then `.\train_3v3.ps1 -Stage coach`.
- **Coach mode:** set on the game's command line (`-coach off|scripted|learned`, in the config's `env_settings`), or with **Coach Mode** on `MatchController` in the Editor.
- **TensorBoard:**
  - **Coach/Role Adherence:** the share of time players with a slot are in it;
  - **Coach/Formation &lt;name&gt;:** how often each formation is used;
  - **Coach/Decisions per match;**
  - **CoachDiag/&lt;possession&gt; - &lt;formation&gt; ground gained / goal diff:** per decision;
  - the coach's own curves appear under the `Coordinator` behaviour.
- **Role rewards were removed (17 Nov, night). From `coach3` on, the players' reward is exactly the original λ reward.**
  - The role rewards turned out to be farmable:
    - holding a slot paid an income every second;
    - "move closer to your slot" skipped the step when the slot jumped.
  - So players earned about 13 per match by walking back to their slots after every formation change, while nobody played the ball:
    - HalfPass, then AllPass, reached 0 passes, 0 interceptions and 0 goals, with full-length matches;
    - NoPass was spared, because its scorer gets the full goal value.
  - Paying the chaser too, and removing only the holding income, didn't fix it. Removing the role rewards did. All three teams were tested identically from the broken models: spacing held at 8–10 m, and passes and goals came back.
  - The coach now only *informs* the players, through 3 inputs plus the slot prior in the network. The code still has `role_hold_weight` and `role_progress_weight`, the progress term is now properly potential-based, but both are 0.
  - Stages trained with the farmable rewards: `roles`, `coach`, and the abandoned `coach2nopress_flawed`.
- **`coach3`:** phase 2 redone with clean rewards. Players start from phase 1, with a new coach, for 1M player steps plus 20k coach decisions. Then **`coach3b`** (continue) or **`coach3nopress`** (continue without Press).
- **Results (17 Nov, morning).** Averages over the last ~100k steps of each run; the 3M column is the last 200k steps. Spacing for 3M was measured later, in the first steps of the `_spaced` runs.

  | | NoPass 3M | NoPass coach3b | NoPass no-Press | HalfPass 3M | HalfPass coach3b | HalfPass no-Press | AllPass 3M | AllPass coach3b | AllPass no-Press |
  |---|---|---|---|---|---|---|---|---|---|
  | nearest teammate | ~1.9 m | 5.0 m | **9.9 m** | ~1.6 m | 4.5 m | **9.0 m** | ~1.8 m | 5.6 m | **11.5 m** |
  | time crowded (<10 m) | ~97% | 89% | 68% | ~98% | 93% | 66% | ~97% | 87% | **42%** |
  | passes / match | 5.5 | 3.5 | 2.1 | 7.2 | 3.2 | 2.5 | 9.6 | 3.3 | 1.1 |
  | interceptions / match | 5.8 | 5.1 | 4.7 | 7.4 | 5.3 | 5.4 | 10.4 | 6.5 | 5.0 |
  | goal in match | 65% | 21% | 18% | 8% | 31% | 24% | 25% | 36% | 26% |
  | coach's formation | – | Press 69% | Balanced 78% | – | Press 71% | even mix | – | Press 67% | Defend 53% |

  - **Every coach that may press ends up mostly pressing,** whatever λ. Given the choice, a team-rewarded coach crowds the ball, so the huddle is a real equilibrium of this game, not only a learning failure.
  - **Without Press, teams stay spread out (9–11.5 m apart) and still score.** The coach's style then differs by λ: NoPass plays Balanced, HalfPass mixes, and AllPass sits back (Defend).
  - **The 3M passes were mostly short taps inside the huddle.** Spread-out teams pass less, because passes have to be longer and more accurate.
  - These are single runs (one seed each), so treat differences of a few percentage points as noise.
  - Models: `Assets/Models/3v3/1_Final_NoPress/` (recommended), `2_Final_WithPress/` and `3_Coach3_Phase2/`, with older stages in `4_Older/`. See `Assets/Models/3v3/README.md`. Backups are in `saved_models/3v3_<Team>_<stage>`.
- **Phase 3 (overnight 17 Nov), superseded by coach3:** both variants continued phase 2, with players and coach starting from `results/3v3_<Team>_coach`, for +1M player steps and +20k coach decisions.
  - **`coach2nopress`:** the coach may not pick Press (`coach_allow_press: 0`, done with action masking). In phase 2 the coaches drifted towards Press (AllPass 78%), which brought players back next to the ball.
  - **`coach2`:** the same rules as phase 2.
- **Watching a coached match in the Editor** (no trainer running):
  1. Open the team's scene, e.g. `TeamAllPass`. Space Size is already 49.
  2. Select the 6 players. In Behavior Parameters set **Model** to the players model, e.g. `Assets/Models/3v3/1_Final_NoPress/AllPass_coach3nopress_players.onnx`, and leave Behavior Type on **Default**.
  3. Select **GameManager**. In `MatchController`:
     - set **Coach Mode** to **Learned**;
     - set **Red Coach Model** and **Blue Coach Model** to the coordinator model, e.g. `1_Final_NoPress/AllPass_coach3nopress_coordinator.onnx`;
     - for a `coach2nopress` model, untick **Coach Allow Press**;
     - check that **Move Gain** is **2**. All coach-stage models need it; it's the code default, but a scene saved earlier may show 1.
  4. Press Play. The coaches are created at runtime as `Coach Red` / `Coach Blue` under GameManager.
  5. **Optional:** to see the random coach of phase 1, set Coach Mode to **Scripted** and use the `_roles_players` model, with no coach model.
- **Coach Mode Off** gives the players zeros for the coach inputs. With coach-stage models (49 inputs) that's fine, but they were trained with a coach, so they play best with one. The older 46-input models (`*_3M`, `*_spaced`) need Space Size 46.

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
  - `-Stage spaced` runs stage 2 (the `_spaced` configs and runs). `-Stage roles` and `-Stage coach` run the two coach phases.
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
  - **Nearest Teammate Distance:** the average distance in metres from a player to its closest teammate. Higher means more spread out.
  - **Crowded Share:** the share of the time players spend within `spacing_distance` of a teammate.
- **Self-play/ELO** is relative to each team's own past versions, so don't compare it between teams.
- **Environment/Episode Length:** 600 decisions means a 60 s draw.

---

## 4. What's in a match

- **6 players** (Red: Team Id 0; Blue: Team Id 1). Each player's place in the scene is its kick-off spot. Max Step is 0 and Space Size is 49. It was 46 before the coach stage; the older 46-input models need Space Size 46, or the `git` history before the coach stage.
- **`MatchController`** on GameManager is the referee:
  - it holds the team lists, goals, ball and all reward settings;
  - a match lasts 60 s (3000 physics steps) or until a goal;
  - if a scene is set up wrong, it logs the problem and stops play instead of training wrongly.
- **Observations (49):**
  - the original 21, unchanged;
  - 2 teammates plus 3 opponents, nearest first, with relative position x/z, velocity x/z and a has-ball flag for each;
  - 3 from the coach: am I the chaser, and the vector to my slot. They are zeros when there is no coach.

  Blue's observations are mirrored, so one policy can play both sides.
- **Shaping**, with the same weights as the earlier runs:
  - Only the teammate closest to the ball is paid for approaching it, so the team doesn't swarm the ball.
  - Carrying the ball towards goal pays the carrier, and pays λ × that to each teammate.
  - `shaping_scale` is 1.0 for the first 500k steps and 0.5 after that, so goals, and therefore λ, count for more later.
  - In stage 2 only, there is also the spacing penalty: `spacing_weight` and `spacing_distance`, both set in the YAML. It is 0 (off) by default.
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

---

## 6. The playable game (`Assets/Scenes/FinalBuild.unity`)

- **Pre-game:** pick Red and Blue (NoPass, HalfPass, AllPass, or TestBrain Bots), then **Kick Off**.
- **Match:** 5 minutes, with no 60-second training resets. After a goal, a banner shows for 2 s and play restarts from kick-off.
- **Full time:** score, result, possession, shots, passes, interceptions, tackles, ball in their half, distance covered, and each coach's main formation. Then **New Match** or **Quit**.
- **Where it lives:**
  - every screen is an ordinary object under **Menu Canvas**;
  - the logic is on **Exhibition Match** (`ExhibitionMatch` and `MatchStats`). Its **Teams** list holds the models for each option, by default the `1_Final_NoPress` ones, along with the match length.
- **Camera:** fixed broadcast view from the +z touchline (FOV 35, height 86 m, 43° down). Red is on the left, like its score.
- **Export:** menu **Football > Build Final Game** builds `Builds/FinalGame/PassOrDribble.exe`.
- **Rebuilding the screens:** `Football > Set Up Final Build Scene` rebuilds them from scratch, replacing them, so hand edits to the screens would be lost. Only use it to start over.
- **Screenshot test:** run the exe with `-autotest <folder>` to play a short scripted session and save a screenshot of every screen.
