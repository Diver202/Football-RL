# Pass or Dribble? Emergent Cooperation in 3D MARL Football

A continuous 3D physics-based football environment in Unity built to investigate the social dilemma of credit assignment and emergent cooperation in Multi-Agent Reinforcement Learning (MARL).

---

## 1. Project Overview & Mechanics

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

## 2. Python Environment Setup (Isolated Python 3.10)

Unity ML-Agents and PyTorch require **Python 3.10.x**. A local virtual environment (`.venv`) has been created in this repository using the machine's installed Python 3.10, ensuring your system-wide Python 3.14 remains completely untouched.

### Activating the Virtual Environment

**In PowerShell:**
```powershell
.\.venv\Scripts\Activate.ps1
```

*(If PowerShell blocks script execution, run: `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`)*

**In Command Prompt (cmd):**
```cmd
.\.venv\Scripts\activate.bat
```

To deactivate when finished:
```bash
deactivate
```

---

### Installing Dependencies

Once the virtual environment is activated:

```bash
# Upgrade pip
python -m pip install --upgrade pip

# Install PyTorch (with CUDA support if you have an NVIDIA GPU, or CPU-only)
# For CUDA 12.1:
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu121

# For CPU only:
# pip install torch torchvision

# Install Unity ML-Agents toolkit
pip install mlagents
```

Verify your setup:
```bash
python --version   # Should report Python 3.10.11
mlagents-learn --help
```

---

## 3. Training Curriculum

Training proceeds in four distinct phases:

```
[Phase 1: 1v1 vs TestBrain] 
            │
            ▼
[Phase 2: 1v1 Self-Play (ELO Checkpoints)]
            │
            ▼
[Phase 3: 2v2 MARL (Testing λ ∈ {0, 0.25, 0.5, 0.75, 1.0})]
            │
            ▼
[Phase 4: 3v3 Full Match Formations]
```

1. **Phase 1 (1v1 vs Heuristic TestBrain):**
   * The RL agent learns ground locomotion, ball interception, stable possession, and kicking towards the goal against the rule-based `TestBrain`.
2. **Phase 2 (1v1 Self-Play):**
   * The learner plays against historical checkpoints of itself using ELO matchmaking, learning competitive defense and shot contesting.
3. **Phase 3 (2v2 Team Cooperation):**
   * Multi-Agent PPO (MAPPO) with Centralized Training & Decentralized Execution (CTDE).
   * As dribble fatigue slows down solo runners, agents learn emergent passing to bypass defenders.
4. **Phase 4 (3v3 Scaling):**
   * Field spacing, passing triangles, and specialized tactical roles (forward, midfielder, defender).

---

## 4. How to Start Training

1. **Open the Unity Scene:**
   * Open `Assets/Scenes/SinglePlayerTesting.unity` in Unity Editor.
2. **Launch the Trainer in PowerShell:**
   ```powershell
   .\.venv\Scripts\Activate.ps1
   mlagents-learn config/football_config.yaml --run-id=phase1_test --time-scale=20
   ```
3. **Press Play in Unity:**
   * When prompted with *"Listening on port 5004. Start training by pressing the Play button in the Unity Editor..."*, press the **Play** button in Unity.
4. **Monitor Live Training with TensorBoard:**
   Open a separate terminal, activate `.venv`, and run:
   ```bash
   tensorboard --logdir results
   ```
   Open `http://localhost:6006` in your browser to view reward curves, possession duration, and policy losses in real-time.
