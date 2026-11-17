using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.Sentis;

/// <summary>
/// Referee for one match between RL agents (independent learners, PPO + self-play).
/// This is the ONE place a match is set up:
///   - Red Team / Blue Team lists decide which side each player is on (up to 3 per side),
///   - Red Goal / Blue Goal and Ball are assigned once here and handed to every player,
///   - all reward settings live here.
///
/// It does not create, move or reconfigure objects in the editor. At runtime it:
///   - resets players (to where they were placed in the scene) and the ball at kick-off,
///   - ends every agent's episode on a goal or when time runs out,
///   - gives the goal rewards: R_i = (1 - lambda) * r_ind + lambda * R_team, and the concede penalty,
///   - records match statistics (passes, interceptions, snatches...) to TensorBoard under "Match/".
/// All rewards are plain per-agent rewards (Agent.AddReward), because PPO ignores group rewards.
///
/// lambda, shaping_scale, spacing_weight and spacing_distance are overridden by the trainer YAML
/// (environment_parameters) during training.
/// </summary>
public class MatchController : MonoBehaviour
{
    [Header("Players")]
    [Tooltip("Agents on Team Red. Their Behavior Parameters > Team Id must be 0.")]
    public List<FootballAgentML> redTeam = new List<FootballAgentML>();
    [Tooltip("Agents on Team Blue. Their Behavior Parameters > Team Id must be 1.")]
    public List<FootballAgentML> blueTeam = new List<FootballAgentML>();

    [Header("Pitch")]
    [Tooltip("The goal Team Red defends (GoalPostRed).")]
    public Transform redGoal;
    [Tooltip("The goal Team Blue defends (GoalPostBlue).")]
    public Transform blueGoal;
    public Transform ball;
    [Tooltip("Ball position at kick-off. (0, 0.15, 0) matches the earlier runs.")]
    public Vector3 ballSpawn = new Vector3(0f, 0.15f, 0f);

    [Header("Goal Rewards")]
    [Tooltip("Cooperation coefficient. Team NoPass = 0, HalfPass = 0.5, AllPass = 1. " +
             "During training the value in the trainer YAML is used.")]
    [Range(0f, 1f)] public float lambda = 0f;
    public float goalReward = 10f;
    [Tooltip("Extra reward for scoring early, scaled by the fraction of time remaining.")]
    public float speedBonus = 5f;
    [Tooltip("Given to every player of the team that concedes.")]
    public float concedePenalty = -5f;

    [Header("Dense Shaping (per player, same weights as the earlier runs)")]
    [Tooltip("Multiplies all shaping. The 3v3 YAML fades it 1 -> 0.5 -> 0 over training.")]
    [Min(0f)] public float shapingScale = 1f;
    public float approachBallWeight = 0.02f;
    public float advanceBallWeight = 0.04f;
    public float firstPossessionBonus = 1.0f;
    public float shotTowardGoalBonus = 0.3f;
    public float shotRange = 35f;

    [Header("Team Shape (spacing)")]
    [Tooltip("Penalty for crowding a teammate. A player standing right on top of a teammate for a whole " +
             "match loses this much; it shrinks to 0 at Spacing Distance. The ball carrier is exempt. " +
             "0 = off (the first 3M-step runs). The trainer YAML sets it (spacing_weight).")]
    [Min(0f)] public float spacingWeight = 0f;
    [Tooltip("Teammates closer than this (metres) count as crowding. The trainer YAML sets it (spacing_distance).")]
    [Min(0.1f)] public float spacingDistance = 10f;
    [Tooltip("Old rule, kept only to reproduce the first '_spaced' runs: also exempt the teammate closest " +
             "to the ball. It failed: players escaped the penalty by racing to be closest. " +
             "The trainer YAML sets it (spacing_exempt_closest: 1 = on).")]
    public bool spacingExemptClosest = false;

