# Pass or Dribble? Emergent Cooperation in 3D MARL Football

A continuous 3D physics-based football environment in Unity built to investigate the social dilemma of credit assignment and emergent cooperation in Multi-Agent Reinforcement Learning (MARL).

---

## 1. Project Mechanics & Academic Formulation

The environment models football as a Partially Observable Stochastic Game (POSG) based on **`proposal.md`**:

* **Action Space ($\mathcal{A}_i$):**
  $$\mathcal{A}_i = \mathcal{A}_i^{move} \times \mathcal{A}_i^{pass\_flag} \times \mathcal{A}_i^{pass\_angle}$$
  * $\mathcal{A}_i^{move} \in [-1, 1]^2$: Continuous 2D force applied for ground movement.
  * $\mathcal{A}_i^{pass\_flag} \in \{0, 1\}$: Discrete decision to pass or shoot.
  * $\mathcal{A}_i^{pass\_angle} \in [-1, 1]^2$: Continuous 2D vector for horizontal deviation angle ($\pm 45^\circ$) and vertical elevation ($0^\circ$ to $35^\circ$).
* **Discrete Tap-and-Chase Dribble (Push-and-Run):**
  Instead of continuous magnetic pulling, the agent delivers discrete impulse taps ($5.0\text{ N}$) to push the ball ahead $1.2\text{--}2.0\text{ m}$. The ball decelerates via Unity linear damping (drag), and the player sprints to catch up before tapping again.
* **Persistent Possession & Velocity Decay ($V_{max}$):**
  $$V_{max}^{(i)}(t) = \max\Big(V_{min},\; V_{base} - \kappa - \alpha \cdot t_{possess}\Big)$$
  Possession is tracked across the tap-and-chase lifecycle via a Ball Carrier state machine. While the carrier chases its own tap, possession remains active and $V_{max}$ continuously decays from $8\text{ m/s} \to 2.5\text{ m/s}$. If an opponent intercepts or the ball escapes beyond $3.5\text{ m}$, possession transfers or resets.
* **Barrier Deflection System:**
  Field boundary walls deflect loose balls horizontally back into the pitch at $20\text{ m/s}$ ($Y = 0$). Near-boundary buffers automatically release possession so agents cannot trap or pin the ball against the barriers.

---

## 2. Architecture & Components Overview

The system strictly decouples physics, rule-based baselines, and reinforcement learning:

| Script | Role | Description |
| :--- | :--- | :--- |
| **`FootballAgent.cs`** | **Physical Motor & Body** | Implements ground locomotion forces, pure-physics micro-dribble rolling, dribble decay velocity clamping ($V_{max}$), and 3D kick trajectory impulses. |
| **`FootballAgentML.cs`** | **Reinforcement Learning Brain** | Inherits from `Unity.MLAgents.Agent`. Collects 21 continuous observations, executes continuous/discrete actions, shapes dense rewards, and handles episode resets. |
| **`TestBrain.cs`** | **Heuristic Sparring Brain** | Never holds the ball, only moves it with dribble taps, so it can be tackled. Circles round the ball at a safe radius until lined up behind it toward the goal, then runs straight through it (tap, chase, tap). Predicts where the ball is rolling, dodges defenders in its path, shoots away from the nearest defender, and only one bot per team chases the ball. |
| **`ManualPlayerController.cs`** | **Optional Disconnected Human Control** | Completely standalone component for testing player movement with Gamepad/Keyboard/Mouse. Kept disconnected by default so `FreeCamera` can navigate without interference. |
| **`BarrierDeflector.cs`** | **Pitch Boundary Physics** | Deflects loose balls horizontally inward and strips possession to prevent wall-pinning. |
| **`GameManager.cs`** | **Match Referee & Events** | Tracks team scores and broadcasts `OnGoalScoredEvent` to trigger episode termination and rewards. |

---

## 3. Unity & Package Setup (Unity 6)

