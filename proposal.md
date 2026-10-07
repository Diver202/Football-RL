# PASS OR DRIBBLE? EMERGENT COOPERATION IN A 3D MARL FOOTBALL ENVIRONMENT

**Divyansh Sharma**
Computer Science and Engineering
Indian Institute of Technology Gandhinagar
Gandhinagar, Gujarat
divyansh.sharma@iitgn.ac.in

**Ankit Saha**
Computer Science and Engineering
Indian Institute of Technology Gandhinagar
Gandhinagar, Gujarat
ankit.saha@iitgn.ac.in

## ABSTRACT

Coordination in multi-agent reinforcement learning (MARL) frequently deteriorates when collective success requires individual sacrifice. We formulate a continuous-space, physics-based 3D football match to investigate the social dilemma of credit assignment. By parameterizing the reward structure using a cooperation coefficient variable, we enforce a trade-off between playing selfishly (dribbling/scoring individually) and cooperative utility (passing to avoid physical movement penalties and bypass opponents). This project aims to map the phase transitions where independent learning agents abandon purely egoistic policies in favor of emergent cooperative team strategies under centralized training with decentralized execution (CTDE).

**Keywords:** Multi-Agent Reinforcement Learning, Partially Observable Stochastic Game, Cooperation Coefficient, Emergent Coordination

---

## 1 Introduction

The resolution of social dilemmas in multi-agent systems is a foundational challenge in artificial intelligence [1]. In cooperative-competitive environments, agents often converge to sub-optimal Nash equilibria when individual incentives conflict with global team objectives.

**The Decision Problem:** The core problem we investigate is the continuous credit assignment dilemma in a competitive physical environment. Agents must dynamically decide whether to act selfishly (retaining possession to maximize individual reward probability) or cooperatively (passing the ball to teammates to increase overall team success probability). This shifts the classic iterated Prisoner's Dilemma from an abstract payoff matrix into a continuous spatial environment where cooperation is governed by physical constraints (velocity decay, momentum, and interception risks) [2].

**The Research Question:** Specifically, this project investigates how the agents' parameterized willingness to cooperate ($\lambda$) affects the final outcome of the game (determined by the final score and win rate) and the emergent gameplay dynamics (frequency of passes, ball snatches, and possession time).

By parameterizing the reward function with a continuous scalar governing altruism, we evaluate how cooperative MARL algorithms [5] balance the credit assignment problem to develop robust team strategies within a 3D continuous-space Unity environment [4].

We wish to gain interesting insights on how the inherent nature of players changes the outcome of the overall game. Football offers a familiar picture of this tension. For example, in the 2026 FIFA World Cup quarter-final, Norway's Alexander Sørloth chose to take on a defender rather than pass the ball to an unmarked Erling Haaland. This decision cost Norway a golden opportunity, and they ultimately lost 2-1 to England [7, 8]. This captures exactly the trade-off we study: a player can keep the ball and chase individual reward, or release it and trust the team.

---

## 2 Preliminaries

The environment is modeled as a Partially Observable Stochastic Game (POSG) characterized by the tuple $(\mathcal{I}, \mathcal{S}, \{\mathcal{A}\}, \mathcal{T}, \{\mathcal{R}\}, \{\Omega_{i}\}, \mathcal{O}, \gamma, H)$.

*   $\mathcal{I}=\mathcal{I}_{A}\cup\mathcal{I}_{B}$ represents the finite set of agents partitioned into two opposing teams.
*   $\mathcal{S}$ is the continuous state space representing the true physical state of the environment (exact XYZ coordinates, velocities, and rotation quaternions of every player and the ball).
*   $\mathcal{A}_{i}$ is the mixed action space for agent $i$ (detailed in Section 3).
*   $\mathcal{T}:\mathcal{S}\times\mathcal{A}\rightarrow\Delta(\mathcal{S})$ is the transition function, governed by Unity's PhysX engine for calculating physics updates, including gravity and parabolic trajectories.
*   $\mathcal{R}_{i}$ is the reward function for agent $i$, dependent on the hyperparameter $\lambda$.
*   $\Omega_{i}$ is the continuous observation space for agent $i$, comprised of local 3D raycast data.
*   $\mathcal{O}:\mathcal{S}\times\mathcal{A}\rightarrow\Delta(\Omega)$ is the observation function determining what agent $i$ actually perceives.
*   $\gamma\in[0,1)$ is the discount factor, kept high due to the delayed-reward nature of scoring a goal.
*   $H$ is the finite episode horizon, representing the maximum number of simulation steps per match.

