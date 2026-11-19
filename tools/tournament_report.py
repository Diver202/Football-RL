"""
Builds the tournament report from the per-match logs.

Input:  tournament/raw/<TeamA>_vs_<TeamB>.csv   (one row per match, written by the game in -tournament mode)
Output: tournament/matches.csv   every match of every pairing in one table (adds a `pairing` column)
        tournament/goals.csv     every goal: pairing, match, minute, scoring team, side
        tournament/summary.json  all numbers below, machine readable
        tournament/RESULTS.md    the structured results document for the report

Usage (repo root):  .venv/Scripts/python.exe tools/tournament_report.py
"""
import csv
import glob
import json
import math
import os
from collections import defaultdict
from datetime import date

RAW = "tournament/raw"
OUT = "tournament"
FORMATIONS = ["Wide", "Attack", "Balanced", "Defend", "Press"]
TEAM_ORDER = ["NoPass", "HalfPass", "AllPass", "TestBrain Bots"]
TEAM_INFO = {
    "NoPass": "trained, lambda = 0 (individual reward), learned coach",
    "HalfPass": "trained, lambda = 0.5 (shared reward), learned coach",
    "AllPass": "trained, lambda = 1 (team reward), learned coach",
    "TestBrain Bots": "hand-coded heuristic bots, no learning, no coach",
}


def wilson(k, n, z=1.96):
    """95% Wilson score interval for a proportion k/n."""
    if n == 0:
        return (float("nan"), float("nan"))
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def binom_two_sided(k, n):
    """Exact two-sided binomial test p-value for k successes in n trials, p = 0.5 (sign test)."""
    if n == 0:
        return float("nan")
    probs = [math.comb(n, i) / 2 ** n for i in range(n + 1)]
    pk = probs[k]
    return min(1.0, sum(p for p in probs if p <= pk + 1e-12))


def mean(xs):
    return sum(xs) / len(xs) if xs else float("nan")


def sd(xs):
    if len(xs) < 2:
        return float("nan")
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def team_view(row, team):
    """The match from `team`'s point of view (it may have played Red or Blue)."""
    side = "red" if row["red_team"] == team else "blue"
    opp = "blue" if side == "red" else "red"
    g, ga = int(row[f"{side}_goals"]), int(row[f"{opp}_goals"])
    return {
        "side": side,
        "opponent": row[f"{opp}_team"],
        "gf": g, "ga": ga,
        "result": "W" if g > ga else "L" if g < ga else "D",
        "possession": float(row[f"{side}_possession_pct"]),
        "shots": int(row[f"{side}_shots"]),
        "shots_against": int(row[f"{opp}_shots"]),
        "passes": int(row[f"{side}_passes"]),
        "interceptions": int(row[f"{side}_interceptions"]),
        "tackles": int(row[f"{side}_tackles"]),
        "territory": float(row[f"{side}_territory_pct"]),
        "distance": float(row[f"{side}_distance_m"]),
        "formation": row[f"{side}_main_formation"],
        "shares": row[f"{side}_formation_shares"],
    }