    [Header("Coach (hierarchical: a coach per team gives each player a role)")]
    [Tooltip("Off = no coach (players see zeros, as before). Scripted = random formations (phase 1). " +
             "Learned = a Coordinator agent per team picks the formation (phase 2). " +
             "Training builds take it from the command line: -coach off|scripted|learned.")]
    public CoachMode coachMode = CoachMode.Off;
    [Tooltip("Physics steps between role re-assignments (who chases, who takes which slot). 25 = 0.5 s.")]
    [Min(1)] public int coachPeriodSteps = 25;
    [Tooltip("Formation decisions: at kick-off, when possession switches between the teams, and otherwise " +
             "every this many seconds (coach_decision_period). The formation is held in between.")]
    [Min(0.5f)] public float coachDecisionPeriod = 3f;
    [Tooltip("Never two formation decisions closer than this (seconds, coach_min_gap).")]
    [Min(0.02f)] public float coachMinGap = 1f;
    [Tooltip("Coach reward per metre the ball moves towards the opponent goal (coach_territory_weight). " +
             "Zero-sum between the two coaches; the full length of the pitch is worth about a goal at 0.1.")]
    [Min(0f)] public float coachTerritoryWeight = 0.1f;
    [Tooltip("Whether the coach may choose the Press formation (coach_allow_press, 1/0). Without it every " +
             "formation keeps the supporting players away from the ball. Done with action masking, so the " +
             "Coordinator network keeps its 5 outputs.")]
    public bool coachAllowPress = true;
    [Tooltip("Every player earns this much over a whole match spent exactly at its target: the ball for the " +
             "chaser, its slot for the others (less further away, 0 beyond Role Radius). The trainer YAML sets " +
             "it (role_hold_weight).")]
    [Min(0f)] public float roleHoldWeight = 0f;
    [Tooltip("Reward per metre a supporting player gets closer to its slot (role_progress_weight).")]
    [Min(0f)] public float roleProgressWeight = 0f;
    [Tooltip("Within this distance (m) of its slot a player counts as in position (role_radius).")]
    [Min(0.1f)] public float roleRadius = 5f;
    [Tooltip("Multiplies the movement action before it is capped at full speed (move_gain). The coach models " +
             "were made with movement outputs halved, so they use 2: same movement, but no longer stuck at the clip.")]
    [Min(0.1f)] public float moveGain = 2f;
    [Tooltip("Coach models for watching a Learned-coach match without a trainer (Coordinator .onnx).")]
    public ModelAsset redCoachModel;
    public ModelAsset blueCoachModel;
    [Tooltip("Slots are kept inside |x| <= this.")]
    public float pitchHalfLength = 50f;
    [Tooltip("Slots are kept inside |z| <= this.")]
    public float pitchHalfWidth = 33f;

    [Header("Episode")]
    [Tooltip("Physics steps before the match is called a draw (50 steps = 1 second). 3000 = 60 s.")]
    public int maxEnvironmentSteps = 3000;

    public int StepCount { get; private set; }
    public float ShapingScale => shapingScale;

    private Rigidbody ballRb;
    private float sceneLambda;

    // Match statistics (reset every kick-off)
    private readonly Dictionary<FootballAgent, int> teamOfBody = new Dictionary<FootballAgent, int>();
    private FootballAgent lastCarrier;
    private bool ballLooseSinceLastCarrier;
    private int passes, interceptions, snatches, teammateSteals;
    private float teammateDistanceSum;
    private int teammateDistanceSamples, crowdedSamples;

    // Coach (null when coachMode is Off)
    private TeamCoach redCoach, blueCoach;
    private TeamCoachAgent redCoachAgent, blueCoachAgent;
    private readonly Dictionary<FootballAgentML, TeamCoach> coachOf = new Dictionary<FootballAgentML, TeamCoach>();

    private void Awake()
    {
        sceneLambda = lambda;
        if (ball != null) ballRb = ball.GetComponent<Rigidbody>();
        foreach (var a in redTeam) if (a != null) a.JoinMatch(this, blueGoal, redGoal, ball, redTeam, blueTeam);
        foreach (var a in blueTeam) if (a != null) a.JoinMatch(this, redGoal, blueGoal, ball, blueTeam, redTeam);
        foreach (var a in redTeam) if (a != null && a.Body != null) teamOfBody[a.Body] = 0;
        foreach (var a in blueTeam) if (a != null && a.Body != null) teamOfBody[a.Body] = 1;
        SetUpCoaches();
    }

    // ---------------- Coach ----------------

