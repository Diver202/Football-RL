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

        // 1. Agent Local State (7 floats)
        Vector3 agentVel = agentRb != null ? agentRb.linearVelocity : Vector3.zero;
        sensor.AddObservation(transform.localPosition);                     // 3 floats
        sensor.AddObservation(agentVel);                                    // 3 floats
        sensor.AddObservation(agent != null ? agent.CurrentPossessionTime : 0f); // 1 float

        // 2. Ball Relative State (7 floats)
        Vector3 toBall = targetBall.position - transform.position;
        Vector3 ballVel = ballRb != null ? ballRb.linearVelocity : Vector3.zero;
        sensor.AddObservation(toBall);                                      // 3 floats
        sensor.AddObservation(ballVel);                                     // 3 floats
        sensor.AddObservation((agent != null && agent.HasPossession) ? 1.0f : 0.0f); // 1 float

        // 3. Goal Relative Vectors (6 floats)
        Vector3 toOppGoal = opponentGoal.position - transform.position;
        Vector3 toOwnGoal = ownGoal != null ? (ownGoal.position - transform.position) : Vector3.zero;
        sensor.AddObservation(toOppGoal);                                   // 3 floats
        sensor.AddObservation(toOwnGoal);                                   // 3 floats

        // 4. Current Max Velocity (1 float)
        sensor.AddObservation(agent != null ? agent.CurrentMaxVelocity : 8f); // 1 float
        // Total: 21 continuous observations
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

        // 1. Apply Movement
        Vector2 moveInput = new Vector2(moveX, moveZ);
        agent.ApplyMovement(moveInput);

        // Turn towards movement direction if moving
        if (moveInput.sqrMagnitude > 0.01f)
        {
            Vector3 targetDir = new Vector3(moveX, 0f, moveZ).normalized;
            transform.forward = Vector3.Slerp(transform.forward, targetDir, 10f * Time.fixedDeltaTime);
        }

        // 2. Handle Possession Acquisition
        if (!agent.HasPossession && targetBall != null)
        {
            float distToBall = Vector3.Distance(transform.position, targetBall.position);
            if (distToBall <= possessionDistance)
            {
                agent.TakePossession(targetBall.gameObject);
                AddReward(0.25f); // Positive reward for successfully taking possession
            }

            // Dense reward for moving closer to the ball
            float ballDistDelta = prevDistanceToBall - distToBall;
            AddReward(ballDistDelta * 0.002f);
            prevDistanceToBall = distToBall;
        }

        // 3. Handle Possession Actions (Kicking & Dribbling)
        if (agent.HasPossession && targetBall != null)
        {
            // Dense reward for moving ball closer to opponent goal
            float distToGoal = Vector3.Distance(targetBall.position, opponentGoal.position);
            float goalDistDelta = prevDistanceBallToGoal - distToGoal;
            AddReward(goalDistDelta * 0.005f);
            prevDistanceBallToGoal = distToGoal;

            // Execute Kick if agent chooses to shoot
            if (kickFlag == 1)
            {
                agent.ExecuteKick(new Vector2(kickAngleX, kickElevY), kickForce);
                AddReward(0.05f); // Small reward for shooting/releasing ball towards goal
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
            AddReward(goalReward);
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
        var continuous = actionsOut.ContinuousActions;
        var discrete = actionsOut.DiscreteActions;

        float moveX = 0f;
        float moveZ = 0f;
        int kick = 0;

#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) moveX -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) moveX += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) moveZ -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) moveZ += 1f;
            if (keyboard.spaceKey.isPressed) kick = 1;
        }
#else
        moveX = Input.GetAxisRaw("Horizontal");
        moveZ = Input.GetAxisRaw("Vertical");
        if (Input.GetKey(KeyCode.Space)) kick = 1;
#endif

        continuous[0] = moveX;
        continuous[1] = moveZ;
        continuous[2] = 0f;    // straight forward
        continuous[3] = 0.2f;  // slight elevation
        continuous[4] = 1.0f;  // full power
        discrete[0] = kick;
    }
}