**Definition 1 (Cooperation Coefficient).** $\lambda\in[0,1]$ is a scalar parameter that weights team reward against individual reward in $\mathcal{R}_{i}$. Setting $\lambda=0$ generates purely individualistic optimization, while $\lambda=1$ generates purely team-oriented utilitarian optimization.

| Symbol | Meaning |
| :--- | :--- |
| $\mathcal{I}_{A}$, $\mathcal{I}_{B}$ | Agents belonging to team A and team B |
| $\mathbb{I}_{possess}(i,t)$ | Boolean indicator that agent $i$ currently controls the ball |
| $V_{base}$, $\kappa$ | Base unencumbered speed, and the dribble speed penalty |
| $\lambda$ | Cooperation coefficient |
| $r_{i}^{ind}$, $R_{team}$ | Individual goal reward and shared team goal reward |

*Table 1: Summary of primary variables and notation.*

---

## 3 Problem Formulation

### 3.1 Agents and Action Space

The environment consists of two competing teams, initially planned as a 5v5 physics-based matchup (subject to scaling down to 3v3 based on computational complexity). Let the global state $s_{t}\in\mathcal{S}$ contain the position $p_{t}^{(j)}\in\mathbb{R}^{3}$ and velocity $v_{t}^{(j)}\in\mathbb{R}^{3}$ for all agents $j\in\mathcal{I}$, alongside the ball's physical state $p_{t}^{(B)},v_{t}^{(B)}$.

Each agent $i$ acts independently at time $t$. The action space $\mathcal{A}_{i}$ is defined as $\mathcal{A}_{i}=\mathcal{A}_{i}^{move}\times\mathcal{A}_{i}^{pass\_flag}\times\mathcal{A}_{i}^{pass\_angle}$:
*   $\mathcal{A}_{i}^{move}\in[-1,1]^{2}$ represents continuous force vectors applied to the agent's X and Z axes for ground movement.
*   $\mathcal{A}_{i}^{pass\_flag}\in\{0,1\}$ is a discrete boolean action indicating the decision to execute a pass.
*   $\mathcal{A}_{i}^{pass\_angle}\in[-1,1]^{2}$ represents a continuous 2D vector determining the horizontal direction and vertical (Y-axis) elevation angle of the trajectory.

### 3.2 Known and Unknown Variables

To strictly adhere to the POSG framework with partial observability, there is a strict separation of information during the execution phase:

*   **What is Known:** Agents know their own local physical state (current velocity) and the environment geometry immediately visible to them. This is captured via the observation space $\Omega_{i}$, which uses 3D Raycasts to detect the distance and tags of the ball, walls, and players within line-of-sight. Agents also know their assigned cooperation coefficient ($\lambda$).
*   **What is Unknown:** Agents do not know the true global state $\mathcal{S}$ (e.g., players behind them). Crucially, they do not know the intended actions or internal policies of their teammates or opponents. They must infer cooperative opportunities purely through spatial observation.

### 3.3 Environment Dynamics and Reward Structure

The physical dynamics impose a localized constraint on the agent possessing the ball, defined by an indicator function $\mathbb{I}_{possess}(i,t)$. The maximum velocity $V_{max}$ of agent $i$ decays according to a dribble penalty constant $\kappa$, simulating the physical difficulty of retaining the ball:

$$V_{max}^{(i)}(t)=V_{base}-\kappa\cdot\mathbb{I}_{possess}(i,t)$$

As the expression suggests, the player moves slower while they keep holding the ball.

The individual intrinsic reward for agent $i$ when a goal is scored is denoted as $r_{i}^{ind}$, which is strictly positive if agent $i$ was the final actor to contact the ball, and $0$ otherwise. The global team reward is $R_{team}$. The shaped optimization target $\mathcal{R}_{i}$ for agent $i$ is parameterized by $\lambda$:

$$\mathcal{R}_{i}(s_{t},\vec{a}_{t})=(1-\lambda)r_{i}^{ind}(t)+\lambda R_{team}(t)$$

The formal objective is to find a joint policy $\pi=\langle\pi_{1},...,\pi_{N}\rangle$ that maximizes the expected discounted return under varying limits of $\lambda$:

$$J(\pi,\lambda)=\mathbb{E}_{\tau\sim\pi}\left[\sum_{t=0}^{H}\gamma^{t}\mathcal{R}_{i}(s_{t},\vec{a}_{t})\right]$$

---

## 4 Proposed Solutions and Evaluation

### 4.1 Solution Approach

