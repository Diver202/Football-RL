using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

/// <summary>
/// ML-Agents brain for one football player. Nothing to assign in the Inspector except Max Step (= 0).
///
/// Everything match-related lives on MatchController, which is the single place to set up a match:
/// the team lists decide which side a player is on, and the controller hands each player its
/// goals, the ball and the reward settings when the scene starts.
///
/// KICK-OFF: the player's Transform position and rotation in the scene ARE its kick-off spot.
///
/// OBSERVATIONS: the same 21 floats, in the same order and units, as the solo and vs-TestBrain runs,
/// so those checkpoints stay loadable. Team Blue gets every x/z quantity rotated 180 degrees (and
/// its movement action rotated back), so it sees the game exactly as Team Red does and one shared
/// policy can play both sides. For Team Red the observations are identical to the old ones.
///
/// ACTIONS: continuous [moveX, moveZ, kickAngleX, kickElevation, kickForce], discrete [kick 0/1].
/// </summary>
[RequireComponent(typeof(FootballAgent))]
public class FootballAgentML : Agent
{
    public const int ObservationSize = 21;

    // Set by MatchController at startup (not shown in the Inspector).
    [System.NonSerialized] public Transform opponentGoal;   // also read by TestBrain
    [System.NonSerialized] public Transform ownGoal;
    [System.NonSerialized] public Transform targetBall;
    [System.NonSerialized] public MatchController match;

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

    /// <summary>Called once by MatchController before the first step.</summary>
    public void JoinMatch(MatchController controller, Transform attackGoal, Transform defendGoal, Transform ball)
    {
        match = controller;
        opponentGoal = attackGoal;
        ownGoal = defendGoal;
        targetBall = ball;
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

        // 1. Movement (mirrored back to world frame for Team Blue)
        float m = MirrorSign;
        Vector2 moveInput = new Vector2(m * moveX, m * moveZ);
        if (moveInput.sqrMagnitude > 1f) moveInput.Normalize();
        Body.ApplyMovement(moveInput);

        float s = match.ShapingScale;
        bool possessing = Body.HasPossession;
        InitialiseDistancesIfNeeded();

        // 2. One-time possession bonus per episode
        if (possessing && !hasSecuredFirstPossession)
        {
            hasSecuredFirstPossession = true;
            AddReward(s * match.firstPossessionBonus);
        }

        // 3. Dense shaping (identical to the vs-bot run)
        if (!possessing && targetBall != null)
        {
            float distToBall = Vector3.Distance(transform.position, targetBall.position);
            AddReward(s * match.approachBallWeight * (prevDistanceToBall - distToBall));
            prevDistanceToBall = distToBall;
        }
        else if (possessing && targetBall != null && opponentGoal != null)
        {
            float distToGoal = Vector3.Distance(targetBall.position, opponentGoal.position);
            AddReward(s * match.advanceBallWeight * (prevDistanceBallToGoal - distToGoal));
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