def main():
    files = sorted(glob.glob(os.path.join(RAW, "*.csv")))
    if not files:
        raise SystemExit(f"No match logs in {RAW}/")
    matches = []
    for f in files:
        pairing = os.path.splitext(os.path.basename(f))[0]
        with open(f, newline="", encoding="utf-8") as fh:
            for row in csv.DictReader(fh):
                row["pairing"] = pairing
                matches.append(row)

    # ---- combined match log + goal log
    fields = ["pairing"] + [k for k in matches[0].keys() if k != "pairing"]
    with open(os.path.join(OUT, "matches.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=fields)
        w.writeheader()
        w.writerows(matches)
    goal_rows = []
    for r in matches:
        for g in filter(None, r["goals"].split(";")):
            side = "red" if g[0] == "R" else "blue"
            t = float(g[1:])
            goal_rows.append({"pairing": r["pairing"], "match": r["match"], "second": t,
                              "minute": min(5, int(t // 60) + 1), "side": side, "team": r[f"{side}_team"]})
    with open(os.path.join(OUT, "goals.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["pairing", "match", "second", "minute", "side", "team"])
        w.writeheader()
        w.writerows(goal_rows)

    teams = [t for t in TEAM_ORDER if any(r["red_team"] == t or r["blue_team"] == t for r in matches)]
    teams += sorted(({r["red_team"] for r in matches} | {r["blue_team"] for r in matches}) - set(teams))

    # ---- standings
    standings = {}
    per_team = defaultdict(list)
    for r in matches:
        for t in (r["red_team"], r["blue_team"]):
            per_team[t].append(team_view(r, t))
    for t in teams:
        v = per_team[t]
        W = sum(x["result"] == "W" for x in v); D = sum(x["result"] == "D" for x in v); L = sum(x["result"] == "L" for x in v)
        GF = sum(x["gf"] for x in v); GA = sum(x["ga"] for x in v)
        standings[t] = {"played": len(v), "won": W, "drawn": D, "lost": L, "goals_for": GF, "goals_against": GA,
                        "goal_difference": GF - GA, "points": 3 * W + D, "points_per_match": (3 * W + D) / len(v),
                        "win_rate": W / len(v)}
    table = sorted(teams, key=lambda t: (-standings[t]["points"], -standings[t]["goal_difference"], -standings[t]["goals_for"]))

    # ---- head to head
    pairs = defaultdict(list)
    for r in matches:
        pairs[r["pairing"]].append(r)
    h2h = {}
    for name, rows in sorted(pairs.items()):
        a, b = name.split("_vs_")
        a = next(t for t in teams if t.replace(" ", "") == a or t == a)
        b = next(t for t in teams if t.replace(" ", "") == b or t == b)
        va = [team_view(r, a) for r in rows]
        wa = sum(x["result"] == "W" for x in va); d = sum(x["result"] == "D" for x in va); wb = sum(x["result"] == "L" for x in va)
        n = len(va)
        ci = wilson(wa, n)
        decided = wa + wb
        h2h[name] = {
            "team_a": a, "team_b": b, "matches": n, "a_wins": wa, "draws": d, "b_wins": wb,
            "a_win_rate": wa / n, "a_win_rate_ci95": ci,
            "b_win_rate": wb / n, "b_win_rate_ci95": wilson(wb, n),
            "a_goals_per_match": mean([x["gf"] for x in va]), "b_goals_per_match": mean([x["ga"] for x in va]),
            "a_goals_sd": sd([x["gf"] for x in va]), "b_goals_sd": sd([x["ga"] for x in va]),
            "sign_test_p": binom_two_sided(min(wa, wb), decided) if decided else float("nan"),
            "a_as_red": {"matches": sum(x["side"] == "red" for x in va),
                         "a_wins": sum(x["side"] == "red" and x["result"] == "W" for x in va)},
            "a_as_blue": {"matches": sum(x["side"] == "blue" for x in va),
                          "a_wins": sum(x["side"] == "blue" and x["result"] == "W" for x in va)},
            "stats": {},
        }
        for key in ["possession", "shots", "passes", "interceptions", "tackles", "territory", "distance"]:
            vb = [team_view(r, b) for r in rows]
            h2h[name]["stats"][key] = {"a": mean([x[key] for x in va]), "b": mean([x[key] for x in vb])}
        for team, views in ((a, va), (b, [team_view(r, b) for r in rows])):
            forms = defaultdict(int)
            for x in views:
                forms[x["formation"]] += 1
            h2h[name].setdefault("main_formation_counts", {})[team] = dict(forms)

    # ---- per-team averages
    averages = {}
    for t in teams:
        v = per_team[t]
        averages[t] = {k: mean([x[k] for x in v]) for k in
                       ["gf", "ga", "possession", "shots", "shots_against", "passes", "interceptions", "tackles", "territory", "distance"]}
        shares = defaultdict(list)
        for x in v:
            for part in filter(None, x["shares"].split(";")):
                f, s = part.split("=")
                shares[f].append(float(s))
        averages[t]["formation_share"] = {f: mean(shares[f]) for f in FORMATIONS if shares[f]}

    # ---- side check, goal timing
    red_w = sum(r["result"] == "red" for r in matches); blue_w = sum(r["result"] == "blue" for r in matches)
    draws = sum(r["result"] == "draw" for r in matches)
    minutes = {t: [0] * 5 for t in teams}
    for g in goal_rows:
        minutes[g["team"]][g["minute"] - 1] += 1
    real = [float(r["real_seconds"]) for r in matches]
    # Consistency: every match's score must equal its goal-by-goal log.
    inconsistent = 0
    for r in matches:
        g = [x for x in r["goals"].split(";") if x]
        if sum(x[0] == "R" for x in g) != int(r["red_goals"]) or sum(x[0] == "B" for x in g) != int(r["blue_goals"]):
            inconsistent += 1

    summary = {"generated": str(date.today()), "matches": len(matches), "standings": standings, "table_order": table,
               "head_to_head": h2h, "team_averages": averages,
               "side_check": {"red_wins": red_w, "blue_wins": blue_w, "draws": draws,
                              "sign_test_p": binom_two_sided(min(red_w, blue_w), red_w + blue_w)},
               "goals_by_minute": minutes}
    with open(os.path.join(OUT, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(summary, fh, indent=2)

    # ---- document
    P = lambda x: f"{100 * x:.0f}%"
    L = []
    L.append("# Tournament results: NoPass vs HalfPass vs AllPass vs TestBrain Bots\n")
    L.append(f"Generated {date.today()} from `tournament/matches.csv` ({len(matches)} matches) by `tools/tournament_report.py`.\n")
    L.append("## Setup\n")
    L.append("| Team | What it is |\n|---|---|")
    for t in teams:
        L.append(f"| {t} | {TEAM_INFO.get(t, '')} |")
    L.append("")
    L.append("- **Format:** every pair of teams (4C2 = 6 pairings) played **100 matches**: "
             f"{len(matches)} matches in total.")
    L.append("- **Match:** 5 minutes of play, 3 v 3, kick-off after every goal, the same game, rules and physics as "
             "the playable build (`Assets/Scenes/FinalBuild.unity`), run headless and faster than real time.")
    L.append("  The physics step is fixed at 0.02 s, so running faster does not change the simulation.")
    L.append("- **Sides:** the teams swapped sides every match (50 as Red, 50 as Blue), so any side advantage cancels out.")
    L.append("- **Models:** the trained teams use their final models (`Assets/Models/3v3/1_Final_NoPress`: players + "
             "learned coach, Press formation disabled). Actions are sampled from the policies (stochastic), so no "
             "two matches are the same.")
    L.append("- **Points:** 3 for a win, 1 for a draw. Win-rate intervals are 95% Wilson intervals; the p-value is "
             "an exact two-sided sign test on decided matches (wins vs losses, draws excluded).\n")

    L.append("## Standings\n")
    L.append("| Pos | Team | P | W | D | L | GF | GA | GD | Pts | Pts / match | Win rate |")
    L.append("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for i, t in enumerate(table, 1):
        s = standings[t]
        L.append(f"| {i} | {t} | {s['played']} | {s['won']} | {s['drawn']} | {s['lost']} | {s['goals_for']} | "
                 f"{s['goals_against']} | {s['goal_difference']:+d} | {s['points']} | {s['points_per_match']:.2f} | {P(s['win_rate'])} |")
    L.append("")

    L.append("## Head-to-head\n")
    L.append("| Pairing (A vs B) | A wins | Draws | B wins | A win rate (95% CI) | B win rate (95% CI) | "
             "Avg score A-B | Sign test p |")
    L.append("|---|---|---|---|---|---|---|---|")
    for name, h in h2h.items():
        ca, cb = h["a_win_rate_ci95"], h["b_win_rate_ci95"]
        L.append(f"| {h['team_a']} vs {h['team_b']} | {h['a_wins']} | {h['draws']} | {h['b_wins']} | "
                 f"{P(h['a_win_rate'])} ({P(ca[0])}-{P(ca[1])}) | {P(h['b_win_rate'])} ({P(cb[0])}-{P(cb[1])}) | "
                 f"{h['a_goals_per_match']:.2f} - {h['b_goals_per_match']:.2f} | {h['sign_test_p']:.3g} |")
    L.append("")

    L.append("### Win-rate matrix (row team's win rate against the column team)\n")
    L.append("| | " + " | ".join(teams) + " |")
    L.append("|---|" + "---|" * len(teams))
    for ra in teams:
        cells = []
        for cb in teams:
            if ra == cb:
                cells.append("-")
                continue
            h = next((h for h in h2h.values() if {h["team_a"], h["team_b"]} == {ra, cb}), None)
            if h is None:
                cells.append("")
            else:
                cells.append(P(h["a_win_rate"] if h["team_a"] == ra else h["b_win_rate"]))
        L.append(f"| **{ra}** | " + " | ".join(cells) + " |")
    L.append("")

    L.append("## Match statistics per team (average per match, over all its matches)\n")
    L.append("| Team | Goals for | Goals against | Possession | Shots | Shots against | Passes | Interceptions | "
             "Tackles | Ball in their half | Distance (km) |")
    L.append("|---|---|---|---|---|---|---|---|---|---|---|")
    for t in table:
        a = averages[t]
        L.append(f"| {t} | {a['gf']:.2f} | {a['ga']:.2f} | {a['possession']:.1f}% | {a['shots']:.2f} | {a['shots_against']:.2f} | "
                 f"{a['passes']:.2f} | {a['interceptions']:.2f} | {a['tackles']:.2f} | {a['territory']:.1f}% | {a['distance'] / 1000:.2f} |")
    L.append("")
    L.append("Pass = a teammate gets the ball after it was kicked or left loose. Interception = the opponent gets a "
             "kicked or loose ball. Tackle = the ball is taken straight off the carrier. Shot = a power kick aimed "
             "at the goal mouth from within 45 m. Distance = all three players together.\n")

    L.append("## Statistics per pairing (average per match)\n")
    L.append("| Pairing (A vs B) | Possession A / B | Shots A / B | Passes A / B | Interceptions A / B | "
             "Tackles A / B | Ball in their half A / B |")
    L.append("|---|---|---|---|---|---|---|")
    for name, h in h2h.items():
        s = h["stats"]
        L.append(f"| {h['team_a']} vs {h['team_b']} | {s['possession']['a']:.0f}% / {s['possession']['b']:.0f}% | "
                 f"{s['shots']['a']:.2f} / {s['shots']['b']:.2f} | {s['passes']['a']:.2f} / {s['passes']['b']:.2f} | "
                 f"{s['interceptions']['a']:.2f} / {s['interceptions']['b']:.2f} | {s['tackles']['a']:.2f} / {s['tackles']['b']:.2f} | "
                 f"{s['territory']['a']:.0f}% / {s['territory']['b']:.0f}% |")
    L.append("")

    coached = [t for t in teams if averages[t].get("formation_share")]
    if coached:
        L.append("## Coach formations (share of match time, averaged over all matches)\n")
        L.append("| Team | " + " | ".join(FORMATIONS) + " |")
        L.append("|---|" + "---|" * len(FORMATIONS))
        for t in coached:
            fs = averages[t]["formation_share"]
            L.append(f"| {t} | " + " | ".join(P(fs.get(f, 0.0)) for f in FORMATIONS) + " |")
        L.append("\nPress is disabled for these coach models, so it is always 0%.\n")

    L.append("## Goals by minute\n")
    L.append("| Team | 0-1 | 1-2 | 2-3 | 3-4 | 4-5 | Total |")
    L.append("|---|---|---|---|---|---|---|")
    for t in table:
        m = minutes[t]
        L.append(f"| {t} | " + " | ".join(str(x) for x in m) + f" | {sum(m)} |")
    L.append("")

    L.append("## Checks\n")
    L.append(f"- **Side bias:** Red won {red_w}, Blue won {blue_w}, {draws} draws "
             f"(sign test p = {summary['side_check']['sign_test_p']:.3g}; sides alternate every match, so any bias "
             "cancels out of the head-to-head results anyway).")
    L.append(f"- **Score vs goal log:** {len(matches) - inconsistent} of {len(matches)} matches agree exactly "
             "(final score = goals logged during the 5 minutes).")
    L.append(f"- **Run:** {len(matches)} matches; real time per 5-minute match {mean(real):.1f} s on average "
             "(headless, sped up).")
    L.append("- **Files:** `tournament/matches.csv` (one row per match, every statistic), `tournament/goals.csv` "
             "(one row per goal, with the time), `tournament/summary.json` (all numbers above), `tournament/raw/` "
             "(the logs as written by the game).\n")
    L.append("## Notes for the report\n")
    L.append("- **Timing fix before this tournament:** the players' dribble cooldown, kick cooldown and possession "
             "timeout used to be counted per rendered frame instead of per physics step, which made the game depend "
             "on the frame rate. They now tick on the 0.02 s physics step, so a match plays the same at any speed "
             "(checked: 20x and 200x give the same results) and the same as real-time play.")
    L.append("- **Training ran with the old timers** (at 20x speed), so the trained agents learned with slightly "
             "coarser dribble and possession timing than the game now has.")
    L.append("- **One seed per team:** each team is one training run; differences between teams combine the effect "
             "of lambda with run-to-run variation, which this tournament cannot separate.\n")
    with open(os.path.join(OUT, "RESULTS.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(L))
    print("\n".join(L[:40]))


if __name__ == "__main__":
    main()
