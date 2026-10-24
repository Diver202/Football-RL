using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

/// <summary>
/// ML-Agents reinforcement learning brain for FootballAgent.
/// Integrates observation collection, action execution (movement & kicking),
/// and reward shaping according to proposal.md.
/// </summary>
public class FootballAgentML : Agent
{
    [Header("Agent & Environment References")]
    [Tooltip("The FootballAgent physical component controlling locomotion and kicking.")]
    public FootballAgent agent;

    [Tooltip("The football GameObject (GlowBall).")]
    public Transform targetBall;

    [Tooltip("The opponent's goal post transform.")]
    public Transform opponentGoal;

    [Tooltip("Our own goal post transform.")]
    public Transform ownGoal;

    [Header("Spawn & Reset Positions")]
    public Vector3 initialAgentPosition = new Vector3(35f, 1f, 0f);
    public Vector3 initialBallPosition = new Vector3(0f, 0.15f, 0f);

    [Header("Hyperparameters & Reward Shaping")]
    public float possessionDistance = 1.3f;
    public float goalReward = 1.0f;
    public float concedePenalty = -1.0f;
    public float boundaryPenalty = -0.05f;

    [Header("Cooperation Parameter (proposal.md Section 3.3)")]
    [Tooltip("Cooperation coefficient lambda in [0, 1]. 0 = selfish (Phase 1), 1 = utilitarian team (Phase 3).")]
    [Range(0f, 1f)]
    public float lambda = 0.0f;

    private Rigidbody agentRb;
    private Rigidbody ballRb;
    private float prevDistanceToBall;
    private float prevDistanceBallToGoal;

    protected override void OnEnable()
    {
        base.OnEnable();
        GameManager.OnGoalScoredEvent += HandleGoalScored;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        GameManager.OnGoalScoredEvent -= HandleGoalScored;
    }

    private void HandleGoalScored(string scoringTeam)
    {
        bool scoredByThisTeam = CompareTag(scoringTeam);
        OnGoalScored(scoredByThisTeam);
    }

    public override void Initialize()
    {
        if (agent == null)
            agent = GetComponent<FootballAgent>();
        if (agentRb == null)
            agentRb = GetComponent<Rigidbody>();
        if (targetBall != null)
            ballRb = targetBall.GetComponent<Rigidbody>();
    }

