using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

/// <summary>
/// ML-Agents brain for one football player. Nothing to assign in the Inspector except Max Step (= 0).
///
/// Everything match-related lives on MatchController, which is the single place to set up a match:
/// the team lists decide which side a player is on, and the controller hands each player its
/// goals, the ball, its teammates/opponents and the reward settings when the scene starts.
///
/// KICK-OFF: the player's Transform position and rotation in the scene ARE its kick-off spot.
///
/// OBSERVATIONS (49 = Behavior Parameters > Space Size):
///   0-20  the original 21, unchanged in order and units from the solo / vs-TestBrain / 1v1 runs.
///   21-45 the other players: 2 teammates then 3 opponents, each sorted nearest first,
///         5 numbers each: relative position x/z, velocity x/z, has-the-ball flag.
///         Empty slots (e.g. in a 1v1 scene) are zeros.
///   46-48 the coach's instruction (see TeamCoach): 1 if I am the chaser, then the vector from me to
///         my slot (zero for the chaser, whose target is the ball). Zeros when there is no coach.
/// The 3v3 base model was made from the 1v1 model by tools/convert_to_3v3.py (21 -> 46 inputs), and the
/// coach models from the 3M 3v3 models by tools/expand_for_coach.py (46 -> 49). New inputs always start
/// with zero weight, so each stage starts out playing exactly like the one before.
///
/// Team Blue gets every x/z quantity rotated 180 degrees (and its movement action rotated back),
/// so it sees the game exactly as Team Red does and one shared policy can play both sides.
///
/// ACTIONS: continuous [moveX, moveZ, kickAngleX, kickElevation, kickForce], discrete [kick 0/1].
/// </summary>
[RequireComponent(typeof(FootballAgent))]
public class FootballAgentML : Agent
{
    public const int BaseObservationSize = 21;   // the original observations
    public const int MaxTeammates = 2;
    public const int MaxOpponents = 3;
    public const int ObservationsPerPlayer = 5;
    public const int CoachObservationSize = 3;   // am I the chaser, vector to my slot x/z
    public const int ObservationSize =
        BaseObservationSize + (MaxTeammates + MaxOpponents) * ObservationsPerPlayer + CoachObservationSize;   // 49

    // Set by MatchController at startup (not shown in the Inspector).
    [System.NonSerialized] public Transform opponentGoal;   // also read by TestBrain
    [System.NonSerialized] public Transform ownGoal;
    [System.NonSerialized] public Transform targetBall;
    [System.NonSerialized] public MatchController match;
    [System.NonSerialized] public List<FootballAgentML> ownTeam;     // includes this player
    [System.NonSerialized] public List<FootballAgentML> otherTeam;

    public FootballAgent Body { get; private set; }

    private Rigidbody agentRb;
    private Rigidbody ballRb;
    private Vector3 kickoffPosition;
    private Quaternion kickoffRotation;
    private bool kickoffCaptured;
    private float prevDistanceToBall;
    private float prevDistanceBallToGoal;
    private bool distancesInitialised;
    private bool hasSecuredFirstPossession;
    private float prevSlotDistance;
    private int slotVersionSeen = -1;

    // Reused every observation to sort the other players by distance (no garbage per step).
    private readonly List<FootballAgentML> nearestBuffer = new List<FootballAgentML>(4);
    private System.Comparison<FootballAgentML> byDistance;

    /// <summary>Called once by MatchController before the first step.</summary>
    public void JoinMatch(MatchController controller, Transform attackGoal, Transform defendGoal, Transform ball,
                          List<FootballAgentML> myTeam, List<FootballAgentML> opponents)
    {
        match = controller;
        opponentGoal = attackGoal;
        ownGoal = defendGoal;
        targetBall = ball;
        ownTeam = myTeam;
        otherTeam = opponents;
        ballRb = ball != null ? ball.GetComponent<Rigidbody>() : null;
        CacheComponentsAndKickoff();
    }

    private void CacheComponentsAndKickoff()
    {
        if (Body == null) Body = GetComponent<FootballAgent>();
        if (agentRb == null) agentRb = GetComponent<Rigidbody>();
        if (!kickoffCaptured)
        {
            // Where the player was placed in the scene = its kick-off spot.
            kickoffPosition = transform.position;
            kickoffRotation = transform.rotation;
            kickoffCaptured = true;
        }
    }

    /// <summary>+1 if this agent attacks towards +x in world space, -1 otherwise.</summary>
    public float TeamSign =>
        (opponentGoal != null && ownGoal != null && opponentGoal.position.x < ownGoal.position.x) ? -1f : 1f;

    /// <summary>+1 for the side the model was trained on (attacking -x, Team Red), -1 for the mirrored side.</summary>
    public float MirrorSign => -TeamSign;

    private Vector3 Mirror(Vector3 v)
    {
        float m = MirrorSign;
        return new Vector3(m * v.x, v.y, m * v.z);
    }

    public override void Initialize()
    {
        CacheComponentsAndKickoff();
    }