* **Unity Version:** Unity 6 (6000.x)
* **Embedded Sentis Engine:** Located in `Packages/com.unity.sentis/` (version 2.1.3). Embedding this locally bypasses Unity 6's empty cloud shim, ensuring `Tensor`, `Model`, and `Unity.Sentis` assemblies resolve with zero compilation errors.
* **ML-Agents Package:** `com.unity.ml-agents: 3.0.0` declared in `Packages/manifest.json`.

### Inspector Configuration For The RL Agent

Select your **Player** GameObject in the Unity Hierarchy and configure:

#### A. `Behavior Parameters` Component
* **Behavior Name:** `FootballAgent` *(must match `config/football_config.yaml`)*
* **Vector Observation $\rightarrow$ Space Size:** `21`
* **Stacked Vectors:** `1`
* **Actions $\rightarrow$ Continuous Actions:** `5` *(MoveX, MoveZ, KickAngleX, KickElevY, KickForce)*
* **Actions $\rightarrow$ Discrete Branches:** `1` *(Branch 0 Size: `2`)*
* **Model:** *None* *(during training; assigned automatically upon export)*
* **Behavior Type:** `Default` *(for training)* or `Heuristic Only` *(for manual testing)*

#### B. `FootballAgentML` Component
* **Max Step:** `5000`
* **Agent:** Drag this **Player** GameObject here *(or leave empty for auto-detection)*
* **Target Ball:** Drag **`GlowBall`** from Hierarchy here
* **Opponent Goal:** Drag **`GoalPostBlue`** from Hierarchy here
* **Own Goal:** Drag **`GoalPostRed`** from Hierarchy here
* **Initial Agent Position:** Set to your team's side (e.g. `X: 35, Y: 1, Z: 0`)
* **Initial Ball Position:** `X: 0, Y: 0.15, Z: 0`

#### C. `Decision Requester` Component
* Click **Add Component** $\rightarrow$ **`Decision Requester`**
* **Decision Period:** `5`
* **Take Actions Between Decisions:** Checked

> [!TIP]
> **Opponent Setup:** For Phase 1 training against an AI bot, duplicate the player, assign them to the opponent team, and leave **`FootballAgent`** + **`TestBrain`** attached on the opponent bot!

---

## 4. Python Environment (`.venv`)

A dedicated local virtual environment running **Python 3.10.11** is configured in `.venv`, keeping your system-wide Python 3.14 completely isolated.

### Activating the Virtual Environment

**In PowerShell:**
```powershell
.\.venv\Scripts\Activate.ps1
```

*(If script execution is blocked: `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`)*

**In Command Prompt (cmd):**
```cmd
.\.venv\Scripts\activate.bat
```

To deactivate when finished:
```bash
deactivate
```

### Verified Compatible Dependencies
* `python`: 3.10.11
* `torch`: 2.2.2 (compatible with ML-Agents ONNX exporter)
* `mlagents`: 1.1.0
* `mlagents-envs`: 1.1.0
* `numpy`: 1.23.5
* `protobuf`: 3.20.3
* `onnx`: 1.15.0

---

## 5. How to Train (Step-by-Step)

### A. Recording Live Video for YouTube (Real-Time 1x Speed)

To watch the agent playing live in the Unity window at natural real-time speed while recording in OBS Studio:

```powershell
.\.venv\Scripts\Activate.ps1
mlagents-learn config/football_config.yaml --run-id=football_yt_01 --time-scale=1
```

1. Wait for the terminal message:
   ```text
   [INFO] Listening on port 5004. Start training by pressing the Play button in the Unity Editor...
   ```
2. Click **Play (▶)** in the Unity Editor.
3. Watch the agent play and learn live on the pitch!

> [!NOTE]
> If you want the agent to learn faster during segments where you don't need real-time recording, increase `--time-scale`:
> * `--time-scale=2` (2x speed)
> * `--time-scale=5` or `--time-scale=20` (high-speed background training)