    public override void OnEpisodeBegin()
    {
        // 1. Reset agent
        transform.position = initialAgentPosition;
        transform.rotation = Quaternion.identity;
        if (agentRb != null)
        {
            agentRb.linearVelocity = Vector3.zero;
            agentRb.angularVelocity = Vector3.zero;
        }

        // 2. Reset ball
        if (targetBall != null)
        {
            if (agent != null && agent.HasPossession)
            {
                agent.ReleasePossession();
            }

            targetBall.position = initialBallPosition;
            if (ballRb != null)
            {
                ballRb.isKinematic = false;
                ballRb.linearVelocity = Vector3.zero;
                ballRb.angularVelocity = Vector3.zero;
            }
        }

        // Cache initial distances for dense shaping
        if (targetBall != null)
        {
            prevDistanceToBall = Vector3.Distance(transform.position, targetBall.position);
            if (opponentGoal != null)
            {
                prevDistanceBallToGoal = Vector3.Distance(targetBall.position, opponentGoal.position);
            }
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (targetBall == null || opponentGoal == null) return;

        // 1. Agent Local State & Orientation (7 floats)
        Vector3 agentVel = agentRb != null ? agentRb.linearVelocity : Vector3.zero;
        sensor.AddObservation(transform.localPosition.x);                         // 1 float
        sensor.AddObservation(transform.localPosition.z);                         // 1 float
        sensor.AddObservation(agentVel.x);                                        // 1 float
        sensor.AddObservation(agentVel.z);                                        // 1 float
        sensor.AddObservation(transform.forward.x);                              // 1 float (heading)
        sensor.AddObservation(transform.forward.z);                              // 1 float (heading)
        sensor.AddObservation(agent != null ? agent.CurrentPossessionTime : 0f); // 1 float

        // 2. Ball Relative State & Possession (7 floats)
        Vector3 toBall = targetBall.position - transform.position;
        Vector3 ballVel = ballRb != null ? ballRb.linearVelocity : Vector3.zero;
        sensor.AddObservation(toBall);                                            // 3 floats
        sensor.AddObservation(ballVel);                                           // 3 floats
        sensor.AddObservation((agent != null && agent.HasPossession) ? 1.0f : 0.0f); // 1 float

        // 3. Goal Relative Vectors (4 floats)
        Vector3 toOppGoal = opponentGoal.position - transform.position;
        Vector3 toOwnGoal = ownGoal != null ? (ownGoal.position - transform.position) : Vector3.zero;
        sensor.AddObservation(toOppGoal.x);                                       // 1 float
        sensor.AddObservation(toOppGoal.z);                                       // 1 float
        sensor.AddObservation(toOwnGoal.x);                                       // 1 float
        sensor.AddObservation(toOwnGoal.z);                                       // 1 float

        // 4. Scalar Distances & Velocity Decay State (3 floats)
        sensor.AddObservation(prevDistanceToBall);                                // 1 float
        sensor.AddObservation(prevDistanceBallToGoal);                            // 1 float
        sensor.AddObservation(agent != null ? agent.CurrentMaxVelocity : 8f);     // 1 float
        // Total: Exactly 21 continuous observations (preserves Inspector Space Size: 21)
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (agent == null) return;

        // Continuous actions:
        // [0] = moveX (-1 to 1)
        // [1] = moveZ (-1 to 1)
        // [2] = kickHorizontalAngle (-1 to 1)
        // [3] = kickElevationAngle (0 to 1)
        // [4] = kickForceFraction (0 to 1)
        float moveX = actions.ContinuousActions[0];
        float moveZ = actions.ContinuousActions[1];
        float kickAngleX = actions.ContinuousActions[2];
        float kickElevY = Mathf.Clamp01(actions.ContinuousActions[3]);
        float kickForce = Mathf.Clamp01(actions.ContinuousActions[4]);

        // Discrete actions:
        // [0] = kickFlag (0: don't kick, 1: kick)
        int kickFlag = actions.DiscreteActions[0];

        // 1. Apply Movement (Full 360-degree omnidirectional continuous input)
        Vector2 moveInput = new Vector2(moveX, moveZ);
        if (moveInput.sqrMagnitude > 1f)
        {
            moveInput.Normalize();
        }
        agent.ApplyMovement(moveInput);

        // 2. Handle Possession Acquisition
        if (!agent.HasPossession && targetBall != null)
        {
            float distToBall = Vector3.Distance(transform.position, targetBall.position);
            if (distToBall <= possessionDistance)
            {
                agent.TakePossession(targetBall.gameObject);
                AddReward(0.3f); // Substantial reward for successfully securing possession
            }

            // Dense reward for moving closer to the ball (amplified for clearer gradient)
            float ballDistDelta = prevDistanceToBall - distToBall;
            AddReward(ballDistDelta * 0.01f);
            prevDistanceToBall = distToBall;
        }

        // 3. Handle Possession Actions (Kicking & Dribbling)
        if (agent.HasPossession && targetBall != null)
        {
            // Dense reward for advancing ball towards opponent goal
            float distToGoal = Vector3.Distance(targetBall.position, opponentGoal.position);
            float goalDistDelta = prevDistanceBallToGoal - distToGoal;
            AddReward(goalDistDelta * 0.01f);
            prevDistanceBallToGoal = distToGoal;

            // Execute Kick only when agent chooses to shoot
            if (kickFlag == 1)
            {
                agent.ExecuteKick(new Vector2(kickAngleX, kickElevY), kickForce);

                // Shaped Kick Reward: only reward kicks directed towards opponent goal
                // (Prevents the 'hot potato' exploit where the agent kicks randomly to harvest rewards)
                Vector3 toGoal = (opponentGoal.position - transform.position).normalized;
                float forwardAlignment = Vector3.Dot(transform.forward, toGoal);
                if (forwardAlignment > 0.25f)
                {
                    AddReward(0.05f * forwardAlignment);
                }
            }
        }

        // 4. Existential step penalty (discourages stalling/running in circles)
        if (MaxStep > 0)
        {
            AddReward(-1f / MaxStep);
        }
    }

    public void OnGoalScored(bool byThisTeam)
    {
        if (byThisTeam)
        {
            // Mathematical formulation from proposal.md Section 3.3:
            // R_i = (1 - lambda) * r_ind + lambda * R_team
            bool isGoalScorer = (agent != null && agent.HasPossession);
            float r_ind = isGoalScorer ? goalReward : (goalReward * 0.5f);
            float r_team = goalReward;
            float totalReward = ((1f - lambda) * r_ind) + (lambda * r_team);
            AddReward(totalReward);
        }
        else
        {
            AddReward(concedePenalty);
        }
        EndEpisode();
    }

    public void OnBallOutOfBounds()
    {
        AddReward(boundaryPenalty);
        EndEpisode();
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // Neutral fallback: Agent is driven purely by ML-Agents neural network.
        // Manual player controls have been completely decoupled into ManualPlayerController.cs
        // so that FreeCamera can navigate without accidentally controlling the bot.
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
