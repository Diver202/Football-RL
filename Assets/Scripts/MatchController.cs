using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;

/// <summary>
/// Referee for one match between RL agents (independent learners, PPO + self-play).
/// This is the ONE place a match is set up:
///   - Red Team / Blue Team lists decide which side each player is on,
///   - Red Goal / Blue Goal and Ball are assigned once here and handed to every player,
///   - all reward settings live here.
///
/// It does not create, move or reconfigure objects in the editor. At runtime it:
///   - resets players (to where they were placed in the scene) and the ball at kick-off,
///   - ends every agent's episode on a goal or when time runs out,
///   - gives the goal rewards: R_i = (1 - lambda) * r_ind + lambda * R_team, and the concede penalty.
/// All rewards are plain per-agent rewards (Agent.AddReward), because PPO ignores group rewards.
///
/// lambda and shaping_scale can be overridden from the trainer YAML (environment_parameters).
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
    [Tooltip("Ball position at kick-off. (0, 0.15, 0) matches the vs-bot run.")]
    public Vector3 ballSpawn = new Vector3(0f, 0.15f, 0f);

    [Header("Goal Rewards")]
    [Range(0f, 1f)] public float lambda = 0f;
    public float goalReward = 10f;
    [Tooltip("Extra reward for scoring early, scaled by the fraction of time remaining.")]
    public float speedBonus = 5f;
    [Tooltip("Given to every player of the team that concedes.")]
    public float concedePenalty = -5f;

    [Header("Dense Shaping (per player, same values as the vs-bot run)")]
    [Min(0f)] public float shapingScale = 1f;
    public float approachBallWeight = 0.02f;
    public float advanceBallWeight = 0.04f;
    public float firstPossessionBonus = 1.0f;
    public float shotTowardGoalBonus = 0.3f;
    public float shotRange = 35f;

    [Header("Episode")]
    [Tooltip("Physics steps before the match is called a draw (50 steps = 1 second). 1200 matches the vs-bot run.")]
    public int maxEnvironmentSteps = 1200;

    public int StepCount { get; private set; }
    public float ShapingScale => shapingScale;

    private Rigidbody ballRb;

    private void Awake()
    {
        if (ball != null) ballRb = ball.GetComponent<Rigidbody>();
        foreach (var a in redTeam) if (a != null) a.JoinMatch(this, blueGoal, redGoal, ball);
        foreach (var a in blueTeam) if (a != null) a.JoinMatch(this, redGoal, blueGoal, ball);
    }

    private void OnEnable() { GameManager.OnGoalScoredEvent += HandleGoal; }
    private void OnDisable() { GameManager.OnGoalScoredEvent -= HandleGoal; }

    private void Start()
    {
        ReadParameters();
        ValidateSetup();
        ResetScene();
        Debug.Log($"MatchController: {redTeam.Count}v{blueTeam.Count}, lambda={lambda}, shaping_scale={shapingScale}");
    }

    private void ReadParameters()
    {
        var p = Academy.Instance.EnvironmentParameters;
        lambda = Mathf.Clamp01(p.GetWithDefault("lambda", lambda));
        shapingScale = Mathf.Max(0f, p.GetWithDefault("shaping_scale", shapingScale));
    }

    /// <summary>Only checks the setup and logs what is wrong; changes nothing.</summary>
    private void ValidateSetup()
    {
        if (ball == null) Debug.LogError("MatchController: Ball is not assigned.");
        if (redGoal == null || blueGoal == null) Debug.LogError("MatchController: Red Goal / Blue Goal not assigned.");
        CheckTeam(redTeam, "Red Team", 0);
        CheckTeam(blueTeam, "Blue Team", 1);
    }

    private void CheckTeam(List<FootballAgentML> team, string listName, int teamId)
    {
        foreach (var a in team)
        {
            if (a == null) { Debug.LogError($"MatchController: empty slot in {listName}."); continue; }
            var bp = a.GetComponent<BehaviorParameters>();
            if (bp != null && bp.TeamId != teamId)
                Debug.LogError($"MatchController: {a.name} is in {listName}, so Behavior Parameters > Team Id must be {teamId} (is {bp.TeamId}).");
            if (a.MaxStep != 0)
                Debug.LogError($"MatchController: {a.name} has Max Step {a.MaxStep}; set it to 0 (the controller ends matches).");
        }
    }

    private void FixedUpdate()
    {
        StepCount++;
        if (StepCount >= maxEnvironmentSteps)
        {
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

        foreach (var a in AllAgents()) a.EndEpisode();
        ResetScene();
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
