# Tournament results: NoPass vs HalfPass vs AllPass vs TestBrain Bots

Generated 2026-11-19 from `tournament/matches.csv` (600 matches) by `tools/tournament_report.py`.

## Setup

| Team | What it is |
|---|---|
| NoPass | trained, lambda = 0 (individual reward), learned coach |
| HalfPass | trained, lambda = 0.5 (shared reward), learned coach |
| AllPass | trained, lambda = 1 (team reward), learned coach |
| TestBrain Bots | hand-coded heuristic bots, no learning, no coach |

- **Format:** every pair of teams (4C2 = 6 pairings) played **100 matches**: 600 matches in total.
- **Match:** 5 minutes of play, 3 v 3, kick-off after every goal, the same game, rules and physics as the playable build (`Assets/Scenes/FinalBuild.unity`), run headless and faster than real time.
  The physics step is fixed at 0.02 s, so running faster does not change the simulation.
- **Sides:** the teams swapped sides every match (50 as Red, 50 as Blue), so any side advantage cancels out.
- **Models:** the trained teams use their final models (`Assets/Models/3v3/1_Final_NoPress`: players + learned coach, Press formation disabled). Actions are sampled from the policies (stochastic), so no two matches are the same.
- **Points:** 3 for a win, 1 for a draw. Win-rate intervals are 95% Wilson intervals; the p-value is an exact two-sided sign test on decided matches (wins vs losses, draws excluded).

## Standings

| Pos | Team | P | W | D | L | GF | GA | GD | Pts | Pts / match | Win rate |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | NoPass | 300 | 260 | 35 | 5 | 984 | 36 | +948 | 815 | 2.72 | 87% |
| 2 | HalfPass | 300 | 125 | 84 | 91 | 241 | 207 | +34 | 459 | 1.53 | 42% |
| 3 | TestBrain Bots | 300 | 106 | 28 | 166 | 567 | 665 | -98 | 346 | 1.15 | 35% |
| 4 | AllPass | 300 | 12 | 47 | 241 | 68 | 952 | -884 | 83 | 0.28 | 4% |

## Head-to-head

| Pairing (A vs B) | A wins | Draws | B wins | A win rate (95% CI) | B win rate (95% CI) | Avg score A-B | Sign test p |
|---|---|---|---|---|---|---|---|
| AllPass vs TestBrain Bots | 0 | 0 | 100 | 0% (0%-4%) | 100% (96%-100%) | 0.19 - 5.50 | 4.83e-13 |
| HalfPass vs AllPass | 55 | 34 | 11 | 55% (45%-64%) | 11% (6%-19%) | 1.06 - 0.35 | 3.6e-08 |
| HalfPass vs TestBrain Bots | 66 | 28 | 6 | 66% (56%-75%) | 6% (3%-12%) | 1.19 - 0.11 | 6.96e-13 |
| NoPass vs AllPass | 86 | 13 | 1 | 86% (78%-91%) | 1% (0%-5%) | 2.96 - 0.14 | 4.21e-13 |
| NoPass vs HalfPass | 74 | 22 | 4 | 74% (65%-82%) | 4% (2%-10%) | 1.61 - 0.16 | 1.38e-12 |
| NoPass vs TestBrain Bots | 100 | 0 | 0 | 100% (96%-100%) | 0% (0%-4%) | 5.27 - 0.06 | 4.83e-13 |

### Win-rate matrix (row team's win rate against the column team)

| | NoPass | HalfPass | AllPass | TestBrain Bots |
|---|---|---|---|---|
| **NoPass** | - | 74% | 86% | 100% |
| **HalfPass** | 4% | - | 55% | 66% |
| **AllPass** | 1% | 11% | - | 0% |
| **TestBrain Bots** | 0% | 6% | 100% | - |

## Match statistics per team (average per match, over all its matches)

