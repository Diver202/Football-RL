using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;

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
/// lambda and shaping_scale are overridden by the trainer YAML (environment_parameters) during training.
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

    private void Awake()
    {
        sceneLambda = lambda;
        if (ball != null) ballRb = ball.GetComponent<Rigidbody>();
        foreach (var a in redTeam) if (a != null) a.JoinMatch(this, blueGoal, redGoal, ball, redTeam, blueTeam);
        foreach (var a in blueTeam) if (a != null) a.JoinMatch(this, redGoal, blueGoal, ball, blueTeam, redTeam);
        foreach (var a in redTeam) if (a != null && a.Body != null) teamOfBody[a.Body] = 0;
        foreach (var a in blueTeam) if (a != null && a.Body != null) teamOfBody[a.Body] = 1;
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
                  $"match length {maxEnvironmentSteps * Time.fixedDeltaTime:0} s");
    }

    private void ReadParameters()
    {
        var p = Academy.Instance.EnvironmentParameters;
        lambda = Mathf.Clamp01(p.GetWithDefault("lambda", lambda));
        shapingScale = Mathf.Max(0f, p.GetWithDefault("shaping_scale", shapingScale));
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
        StepCount++;
        if (StepCount >= maxEnvironmentSteps)
        {
            RecordMatchStats(goalScored: false);
            foreach (var a in AllAgents()) a.EpisodeInterrupted();
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

        RecordMatchStats(goalScored: true);
        foreach (var a in AllAgents()) a.EndEpisode();
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

    /// <summary>One sample per match; TensorBoard shows the average per match.</summary>
    private void RecordMatchStats(bool goalScored)
    {
        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Match/Goal Scored", goalScored ? 1f : 0f);
        stats.Add("Match/Passes", passes);
        stats.Add("Match/Interceptions", interceptions);
        stats.Add("Match/Snatches", snatches);
        stats.Add("Match/Teammate Steals", teammateSteals);
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
    }
}