To solve the formulated POSG, we will implement Multi-Agent Proximal Policy Optimization (MAPPO) utilizing a Centralized Training with Decentralized Execution (CTDE) architecture. During training, a centralized critic network will evaluate the joint continuous state $\mathcal{S}$ to stabilize the highly non-stationary environment. During evaluation, the decentralized actors will output actions relying strictly on their partial local raycast observations $\Omega_{i}$.

### 4.2 Evaluation Metrics and Baselines

We will execute identical training environments simultaneously, sweeping the cooperation coefficient across discrete intervals ($\lambda \in \{0.0, 0.25, 0.5, 0.75, 1.0\}$). The learned policies will be evaluated via a round-robin tournament against each other and the following baselines:
*   **Independent PPO (IPPO):** A baseline lacking a centralized critic, testing the necessity of CTDE.
*   **Heuristic Greedy Agents:** Hardcoded bots simulating $\lambda=0$ (always chasing the ball and shooting).

This round-robin tournament will be simulated multiple times. Each algorithm gathers a cumulative score, which increments by one if it wins a match. While the primary objective evaluation criteria is the match score, we will also track emergent secondary metrics including passes per game, interception rate, and overall ball possession time. Tracking these metrics provides vital insight into how the mathematical parameters manifest into real-world playstyles.

### 4.3 Successful Outcome and Anticipated Challenges

A successful outcome will be the generation of a clear Pareto frontier illustrating the trade-off between individual hoarding and emergent passing based on $\lambda$. We aim to empirically prove that pure egoism ($\lambda=0$) results in sub-optimal win rates due to physical dribble penalties, while a higher cooperation coefficient bypasses physical constraints and yields a mathematically superior team win rate and dynamic playstyle.

We anticipate several limitations and challenges. The primary challenge is reward sparsity; goals in 3D physics environments are rare early in training. We will likely need to employ intrinsic reward shaping (e.g., small dense rewards for simply moving toward the ball) before the sparse $\lambda$ parameter can effectively guide complex team strategies. Furthermore, the inherent noise of Unity's continuous collision physics may complicate the critic's ability to assign accurate credit for long-distance aerial passes.

---

## 5 Contribution

The project tasks are distributed as follows to ensure equitable workload and leverage individual expertise:
*   **Divyansh Sharma:** Responsible for the development of the 3D continuous-space environment using Unity. Tasks include configuring the rigid-body physics for parabolic trajectories, implementing the localized velocity-decay constraints (dribble penalties), setting up the 3D Ray Perception Sensors for partial observability, and tuning the simulation step rates for MARL training.
*   **Ankit Saha:** Responsible for the algorithmic implementation and evaluation. Tasks include integrating the PPO-based CTDE architecture via the ML-Agents toolkit, formulating the Python-side parameterized reward logic, tracking tensorboard metrics, and conducting the hyperparameter sweeps over the $\lambda$ parameter.

### Acknowledgments
This project is formulated as part of the Foundations of AI: Multi-Agent Systems curriculum under the guidance of Dr. Manisha Padala.

## References
[1] Leibo, Joel Z., et al. "Multi-agent reinforcement learning in sequential social dilemmas." Proceedings of the 16th Conference on Autonomous Agents and MultiAgent Systems. 2017.

[2] Hughes, Edward, et al. "Inequity aversion improves cooperation in intertemporal social dilemmas." Advances in Neural Information Processing Systems 31. 2018.

[3] McKee, Kevin R., et al. "Social diversity and social preferences in mixed-motive reinforcement learning." Proceedings of the 19th International Conference on Autonomous Agents and MultiAgent Systems. 2020.

[4] Juliani, Arthur, et al. "Unity: A general platform for intelligent agents." arXiv preprint arXiv: 1809.02627. 2018.

[5] Yu, Chao, et al. "The surprising effectiveness of PPO in cooperative multi-agent games." Advances in Neural Information Processing Systems 35 (2022): 24611-24624.

[6] Oliehoek, Frans A., and Christopher Amato. A concise introduction to decentralized POMDPs. Springer, 2016.

[7] "Norway 1-2 England (a.e.t.), FIFA World Cup quarter-final." ESPN, 11 July 2026. https://www.espn.com/soccer/match/_/gameId/760512/england-norway

[8] "Alexander Sorloth explains why he didn't pass to Erling Haaland when Norway had golden chance to establish two-goal lead vs England in World Cup quarter-final tie." Goal.com, 12 July 2026. https://www.goal.com/en/lists/alexander-sorloth-explains-why-didn-t-pass-to-erling-haaland-norway-golden-chance-establish-two-goa/blt5b4cd1fb7c3c185a