    /// <summary>"-coach off|scripted|learned" on the command line (training builds) overrides the Inspector.</summary>
    private static CoachMode? CoachModeFromCommandLine()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "-coach") continue;
            switch (args[i + 1].ToLowerInvariant())
            {
                case "off": return CoachMode.Off;
                case "scripted": return CoachMode.Scripted;
                case "learned": return CoachMode.Learned;
                default: Debug.LogError($"MatchController: unknown -coach value '{args[i + 1]}'"); return null;
            }
        }
        return null;
    }

    private void SetUpCoaches()
    {
        CoachMode? fromArgs = CoachModeFromCommandLine();
        if (fromArgs.HasValue) coachMode = fromArgs.Value;
        if (coachMode == CoachMode.Off || ball == null || redGoal == null || blueGoal == null) return;

        redCoach = new TeamCoach(this, redTeam, blueGoal, redGoal);
        blueCoach = new TeamCoach(this, blueTeam, redGoal, blueGoal);
        foreach (var a in redTeam) if (a != null) coachOf[a] = redCoach;
        foreach (var a in blueTeam) if (a != null) coachOf[a] = blueCoach;

        if (coachMode == CoachMode.Learned)
        {
            redCoachAgent = CreateCoachAgent("Red", 0, redCoach, redTeam, blueTeam, blueGoal, redGoal, redCoachModel);
            blueCoachAgent = CreateCoachAgent("Blue", 1, blueCoach, blueTeam, redTeam, redGoal, blueGoal, blueCoachModel);
        }
    }

    /// <summary>
    /// Creates a team's Coordinator agent at runtime (only in Learned mode), so the scenes need no coach
    /// objects. The object is configured while inactive, because ML-Agents reads Behavior Parameters
    /// when the agent is enabled.
    /// </summary>
    private TeamCoachAgent CreateCoachAgent(string label, int teamId, TeamCoach coach, List<FootballAgentML> mine,
                                            List<FootballAgentML> theirs, Transform attackGoal, Transform defendGoal,
                                            ModelAsset model)
    {
        var go = new GameObject($"Coach {label}");
        go.SetActive(false);
        go.transform.SetParent(transform, false);
        var bp = go.AddComponent<BehaviorParameters>();
        bp.BehaviorName = TeamCoachAgent.BehaviorNameValue;
        bp.TeamId = teamId;
        bp.BrainParameters.VectorObservationSize = TeamCoachAgent.ObservationSize;
        bp.BrainParameters.NumStackedVectorObservations = 1;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(TeamCoach.FormationCount);
        bp.Model = model;
        var agent = go.AddComponent<TeamCoachAgent>();
        agent.MaxStep = 0;
        agent.Setup(this, coach, mine, theirs, attackGoal, defendGoal);
        go.SetActive(true);
        return agent;
    }

    /// <summary>The coach of `agent`'s team, or null when there is no coach.</summary>
    public TeamCoach CoachOf(FootballAgentML agent) =>
        agent != null && coachOf.TryGetValue(agent, out var c) ? c : null;

    /// <summary>Whether `agent` is the one meant to go for the ball: the coach's chaser, or without a
    /// coach the teammate closest to the ball (the original rule).</summary>
    public bool IsChaser(FootballAgentML agent)
    {
        TeamCoach c = CoachOf(agent);
        return c != null ? c.IsChaser(agent) : IsClosestTeammateToBall(agent);
    }

    private TeamCoachAgent CoachAgentOf(List<FootballAgentML> team) => team == redTeam ? redCoachAgent : blueCoachAgent;

    private IEnumerable<TeamCoachAgent> CoachAgents()
    {
        if (redCoachAgent != null) yield return redCoachAgent;
        if (blueCoachAgent != null) yield return blueCoachAgent;
    }

    // Formation decision clock (shared by both coaches: the same moments matter to both teams)
    private int lastPossessingTeam = -1;   // team of the last ball carrier (0 Red, 1 Blue), -1 none yet
    private int stepsSinceDecision;
    private bool decisionPending;
    private float prevRedTerritory;

    private void StepCoaches()
    {
        if (redCoach == null) return;

        // A switch of possession between the teams is a decision point for both coaches.
        FootballAgent carrier = FootballAgent.activeBallCarrier;
        if (carrier != null && teamOfBody.TryGetValue(carrier, out int team) && team != lastPossessingTeam)
        {
            lastPossessingTeam = team;
            decisionPending = true;
        }

        stepsSinceDecision++;
        int periodSteps = Mathf.Max(1, Mathf.RoundToInt(coachDecisionPeriod / Time.fixedDeltaTime));
        int minGapSteps = Mathf.Max(1, Mathf.RoundToInt(coachMinGap / Time.fixedDeltaTime));
        if ((decisionPending && stepsSinceDecision >= minGapSteps) || stepsSinceDecision >= periodSteps)
        {
            stepsSinceDecision = 0;
            decisionPending = false;
            if (coachMode == CoachMode.Scripted)
            {
                int choices = coachAllowPress ? TeamCoach.FormationCount : TeamCoach.PressFormation;   // Press is last
                redCoach.SetFormation(UnityEngine.Random.Range(0, choices));
                blueCoach.SetFormation(UnityEngine.Random.Range(0, choices));
            }
            else
            {
                // The Coordinator's action arrives on the next step and calls TeamCoach.SetFormation.
                foreach (var agent in CoachAgents()) agent.RequestDecision();
            }
        }

        if (StepCount % coachPeriodSteps == 0) { redCoach.Tick(); blueCoach.Tick(); }
        redCoach.Step();
        blueCoach.Step();

        // Territory: each coach is paid for every metre the ball moves towards the goal its team attacks
        // (and pays for every metre towards its own). Zero-sum, potential-based: over a match it adds up to
        // where the ball ends, so it does not change what winning means, it only gives credit sooner.
        float redTerritory = redCoach.Territory();
        float delta = coachTerritoryWeight * (redTerritory - prevRedTerritory);
        prevRedTerritory = redTerritory;
        if (redCoachAgent != null) redCoachAgent.AddReward(delta);
        if (blueCoachAgent != null) blueCoachAgent.AddReward(-delta);
    }

    private void OnEnable() { GameManager.OnGoalScoredEvent += HandleGoal; }
    private void OnDisable() { GameManager.OnGoalScoredEvent -= HandleGoal; }

    private void Start()
    {
        ReadParameters();
        if (!ValidateSetup())
        {
            // A wrongly set up scene must not train silently: stop instead.
            Debug.LogError("MatchController: setup errors above. Stopping.");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(1);
#endif
            return;
        }
        if (Mathf.Abs(lambda - sceneLambda) > 1e-4f)
            Debug.LogWarning($"MatchController: the trainer set lambda={lambda}, the scene says {sceneLambda}. Using {lambda}.");
        ResetScene();
        Debug.Log($"MatchController: {redTeam.Count}v{blueTeam.Count}, lambda={lambda}, shaping_scale={shapingScale}, " +
                  $"spacing_weight={spacingWeight}, spacing_distance={spacingDistance} m, " +
                  $"spacing exempts {(spacingExemptClosest ? "carrier + closest to ball (old rule)" : "carrier only")}, " +
                  $"coach={coachMode}, role_hold_weight={roleHoldWeight}, role_progress_weight={roleProgressWeight}, " +
                  $"role_radius={roleRadius} m, move_gain={moveGain}, coach_decision_period={coachDecisionPeriod} s, " +
                  $"coach_min_gap={coachMinGap} s, coach_territory_weight={coachTerritoryWeight}, " +
                  $"coach_allow_press={coachAllowPress}, " +
                  $"match length {maxEnvironmentSteps * Time.fixedDeltaTime:0} s");
    }

    private void ReadParameters()
    {
        var p = Academy.Instance.EnvironmentParameters;
        lambda = Mathf.Clamp01(p.GetWithDefault("lambda", lambda));
        shapingScale = Mathf.Max(0f, p.GetWithDefault("shaping_scale", shapingScale));
        spacingWeight = Mathf.Max(0f, p.GetWithDefault("spacing_weight", spacingWeight));
        spacingDistance = Mathf.Max(0.1f, p.GetWithDefault("spacing_distance", spacingDistance));
        spacingExemptClosest = p.GetWithDefault("spacing_exempt_closest", spacingExemptClosest ? 1f : 0f) > 0.5f;
        roleHoldWeight = Mathf.Max(0f, p.GetWithDefault("role_hold_weight", roleHoldWeight));
        roleProgressWeight = Mathf.Max(0f, p.GetWithDefault("role_progress_weight", roleProgressWeight));
        roleRadius = Mathf.Max(0.1f, p.GetWithDefault("role_radius", roleRadius));
        moveGain = Mathf.Max(0.1f, p.GetWithDefault("move_gain", moveGain));
        coachDecisionPeriod = Mathf.Max(0.5f, p.GetWithDefault("coach_decision_period", coachDecisionPeriod));
        coachMinGap = Mathf.Max(0.02f, p.GetWithDefault("coach_min_gap", coachMinGap));
        coachTerritoryWeight = Mathf.Max(0f, p.GetWithDefault("coach_territory_weight", coachTerritoryWeight));
        coachAllowPress = p.GetWithDefault("coach_allow_press", coachAllowPress ? 1f : 0f) > 0.5f;
    }

    /// <summary>Only checks the setup and logs what is wrong; changes nothing.</summary>
    private bool ValidateSetup()
    {
        bool ok = true;
        if (ball == null) { Debug.LogError("MatchController: Ball is not assigned."); ok = false; }
        if (redGoal == null || blueGoal == null) { Debug.LogError("MatchController: Red Goal / Blue Goal not assigned."); ok = false; }
        if (redTeam.Count == 0 || blueTeam.Count == 0) { Debug.LogError("MatchController: both teams need at least one player."); ok = false; }
        int maxPerTeam = Mathf.Min(FootballAgentML.MaxTeammates + 1, FootballAgentML.MaxOpponents);
        if (redTeam.Count > maxPerTeam || blueTeam.Count > maxPerTeam)
        {
            Debug.LogError($"MatchController: at most {maxPerTeam} players per team (the observations have slots for 2 teammates and 3 opponents).");
            ok = false;
        }
        ok &= CheckTeam(redTeam, "Red Team", 0);
        ok &= CheckTeam(blueTeam, "Blue Team", 1);
        return ok;
    }

    private bool CheckTeam(List<FootballAgentML> team, string listName, int teamId)
    {
        bool ok = true;
        foreach (var a in team)
        {
            if (a == null) { Debug.LogError($"MatchController: empty slot in {listName}."); ok = false; continue; }
            var bp = a.GetComponent<BehaviorParameters>();
            if (bp != null && bp.TeamId != teamId)
            {
                Debug.LogError($"MatchController: {a.name} is in {listName}, so Behavior Parameters > Team Id must be {teamId} (is {bp.TeamId}).");
                ok = false;
            }
            if (bp != null && bp.BrainParameters.VectorObservationSize != FootballAgentML.ObservationSize)
            {
                Debug.LogError($"MatchController: {a.name} Behavior Parameters > Space Size must be {FootballAgentML.ObservationSize} (is {bp.BrainParameters.VectorObservationSize}).");
                ok = false;
            }
            if (a.MaxStep != 0)
            {
                Debug.LogError($"MatchController: {a.name} has Max Step {a.MaxStep}; set it to 0 (the controller ends matches).");
                ok = false;
            }
        }
        return ok;
    }

    private void FixedUpdate()
    {
        TrackPossession();
        TrackSpacing(redTeam);
        TrackSpacing(blueTeam);
        StepCoaches();
        StepCount++;
        if (StepCount >= maxEnvironmentSteps)
        {
            redCoach?.EndSegment(0);
            blueCoach?.EndSegment(0);
            RecordMatchStats(goalScored: false);
            foreach (var a in AllAgents()) a.EpisodeInterrupted();
            foreach (var c in CoachAgents()) c.EpisodeInterrupted();
            ResetScene();
        }
    }

    private void HandleGoal(string scoringTeam)
    {
        var scorers = scoringTeam == "TeamRed" ? redTeam : blueTeam;
        var conceders = scoringTeam == "TeamRed" ? blueTeam : redTeam;

        // R_i = (1 - lambda) * r_ind + lambda * R_team
        // r_ind = goal value for the last player on the scoring team to touch the ball, else 0.
        float remaining = Mathf.Clamp01((float)(maxEnvironmentSteps - StepCount) / maxEnvironmentSteps);
        float goalValue = goalReward + speedBonus * remaining;
        FootballAgent lastTouch = FootballAgent.LastToucher;

        foreach (var a in scorers)
        {
            if (a == null) continue;
            float rInd = (lastTouch != null && a.Body == lastTouch) ? goalValue : 0f;
            a.AddReward((1f - lambda) * rInd + lambda * goalValue);
        }
        foreach (var a in conceders) if (a != null) a.AddReward(concedePenalty);

        // Coaches judge the team result only (the same for every lambda), zero-sum: +goal value for
        // scoring, -goal value for conceding.
        CoachAgentOf(scorers)?.AddReward(goalValue);
        CoachAgentOf(conceders)?.AddReward(-goalValue);
        CoachOf(scorers.Count > 0 ? scorers[0] : null)?.EndSegment(+1);
        CoachOf(conceders.Count > 0 ? conceders[0] : null)?.EndSegment(-1);

        RecordMatchStats(goalScored: true);
        foreach (var a in AllAgents()) a.EndEpisode();
        foreach (var c in CoachAgents()) c.EndEpisode();
        ResetScene();
    }

    // ---------------- Shaping helpers (called by FootballAgentML) ----------------

    /// <summary>True if no teammate of `agent` is closer to the ball than `agent`.</summary>
    public bool IsClosestTeammateToBall(FootballAgentML agent)
    {
        if (ball == null || agent.ownTeam == null) return true;
        float mine = (agent.transform.position - ball.position).sqrMagnitude;
        foreach (var mate in agent.ownTeam)
        {
            if (mate == null || mate == agent || !mate.gameObject.activeInHierarchy) continue;
            if ((mate.transform.position - ball.position).sqrMagnitude < mine) return false;
        }
        return true;
    }

    /// <summary>Distance on the ground from `agent` to its nearest teammate (infinity if it has none).</summary>
    public float NearestTeammateDistance(FootballAgentML agent)
    {
        float best = float.PositiveInfinity;
        if (agent.ownTeam == null) return best;
        Vector3 me = agent.transform.position;
        foreach (var mate in agent.ownTeam)
        {
            if (mate == null || mate == agent || !mate.gameObject.activeInHierarchy) continue;
            Vector3 d = mate.transform.position - me;
            d.y = 0f;
            best = Mathf.Min(best, d.magnitude);
        }
        return best;
    }

    /// <summary>
    /// Spacing penalty for this physics step (0 or negative). Every player except the ball carrier is
    /// penalised for standing within spacingDistance of a teammate, more the closer it is. Summed over
    /// a whole match spent right on top of a teammate it comes to -spacingWeight.
    /// When two players crowd each other both pay, so there is no way out except moving apart
    /// (or having the ball). With spacingExemptClosest (old rule) the teammate closest to the ball is
    /// exempt too.
    /// </summary>
    public float SpacingPenalty(FootballAgentML agent, bool carrying, bool closestToBall)
    {
        if (spacingWeight <= 0f || carrying) return 0f;
        if (spacingExemptClosest && closestToBall) return 0f;
        float d = NearestTeammateDistance(agent);
        if (d >= spacingDistance) return 0f;
        return -spacingWeight * (1f - d / spacingDistance) / maxEnvironmentSteps;
    }

    /// <summary>Ball-progress shaping: the carrier gets `reward`, each teammate gets lambda * `reward`.</summary>
    public void RewardBallProgress(FootballAgentML carrier, float reward)
    {
        carrier.AddReward(reward);
        if (lambda <= 0f || carrier.ownTeam == null) return;
        foreach (var mate in carrier.ownTeam)
            if (mate != null && mate != carrier) mate.AddReward(lambda * reward);
    }

    // ---------------- Match statistics (TensorBoard "Match/...") ----------------

    /// <summary>
    /// Watches who has the ball. When possession moves to another player:
    ///   teammate, after the ball was released (kick / loose ball)  -> pass
    ///   teammate, taken straight off the carrier                   -> teammate steal
    ///   opponent, after the ball was released                      -> interception
    ///   opponent, taken straight off the carrier                   -> snatch
    /// </summary>
    private void TrackPossession()
    {
        FootballAgent carrier = FootballAgent.activeBallCarrier;
        if (carrier == null)
        {
            if (lastCarrier != null) ballLooseSinceLastCarrier = true;
            return;
        }
        if (carrier != lastCarrier && lastCarrier != null &&
            teamOfBody.TryGetValue(carrier, out int newTeam) && teamOfBody.TryGetValue(lastCarrier, out int oldTeam))
        {
            if (newTeam == oldTeam) { if (ballLooseSinceLastCarrier) passes++; else teammateSteals++; }
            else { if (ballLooseSinceLastCarrier) interceptions++; else snatches++; }
        }
        lastCarrier = carrier;
        ballLooseSinceLastCarrier = false;
    }

    /// <summary>Measures team shape every step (all players, whether or not the spacing penalty is on).</summary>
    private void TrackSpacing(List<FootballAgentML> team)
    {
        foreach (var a in team)
        {
            if (a == null) continue;
            float d = NearestTeammateDistance(a);
            if (float.IsInfinity(d)) continue;
            teammateDistanceSum += d;
            teammateDistanceSamples++;
            if (d < spacingDistance) crowdedSamples++;
        }
    }

    /// <summary>One sample per match; TensorBoard shows the average per match.</summary>
    private void RecordMatchStats(bool goalScored)
    {
        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Match/Goal Scored", goalScored ? 1f : 0f);
        stats.Add("Match/Passes", passes);
        stats.Add("Match/Interceptions", interceptions);
        stats.Add("Match/Snatches", snatches);
        stats.Add("Match/Teammate Steals", teammateSteals);
        if (teammateDistanceSamples > 0)
        {
            // Average distance from a player to its nearest teammate (metres), and the share of the
            // time players spent closer than spacingDistance to a teammate.
            stats.Add("Match/Nearest Teammate Distance", teammateDistanceSum / teammateDistanceSamples);
            stats.Add("Match/Crowded Share", (float)crowdedSamples / teammateDistanceSamples);
        }
        if (redCoach != null)
        {
            // Share of the time players with a slot were within role_radius of it, and how often each
            // formation was used (both teams together).
            int support = redCoach.SupportSteps + blueCoach.SupportSteps;
            if (support > 0)
                stats.Add("Coach/Role Adherence", (float)(redCoach.SupportInSlotSteps + blueCoach.SupportInSlotSteps) / support);
            stats.Add("Coach/Decisions per match", (redCoach.Decisions + blueCoach.Decisions) / 2f);
            int ticks = redCoach.Ticks + blueCoach.Ticks;
            if (ticks > 0)
                for (int f = 0; f < TeamCoach.FormationCount; f++)
                    stats.Add($"Coach/Formation {TeamCoach.FormationNames[f]}",
                              (float)(redCoach.FormationTicks[f] + blueCoach.FormationTicks[f]) / ticks);
        }
    }

    private IEnumerable<FootballAgentML> AllAgents()
    {
        foreach (var a in redTeam) if (a != null) yield return a;
        foreach (var a in blueTeam) if (a != null) yield return a;
    }

    /// <summary>Puts every player back where it was placed in the scene and the ball on the spot.</summary>
    public void ResetScene()
    {
        StepCount = 0;
        ReadParameters();
        FootballAgent.ClearGlobalPossession();

        lastCarrier = null;
        ballLooseSinceLastCarrier = false;
        passes = interceptions = snatches = teammateSteals = 0;
        teammateDistanceSum = 0f;
        teammateDistanceSamples = crowdedSamples = 0;

        foreach (var a in AllAgents()) a.ResetToKickoff();

        if (ball != null)
        {
            ball.position = ballSpawn;
            if (ballRb != null)
            {
                ballRb.isKinematic = false;
                ballRb.linearVelocity = Vector3.zero;
                ballRb.angularVelocity = Vector3.zero;
            }
        }

        // After the ball is back on the spot (territory and slots are measured from the ball).
        if (redCoach != null)
        {
            redCoach.ResetForKickoff();
            blueCoach.ResetForKickoff();
            // First formation decision on the first step of the match.
            lastPossessingTeam = -1;
            decisionPending = true;
            stepsSinceDecision = int.MaxValue / 2;
            prevRedTerritory = redCoach.Territory();
        }
    }
}
