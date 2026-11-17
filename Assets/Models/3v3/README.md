# 3v3 models

Every folder has one set per team (NoPass λ=0, HalfPass λ=0.5, AllPass λ=1). Use a team's models in that team's scene (`Assets/Scenes/Team<Team>.unity`).

| Folder | What it is | Inputs | Coach |
|---|---|---|---|
| **1_Final_NoPress** | **Recommended.** Final models; the coach can't choose Press, so players stay spread out (9–11.5 m apart). | 49 | yes |
| **2_Final_WithPress** | Final models; the coach may choose Press, and mostly does, so play is compact (4.5–5.6 m) with more passes. | 49 | yes |
| 3_Coach3_Phase2 | The starting point for both finals: the clean phase 2. | 49 | yes |
| 4_Older/A_3M_NoCoach | The 3M self-play models, before the coach. They huddle. | **46** | no |
| 4_Older/B_Spaced_Failed | The first spacing attempt (it failed). | **46** | no |
| 4_Older/C_Roles_Phase1 | Phase 1: players trained with a random (scripted) coach. | 49 | scripted |
| 4_Older/D_CoachV2 | Coach v2, trained with the role rewards that turned out to be farmable. | 49 | yes |

## Watching a coached match

1. Select the 6 players. In **Behavior Parameters**, set **Model** to `<Team>_..._players.onnx` and leave **Behavior Type** on **Default**.
2. Select **GameManager**. In `MatchController`:
   - **Coach Mode** = **Learned**;
   - **Red Coach Model** and **Blue Coach Model** = `<Team>_..._coordinator.onnx`;
   - **Move Gain** = **2**;
   - **Coach Allow Press**: unticked for `1_Final_NoPress`, ticked for `2_Final_WithPress`.
3. Press Play. No trainer is needed.

For `C_Roles_Phase1`, set Coach Mode to **Scripted** and leave the coach models empty. The 46-input models need Space Size **46** on the players and Coach Mode **Off**.
