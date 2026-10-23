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
* **Dribble Velocity Decay ($V_{max}$):**
  $$V_{max}^{(i)}(t) = \max\Big(V_{min},\; V_{base} - \kappa - \alpha \cdot t_{possess}\Big)$$
  The longer an agent holds the ball, the slower they move. This physical penalty forces agents to choose between selfish dribbling and cooperative passing.
* **Cooperation Reward Structure:**
  $$\mathcal{R}_i = (1 - \lambda) r_i^{ind} + \lambda R_{team}$$
  * $\lambda = 0$: Purely egoistic policy (hoards ball, shoots alone).
  * $\lambda = 1$: Purely utilitarian team policy (maximizes team success via passing).
* **Barrier Deflection System:**
  Field boundary walls deflect loose balls horizontally back into the pitch at $20\text{ m/s}$ ($Y = 0$). Near-boundary buffers automatically release possession so agents cannot trap or pin the ball against the barriers.

---

## 2. Architecture & Components Overview

The system strictly decouples physics, rule-based baselines, and reinforcement learning:

| Script | Role | Description |
| :--- | :--- | :--- |
| **`FootballAgent.cs`** | **Physical Motor & Body** | Implements ground locomotion forces, kinematic ball-holding, dribble decay velocity clamping ($V_{max}$), and 3D kick trajectory impulses. |
| **`FootballAgentML.cs`** | **Reinforcement Learning Brain** | Inherits from `Unity.MLAgents.Agent`. Collects 21 continuous observations, executes continuous/discrete actions, shapes dense rewards, and handles episode resets. Compatible with Unity's New Input System. |
| **`TestBrain.cs`** | **Rule-Based Baseline** | Hardcoded greedy bot (chases ball, acquires possession, shoots in range). Used as the sparring opponent for Phase 1. |
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

### B. Pausing, Resuming, and Saving Progress

* **Will training be lost if I stop?**
  **No!** ML-Agents automatically saves periodic checkpoints and summaries.
* **To stop cleanly:** Click **Stop (⏹)** in Unity or press **`Ctrl + C`** in your PowerShell terminal. ML-Agents will export the neural network weights into:
  ```text
  results/football_yt_01/FootballAgent.onnx
  ```
* **To resume where you left off:** Add the `--resume` flag:
  ```powershell
  mlagents-learn config/football_config.yaml --run-id=football_yt_01 --resume --time-scale=1
  ```
  Hit **Play (▶)** in Unity, and the agent continues with its existing knowledge!

---

### C. Binning / Discarding a Model to Start Fresh

If your agent picked up bad habits or you adjusted rewards and want a clean slate:

* **Option 1 (Force Overwrite):** Add `--force` to erase existing checkpoints:
  ```powershell
  mlagents-learn config/football_config.yaml --run-id=football_yt_01 --force --time-scale=1
  ```
* **Option 2 (New Run ID - Recommended):** Change the run ID to preserve the old run for comparison:
  ```powershell
  mlagents-learn config/football_config.yaml --run-id=football_yt_02 --time-scale=1
  ```
* **Option 3:** Delete the `results/football_yt_01/` directory on disk.

---

### D. Testing Manually (Heuristic Keyboard Mode)

You can play as the agent anytime inside the Unity Editor without running Python:
1. In `Behavior Parameters`, change **Behavior Type** to **`Heuristic Only`**.
2. Click **Play (▶)** in Unity.
3. Controls (via New Input System):
   * **W / A / S / D** or **Arrow Keys**: Move and turn
   * **Spacebar**: Kick the ball with forward impulse

---

## 6. Live Monitoring with TensorBoard

Open a separate PowerShell terminal to view live reward curves and loss graphs:

```powershell
.\.venv\Scripts\Activate.ps1
tensorboard --logdir results
```

Open your browser to:
```text
http://localhost:6006
```

### Key Metrics to Track
* **`Environment/Cumulative Reward`**: Should steadily climb upward as the agent learns to acquire possession and score.
* **`Environment/Episode Length`**: Drops as the bot gets faster at scoring goals.
* **`Policy/Value Loss` & `Policy/Extrinsic Reward`**: Tracks critic accuracy and learning convergence.

---

## 7. Curriculum Roadmap

```
[Phase 1: 1v1 vs TestBrain]
            │  (Locomotion, interception, dribbling, shooting)
            ▼
[Phase 2: 1v1 Self-Play]
            │  (ELO matchmaking against past checkpoints; competitive defense)
            ▼
[Phase 3: 2v2 MARL (Sweeping λ ∈ {0, 0.25, 0.5, 0.75, 1.0})]
            │  (Dribble fatigue forces emergent passing Pareto frontier)
            ▼
[Phase 4: 3v3 Full Match Formations]
               (Passing triangles and tactical roles)
```