    public override void OnEpisodeBegin()
    {
        // MatchController.ResetScene() moves everyone; only reset per-episode trackers here.
        distancesInitialised = false;
        hasSecuredFirstPossession = false;
        slotVersionSeen = -1;
        if (Body != null) Body.ReleasePossession();
    }

    /// <summary>Called by MatchController at kick-off.</summary>
    public void ResetToKickoff()
    {
        CacheComponentsAndKickoff();
        transform.SetPositionAndRotation(kickoffPosition, kickoffRotation);
        if (agentRb != null)
        {
            agentRb.position = kickoffPosition;
            agentRb.rotation = kickoffRotation;
            agentRb.linearVelocity = Vector3.zero;
            agentRb.angularVelocity = Vector3.zero;
        }
        if (Body != null) Body.ReleasePossession();
        distancesInitialised = false;
    }

    private void InitialiseDistancesIfNeeded()
    {
        if (distancesInitialised || targetBall == null || opponentGoal == null) return;
        prevDistanceToBall = Vector3.Distance(transform.position, targetBall.position);
        prevDistanceBallToGoal = Vector3.Distance(targetBall.position, opponentGoal.position);
        distancesInitialised = true;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (targetBall == null || opponentGoal == null)
        {
            for (int i = 0; i < ObservationSize; i++) sensor.AddObservation(0f);
            return;
        }
        InitialiseDistancesIfNeeded();

        // ---- The original 21 (do not reorder: the trained network depends on this order) ----

        // 1. Agent state & orientation (7)
        Vector3 pos = Mirror(transform.localPosition);
        Vector3 agentVel = Mirror(agentRb != null ? agentRb.linearVelocity : Vector3.zero);
        Vector3 fwd = Mirror(transform.forward);
        sensor.AddObservation(pos.x);
        sensor.AddObservation(pos.z);
        sensor.AddObservation(agentVel.x);
        sensor.AddObservation(agentVel.z);
        sensor.AddObservation(fwd.x);
        sensor.AddObservation(fwd.z);
        sensor.AddObservation(Body != null ? Body.CurrentPossessionTime : 0f);

        // 2. Ball relative state & possession (7)
        Vector3 toBall = Mirror(targetBall.position - transform.position);
        Vector3 ballVel = Mirror(ballRb != null ? ballRb.linearVelocity : Vector3.zero);
        sensor.AddObservation(toBall);
        sensor.AddObservation(ballVel);
        sensor.AddObservation((Body != null && Body.HasPossession) ? 1.0f : 0.0f);

        // 3. Goal relative vectors (4)
        Vector3 toOppGoal = Mirror(opponentGoal.position - transform.position);
        Vector3 toOwnGoal = ownGoal != null ? Mirror(ownGoal.position - transform.position) : Vector3.zero;
        sensor.AddObservation(toOppGoal.x);
        sensor.AddObservation(toOppGoal.z);
        sensor.AddObservation(toOwnGoal.x);
        sensor.AddObservation(toOwnGoal.z);

        // 4. Scalar distances & velocity decay state (3)
        sensor.AddObservation(prevDistanceToBall);
        sensor.AddObservation(prevDistanceBallToGoal);
        sensor.AddObservation(Body != null ? Body.CurrentMaxVelocity : 8f);

        // ---- New in 3v3 (25) ----

        // 5. Teammates (2 x 5), then opponents (3 x 5), each nearest first
        AddPlayerObservations(sensor, ownTeam, MaxTeammates);
        AddPlayerObservations(sensor, otherTeam, MaxOpponents);

        // ---- New with the coach (3) ----

        // 6. The coach's instruction: am I the chaser, and where should I be
        TeamCoach coach = match != null ? match.CoachOf(this) : null;
        if (coach == null)
        {
            for (int i = 0; i < CoachObservationSize; i++) sensor.AddObservation(0f);
        }
        else
        {
            // The chaser's target is the ball, which it already sees, so its slot vector is zero.
            bool isChaser = coach.IsChaser(this);
            Vector3 toTarget = isChaser ? Vector3.zero : Mirror(coach.SlotOf(this) - transform.position);
            sensor.AddObservation(isChaser ? 1f : 0f);
            sensor.AddObservation(toTarget.x);
            sensor.AddObservation(toTarget.z);
        }
    }