---

### B. Pausing, Resuming, and Saving Progress (Exact Mechanics)

Understanding how weights are saved and exported ensures you never lose training progress:

1. **Continuous Checkpoints (`.pt` files):**
   - As training progresses, ML-Agents saves PyTorch model weights every `summary_freq` (10,000 steps) to:
     ```text
     results/<run-id>/FootballAgent/checkpoint.pt
     results/<run-id>/FootballAgent/FootballAgent-<step>.pt
     ```
   - It retains the last 5 checkpoints (`keep_checkpoints: 5`). Even if your computer crashes or training is interrupted, your weights are safely stored in these `.pt` files.

2. **The Final Inference Model (`.onnx` file):**
   - **When is `.onnx` generated?**
     - When training reaches `max_steps` (1,000,000 steps).
     - **OR** when you cleanly stop training by pressing **`Ctrl + C`** **once** in the terminal running `mlagents-learn`.
   - **Crucial Rule:** Always stop training by pressing **`Ctrl + C` in PowerShell**, not by clicking Stop in Unity or closing the terminal window with the `X` button. ML-Agents requires ~5 seconds to catch the interrupt signal, run `torch.onnx.export()`, and write:
     ```text
     results/<run-id>/FootballAgent.onnx
     ```

3. **To Resume Where You Left Off:**
   - To pick up from the exact last saved step, simply add the `--resume` flag with the same `--run-id`:
     ```powershell
     mlagents-learn config/football_config.yaml --run-id=football_yt_01 --resume --time-scale=1
     ```
   - ML-Agents will output `[INFO] Resuming training from results\football_yt_01`, reload the latest `.pt` weights, buffer, and learning rate, and prompt you to press **Play (▶)** in Unity.

---

### C. Binning / Discarding a Model to Start Fresh

If an agent converged on bad habits or you adjusted rewards and want a clean slate:

* **Option 1 (Force Overwrite):** Add `--force` to wipe the existing folder and start from step 0:
  ```powershell
  mlagents-learn config/football_config.yaml --run-id=football_yt_01 --force --time-scale=1
  ```
* **Option 2 (New Run ID - Highly Recommended):** Increment the run ID (e.g. `football_yt_02`) to keep your previous logs and TensorBoard curves intact:
  ```powershell
  mlagents-learn config/football_config.yaml --run-id=football_yt_02 --time-scale=1
  ```
* **Option 3 (Manual Binning):** Delete the `results/<run-id>/` directory from your file explorer.

---

### D. FreeCamera & Optional Manual Player Control

* **FreeCamera (Default):**
  - The scene camera uses `FreeCamera` (WASD + Mouse) so you can fly around the pitch, spectate, and record video.
  - **`FootballAgentML` is completely decoupled from keyboard/mouse inputs.** Pressing WASD, Space, or clicking the mouse will **never** accidentally drive the bot or trigger kicks.

* **Optional Manual Character Control (`ManualPlayerController.cs`):**
  - If you ever want to manually test physical 360-degree locomotion and kicking without Python:
    1. Attach the `ManualPlayerController` component to any GameObject with `FootballAgent`.
    2. Check the **`Enable Manual Control`** checkbox in the Inspector.
  - **Controls:**
    - **Gamepad (Xbox / PS):** Left Stick for 360° analog locomotion; A button / Right Trigger to kick.
    - **Keyboard:** W/A/S/D (smooth analog traction); Spacebar to kick.
    - **Mouse:** Point anywhere on the turf to aim kicks; Left Click to shoot.

---

## 6. Understanding Early Training & Exploration

### Why Did the Agent Initially Move Around Randomly?
When you start training a brand-new reinforcement learning model, **random movement is completely normal and mathematically intended**:

1. **Neural Network Cold Start:** At step 0, all weights and biases in the policy network are randomly initialized. For continuous locomotion (`MoveX`, `MoveZ`), the policy samples from a Gaussian distribution $\mathcal{N}(\mu, \sigma^2)$ where initial variance $\sigma \approx 1.0$.
2. **Entropy Regularization ($\beta = 0.005$):** The entropy bonus explicitly prevents premature convergence by forcing the agent to explore all directions on the pitch rather than settling into a standstill.
3. **Reward Signal Enhancements in `FootballAgentML.cs`:**
   - **Heading Orientation (21 observations):** The agent now explicitly observes its forward heading (`transform.forward.x`, `transform.forward.z`), allowing it to understand which way it is facing when taking shots.
   - **Amplified Dense Gradient:** The reward for closing the distance to the ball has been increased ($\Delta d_{ball} \times 0.01$), providing a steep hill-climbing gradient so the agent rapidly learns to sprint toward the ball within the first 10,000–30,000 steps.
   - **Goal-Aligned Kick Shaping:** Kicks are now only rewarded if directed toward the opponent's goal ($\text{Dot}(\text{forward}, \text{toGoal}) > 0.25$), eliminating the earlier "hot potato" trap where the agent kicked randomly just to harvest points.

---

## 7. Multi-Agent Passing Roadmap (Transitioning from 1v1 to 2v2/3v3)

The ultimate objective formulated in **`proposal.md`** is emergent cooperative passing governed by the cooperation parameter $\lambda \in [0, 1]$.

### Why the Bots Will Choose to Pass
In naive RL, agents rarely pass because passing carries turnover risks. In our environment, passing is mathematically forced by the **Dribble Velocity Decay Equation**:
$$V_{max}^{(i)}(t) = \max\Big(V_{min},\; V_{base} - \kappa - \alpha \cdot t_{possess}\Big)$$
- A defending bot sprints at **$8.0\text{ m/s}$**.
- An unencumbered teammate sprints at **$8.0\text{ m/s}$**.
- A dribbling agent slows down to **$2.5\text{ m/s}$** after retaining possession for 4+ seconds.
- As the defender closes in, the only way for the ball carrier to avoid being tackled is to pass to their sprinting teammate upfield!

### Step-by-Step Transition to 2v2 Passing:

1. **Scene Setup (2v2 Pitch):**
   - Duplicate the Blue Agent $\rightarrow$ `BlueAgent1` (Striker) and `BlueAgent2` (Midfielder).
   - Duplicate the Red Agent $\rightarrow$ `RedAgent1` and `RedAgent2`.
   - Both agents on each team share the same `Behavior Parameters` name (`FootballAgent`), so both agents train the same neural network concurrently.
2. **Observation Expansion for Teammates & Opponents:**
   - For 2v2, each agent observes:
     - Teammate relative position and velocity ($6$ floats).
     - Opponent relative positions and velocities ($12$ floats).
   - Update `CollectObservations` in `FootballAgentML.cs` and set `Space Size` in the Inspector accordingly.
3. **The Parameterized Cooperation Reward ($\lambda$):**
   - Configured via the `lambda` slider on `FootballAgentML`:
     $$\mathcal{R}_i = (1 - \lambda) r_i^{ind} + \lambda R_{team}$$
     - $\lambda = 0.0$: Pure egoism (player hoards the ball to score alone).
     - $\lambda = 0.5$: Balanced team play (scorer and passer split credit).
     - $\lambda = 1.0$: Utilitarian team play (entire team is rewarded equally for any goal).
4. **Self-Play Training (`config/football_config.yaml`):**
   - Enable Self-Play so Team Blue and Team Red play against past checkpoints of themselves, developing dynamic counter-strategies:
     ```yaml
     behaviors:
       FootballAgent:
         trainer_type: ppo
         self_play:
           save_steps: 20000
           team_change: 100000
           swap_steps: 10000
           play_against_latest_model_ratio: 0.5
           initial_elo: 1200.0
     ```
