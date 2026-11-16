using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// The learning coach ("Coordinator" behavior) of one team, used when MatchController's coach mode is
/// Learned. MatchController creates it at runtime (one per team); nothing to set up in the scene.
///
/// Every 0.5 s it sees the whole match from its team's point of view and picks a FORMATION
/// (see TeamCoach). It does not move players: it only gives them their instructions, and each player
/// still decides its own actions with its own policy and its own lambda-weighted reward.
///
/// OBSERVATIONS (40), mirrored for Team Blue like the players' observations:
///   ball position x/z, ball velocity x/z                                   (4)
///   own players in team-list order: position x/z, velocity x/z, has-ball   (15)
///   opponents, nearest to the ball first: same 5 each                      (15)
///   current formation, one-hot                                             (5)
///   fraction of the match remaining                                        (1)
/// ACTION: one discrete branch, the formation (5 options).
/// REWARD (team result, the same for every lambda): +goal value when the team scores, the concede
///   penalty when it concedes, and the ball-progress shaping whenever a teammate carries the ball forward.
/// </summary>
public class TeamCoachAgent : Agent
{
    public const string BehaviorNameValue = "Coordinator";
    public const int ObservationSize = 4 + 15 + 15 + TeamCoach.FormationCount + 1;   // 40

    private MatchController match;
    private TeamCoach coach;
    private List<FootballAgentML> team;
    private List<FootballAgentML> opponents;
    private Rigidbody ballRb;
    private float mirror = 1f;
    private readonly List<FootballAgentML> byBallDistance = new List<FootballAgentML>(3);

    /// <summary>Called by MatchController before the agent is enabled.</summary>
    public void Setup(MatchController controller, TeamCoach teamCoach, List<FootballAgentML> myTeam,
                      List<FootballAgentML> otherTeam, Transform attackGoal, Transform defendGoal)
    {
        match = controller;
        coach = teamCoach;
        team = myTeam;
        opponents = otherTeam;
        ballRb = controller.ball != null ? controller.ball.GetComponent<Rigidbody>() : null;
        // Same convention as FootballAgentML: +1 for the side that attacks -x (Team Red).
        mirror = attackGoal.position.x < defendGoal.position.x ? 1f : -1f;
    }

    private Vector3 Mirror(Vector3 v) => new Vector3(mirror * v.x, v.y, mirror * v.z);

    public override void CollectObservations(VectorSensor sensor)
    {
        if (match == null || match.ball == null)
        {
            for (int i = 0; i < ObservationSize; i++) sensor.AddObservation(0f);
            return;
        }

        Vector3 ballPos = Mirror(match.ball.position);
        Vector3 ballVel = Mirror(ballRb != null ? ballRb.linearVelocity : Vector3.zero);
        sensor.AddObservation(ballPos.x);
        sensor.AddObservation(ballPos.z);
        sensor.AddObservation(ballVel.x);
        sensor.AddObservation(ballVel.z);

        AddPlayers(sensor, team, sortByBall: false);
        AddPlayers(sensor, opponents, sortByBall: true);

        for (int f = 0; f < TeamCoach.FormationCount; f++) sensor.AddObservation(coach.Formation == f ? 1f : 0f);
        sensor.AddObservation(Mathf.Clamp01(1f - (float)match.StepCount / match.maxEnvironmentSteps));
    }

    private void AddPlayers(VectorSensor sensor, List<FootballAgentML> list, bool sortByBall)
    {
        byBallDistance.Clear();
        if (list != null) foreach (var p in list) if (p != null) byBallDistance.Add(p);
        if (sortByBall)
        {
            Vector3 b = match.ball.position;
            byBallDistance.Sort((a, c) => (a.transform.position - b).sqrMagnitude
                                          .CompareTo((c.transform.position - b).sqrMagnitude));
        }
        for (int i = 0; i < 3; i++)
        {
            if (i < byBallDistance.Count)
            {
                FootballAgentML p = byBallDistance[i];
                Vector3 pos = Mirror(p.transform.position);
                Rigidbody rb = p.GetComponent<Rigidbody>();
                Vector3 vel = Mirror(rb != null ? rb.linearVelocity : Vector3.zero);
                sensor.AddObservation(pos.x);
                sensor.AddObservation(pos.z);
                sensor.AddObservation(vel.x);
                sensor.AddObservation(vel.z);
                sensor.AddObservation(p.Body != null && p.Body.HasPossession ? 1f : 0f);
            }
            else
            {
                for (int k = 0; k < 5; k++) sensor.AddObservation(0f);
            }
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (coach != null) coach.SetFormation(actions.DiscreteActions[0]);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // No model and no trainer: keep the Balanced formation.
        var discrete = actionsOut.DiscreteActions;
        discrete[0] = TeamCoach.DefaultFormation;
    }
}