    /// <summary>Adds `slots` x 5 numbers for the nearest players of `team` (excluding this one); zeros for empty slots.</summary>
    private void AddPlayerObservations(VectorSensor sensor, List<FootballAgentML> team, int slots)
    {
        if (byDistance == null)
            byDistance = (a, b) =>
                (a.transform.position - transform.position).sqrMagnitude
                .CompareTo((b.transform.position - transform.position).sqrMagnitude);

        nearestBuffer.Clear();
        if (team != null)
        {
            foreach (var p in team)
                if (p != null && p != this && p.gameObject.activeInHierarchy) nearestBuffer.Add(p);
        }
        nearestBuffer.Sort(byDistance);

        for (int i = 0; i < slots; i++)
        {
            if (i < nearestBuffer.Count)
            {
                FootballAgentML p = nearestBuffer[i];
                Vector3 relPos = Mirror(p.transform.position - transform.position);
                Vector3 vel = Mirror(p.agentRb != null ? p.agentRb.linearVelocity : Vector3.zero);
                sensor.AddObservation(relPos.x);
                sensor.AddObservation(relPos.z);
                sensor.AddObservation(vel.x);
                sensor.AddObservation(vel.z);
                sensor.AddObservation((p.Body != null && p.Body.HasPossession) ? 1f : 0f);
            }
            else
            {
                for (int k = 0; k < ObservationsPerPlayer; k++) sensor.AddObservation(0f);
            }
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (Body == null || match == null) return;

        float moveX = actions.ContinuousActions[0];
        float moveZ = actions.ContinuousActions[1];
        float kickAngleX = actions.ContinuousActions[2];
        float kickElevY = Mathf.Clamp01(actions.ContinuousActions[3]);
        float kickForce = Mathf.Clamp01(actions.ContinuousActions[4]);
        int kickFlag = actions.DiscreteActions[0];

        // 1. Movement (mirrored back to world frame for Team Blue). move_gain scales it before it is
        //    capped at full speed (2 for the coach models, whose movement outputs were halved).
        float m = MirrorSign;
        Vector2 moveInput = new Vector2(m * moveX, m * moveZ) * match.moveGain;
        if (moveInput.sqrMagnitude > 1f) moveInput.Normalize();
        Body.ApplyMovement(moveInput);

        float s = match.ShapingScale;
        bool possessing = Body.HasPossession;
        bool closestToBall = match.IsClosestTeammateToBall(this);
        bool chaser = match.IsChaser(this);   // the coach's chaser, or without a coach the closest teammate
        InitialiseDistancesIfNeeded();

        // 2. One-time possession bonus per episode
        if (possessing && !hasSecuredFirstPossession)
        {
            hasSecuredFirstPossession = true;
            AddReward(s * match.firstPossessionBonus);
        }

        // 3. Dense shaping
        if (!possessing && targetBall != null)
        {
            // Approach the ball: only the chaser is paid (the coach's chaser, or without a coach the
            // player on this team closest to the ball), so the whole team does not swarm the ball.
            float distToBall = Vector3.Distance(transform.position, targetBall.position);
            if (chaser)
                AddReward(s * match.approachBallWeight * (prevDistanceToBall - distToBall));
            prevDistanceToBall = distToBall;
        }
        else if (possessing && targetBall != null && opponentGoal != null)
        {
            // Carry the ball towards goal: the carrier gets this, teammates get lambda x this
            // (the same (1 - lambda) * individual + lambda * team split as the goal reward).
            float distToGoal = Vector3.Distance(targetBall.position, opponentGoal.position);
            match.RewardBallProgress(this, s * match.advanceBallWeight * (prevDistanceBallToGoal - distToGoal));
            prevDistanceBallToGoal = distToGoal;
        }

        // 4. Power kick (kick angle is relative to the body, so it needs no mirroring)
        if (kickFlag == 1 && Body.CanKickBall())
        {
            Body.ExecuteKick(new Vector2(kickAngleX, kickElevY), kickForce);

            if (opponentGoal != null)
            {
                Vector3 toGoal = (opponentGoal.position - transform.position).normalized;
                float alignment = Vector3.Dot(transform.forward, toGoal);
                float distToGoal = Vector3.Distance(transform.position, opponentGoal.position);
                if (alignment > 0.3f && distToGoal < match.shotRange)
                    AddReward(s * match.shotTowardGoalBonus * alignment);
            }
        }

        // 5. Existential penalty: -1 over a full-length match
        AddReward(-1f / match.maxEnvironmentSteps);

        // 6. Team shape: every player except the ball carrier is penalised for crowding a teammate
        //    (0 unless spacing_weight is set in the trainer YAML).
        AddReward(match.SpacingPenalty(this, possessing, closestToBall));

        // 7. Coach role: a player given a slot is paid for moving towards it and for being in it.
        RewardRole(possessing);
    }

    private void RewardRole(bool possessing)
    {
        TeamCoach coach = match.CoachOf(this);
        if (coach == null || possessing || coach.IsChaser(this))
        {
            slotVersionSeen = -1;
            return;
        }
        float d = TeamCoach.FlatDistance(transform.position, coach.SlotOf(this));
        int v = coach.VersionOf(this);
        // Progress is only paid while the target is the same one as last step (not when it jumps).
        if (v == slotVersionSeen) AddReward(match.roleProgressWeight * (prevSlotDistance - d));
        slotVersionSeen = v;
        prevSlotDistance = d;
        if (d < match.roleRadius)
            AddReward(match.roleHoldWeight * (1f - d / match.roleRadius) / match.maxEnvironmentSteps);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // Neutral fallback. Manual control lives in ManualPlayerController.cs.
        var continuous = actionsOut.ContinuousActions;
        var discrete = actionsOut.DiscreteActions;
        continuous[0] = 0f;
        continuous[1] = 0f;
        continuous[2] = 0f;
        continuous[3] = 0.2f;
        continuous[4] = 0f;
        discrete[0] = 0;
    }
}