| Team | Goals for | Goals against | Possession | Shots | Shots against | Passes | Interceptions | Tackles | Ball in their half | Distance (km) |
|---|---|---|---|---|---|---|---|---|---|---|
| NoPass | 3.28 | 0.12 | 34.8% | 19.52 | 3.31 | 3.67 | 9.75 | 18.42 | 63.8% | 5.77 |
| HalfPass | 0.80 | 0.69 | 39.1% | 6.81 | 4.86 | 6.80 | 9.38 | 17.74 | 61.1% | 6.17 |
| TestBrain Bots | 1.89 | 2.22 | 96.2% | 4.83 | 16.18 | 3.06 | 30.58 | 10.07 | 34.4% | 4.96 |
| AllPass | 0.23 | 3.17 | 30.0% | 4.23 | 11.06 | 1.88 | 9.07 | 4.20 | 40.7% | 5.66 |

Pass = a teammate gets the ball after it was kicked or left loose. Interception = the opponent gets a kicked or loose ball. Tackle = the ball is taken straight off the carrier. Shot = a power kick aimed at the goal mouth from within 45 m. Distance = all three players together.

## Statistics per pairing (average per match)

| Pairing (A vs B) | Possession A / B | Shots A / B | Passes A / B | Interceptions A / B | Tackles A / B | Ball in their half A / B |
|---|---|---|---|---|---|---|
| AllPass vs TestBrain Bots | 1% / 99% | 3.14 / 13.24 | 0.14 / 3.65 | 3.31 / 12.02 | 9.97 / 0.92 | 35% / 65% |
| HalfPass vs AllPass | 58% / 42% | 6.64 / 4.13 | 9.16 / 2.72 | 13.31 / 12.57 | 0.88 / 1.18 | 60% / 40% |
| HalfPass vs TestBrain Bots | 5% / 95% | 10.23 / 0.33 | 3.25 / 3.22 | 4.38 / 42.10 | 51.48 / 12.52 | 76% / 24% |
| NoPass vs AllPass | 52% / 48% | 13.29 / 5.43 | 4.65 / 2.77 | 13.19 / 11.32 | 0.79 / 1.45 | 53% / 47% |
| NoPass vs HalfPass | 46% / 54% | 10.11 / 3.56 | 4.43 / 7.98 | 11.05 / 10.44 | 0.84 / 0.85 | 52% / 48% |
| NoPass vs TestBrain Bots | 6% / 94% | 35.16 / 0.93 | 1.94 / 2.30 | 5.00 / 37.63 | 53.63 / 16.76 | 86% / 14% |

## Coach formations (share of match time, averaged over all matches)

| Team | Wide | Attack | Balanced | Defend | Press |
|---|---|---|---|---|---|
| NoPass | 7% | 3% | 84% | 6% | 0% |
| HalfPass | 24% | 29% | 24% | 23% | 0% |
| AllPass | 10% | 8% | 17% | 64% | 0% |

Press is disabled for these coach models, so it is always 0%.

## Goals by minute

| Team | 0-1 | 1-2 | 2-3 | 3-4 | 4-5 | Total |
|---|---|---|---|---|---|---|
| NoPass | 200 | 208 | 178 | 188 | 210 | 984 |
| HalfPass | 57 | 43 | 48 | 50 | 43 | 241 |
| TestBrain Bots | 90 | 109 | 128 | 122 | 118 | 567 |
| AllPass | 11 | 14 | 19 | 8 | 16 | 68 |

## Checks

- **Side bias:** Red won 253, Blue won 250, 97 draws (sign test p = 0.929; sides alternate every match, so any bias cancels out of the head-to-head results anyway).
- **Score vs goal log:** 600 of 600 matches agree exactly (final score = goals logged during the 5 minutes).
- **Run:** 600 matches; real time per 5-minute match 4.2 s on average (headless, sped up).
- **Files:** `tournament/matches.csv` (one row per match, every statistic), `tournament/goals.csv` (one row per goal, with the time), `tournament/summary.json` (all numbers above), `tournament/raw/` (the logs as written by the game).

## Notes for the report

- **Timing fix before this tournament:** the players' dribble cooldown, kick cooldown and possession timeout used to be counted per rendered frame instead of per physics step, which made the game depend on the frame rate. They now tick on the 0.02 s physics step, so a match plays the same at any speed (checked: 20x and 200x give the same results) and the same as real-time play.
- **Training ran with the old timers** (at 20x speed), so the trained agents learned with slightly coarser dribble and possession timing than the game now has.
- **One seed per team:** each team is one training run; differences between teams combine the effect of lambda with run-to-run variation, which this tournament cannot separate.
