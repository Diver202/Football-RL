using UnityEngine;

/// <summary>
/// Advanced heuristic brain for a 1v1 football agent.
/// Designed to provide a strong, realistic, and challenging baseline for training RL agents.
/// Implements:
/// - Dynamic trajectory interception (predicting ball movement and ground landing points)
/// - Defensive goal-side jockeying, shot blocking, and physical tackle execution
/// - Offensive opponent evasion (dribbling around defenders, feints, and spatial corridor routing)
/// - Intelligent shooting (finding open corners of the net, driven shots vs chipped lobs over defenders)
/// - Emergency clearances under defensive pressure
/// - Pitch boundary containment to avoid getting trapped on the walls
/// - Burst touches / self-passes to exploit unencumbered movement speed
/// </summary>
[RequireComponent(typeof(FootballAgent))]
public class SinglePlayerTrainer : MonoBehaviour
{
    public enum BotState
    {
        ChasingBall,
        AttackingWithBall,
        DefendingOpponent
    }

    [Header("Match References (Auto-detected if null)")]
    [Tooltip("The match ball")]
    public Transform targetBall;
    [Tooltip("The goal to score in")]
    public Transform opponentGoal;
    [Tooltip("The goal to defend")]
    public Transform ownGoal;
    [Tooltip("The opposing football agent")]
    public Transform opponent;

    [Header("Offensive / Shooting Settings")]
    [Tooltip("Maximum distance from opponent goal to attempt a shot")]
    public float maxShootDistance = 24f;
    [Tooltip("Minimum distance before shot is considered point-blank")]
    public float closeRangeShotDistance = 10f;
    [Tooltip("Target offset from goal posts into the corners of the goal")]
    public float goalCornerInset = 1.0f;
    [Tooltip("Width of the goal mouth (standard ~8.5m)")]
    public float goalWidth = 8.5f;
    [Tooltip("Required angle alignment before executing a kick (degrees)")]
    public float kickAlignmentThreshold = 25f;

    [Header("Dribbling & Evasion Settings")]
    [Tooltip("Distance at which opponent triggers evasion maneuvers")]
    public float evasionTriggerDistance = 9f;
    [Tooltip("Corridor width in front of the bot that must be clear to drive forward")]
    public float dribbleCorridorWidth = 3.5f;
    [Tooltip("Strength of lateral evasion force when dodging an opponent")]
    public float evasionStrength = 1.6f;
    [Tooltip("Emergency knock-ahead touch when defender rushes within tackling range")]
    public bool enableBurstKnockAhead = true;
    [Tooltip("Distance from defender that triggers an emergency burst touch")]
    public float burstTriggerDistance = 3.2f;

    [Header("Defensive / Jockey Settings")]
    [Tooltip("Distance in front of opponent to maintain during goal-side jockeying")]
    public float jockeyDistance = 4.0f;
    [Tooltip("Distance at which the bot commits from jockeying into a direct tackle sprint")]
    public float tackleCommitDistance = 3.0f;
    [Tooltip("Distance from own goal where defense becomes maximum urgency")]
    public float dangerZoneRadius = 18f;

    [Header("Clearance Settings")]
    [Tooltip("Radius around own goal where taking possession under pressure triggers an immediate lofted clearance")]
    public float clearanceDangerRadius = 16f;

    [Header("Pitch Boundaries")]
    public float pitchHalfLengthX = 50f;
    public float pitchHalfWidthZ = 33f;
    public float boundaryBuffer = 4f;

    [Header("Steering & Dynamics")]
    [Tooltip("Smooth rotation slerp factor")]
    public float turnSpeed = 12f;

    // Current tactical state
    public BotState CurrentState { get; private set; } = BotState.ChasingBall;

    // Internal component caches
    private FootballAgent agent;
    private Rigidbody agentRb;
    private Rigidbody ballRb;
    private FootballAgent opponentAgent;
    private Rigidbody opponentRb;

    // Timers & cooldowns
    private float burstTouchCooldown = 0f;
    private float decisionTimer = 0f;
    private Vector3 currentTargetPosition;

    private void Awake()
    {
        agent = GetComponent<FootballAgent>();
        agentRb = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        AutoDiscoverReferences();
    }

    /// <summary>
    /// Intelligently resolves any unassigned scene references so the script works out-of-the-box.
    /// </summary>
    private void AutoDiscoverReferences()
    {
        // 1. Resolve Ball
        if (targetBall == null)
        {
            GameObject ballObj = GameObject.FindGameObjectWithTag("Ball");
            if (ballObj == null)
            {
                ballObj = GameObject.Find("GlowBall");
            }
            if (ballObj != null)
            {
                targetBall = ballObj.transform;
            }
        }
        if (targetBall != null)
        {
            ballRb = targetBall.GetComponent<Rigidbody>();
        }

        // 2. Resolve Opponent
        if (opponent == null)
        {
            FootballAgent[] allAgents = FindObjectsByType<FootballAgent>(FindObjectsSortMode.None);
            foreach (var other in allAgents)
            {
                if (other != agent)
                {
                    opponent = other.transform;
                    opponentAgent = other;
                    opponentRb = other.GetComponent<Rigidbody>();
                    break;
                }
            }
        }
        else if (opponentAgent == null)
        {
            opponentAgent = opponent.GetComponent<FootballAgent>();
            opponentRb = opponent.GetComponent<Rigidbody>();
        }

        // 3. Resolve Opponent Goal and Own Goal
        if (opponentGoal == null || ownGoal == null)
        {
            GoalTrigger[] goals = FindObjectsByType<GoalTrigger>(FindObjectsSortMode.None);
            if (goals.Length >= 2)
            {
                // In this environment:
                // GoalPostBlue is around x = -53.5
                // GoalPostRed is around x = +53.5
                // If our bot started on the negative X side (blue side), opponentGoal is on positive X side (red side)
                float initialX = transform.position.x;
                GoalTrigger goalA = goals[0];
                GoalTrigger goalB = goals[1];

                Transform farGoal = Mathf.Abs(goalA.transform.position.x - initialX) > Mathf.Abs(goalB.transform.position.x - initialX) 
                    ? goalA.transform 
                    : goalB.transform;

                Transform nearGoal = (farGoal == goalA.transform) ? goalB.transform : goalA.transform;

                if (opponentGoal == null) opponentGoal = farGoal;
                if (ownGoal == null) ownGoal = nearGoal;
            }
            else
            {
                // Fallback by name lookup
                GameObject redGoal = GameObject.Find("GoalPostRed");
                GameObject blueGoal = GameObject.Find("GoalPostBlue");

                if (redGoal != null && blueGoal != null)
                {
                    bool isBlueSide = transform.position.x < 0;
                    if (opponentGoal == null) opponentGoal = isBlueSide ? redGoal.transform : blueGoal.transform;
                    if (ownGoal == null) ownGoal = isBlueSide ? blueGoal.transform : redGoal.transform;
                }
            }
        }
    }

    private void FixedUpdate()
    {
        if (burstTouchCooldown > 0f)
        {
            burstTouchCooldown -= Time.fixedDeltaTime;
        }

        if (targetBall == null || opponentGoal == null)
        {
            AutoDiscoverReferences();
            if (targetBall == null || opponentGoal == null) return;
        }

        // Determine current tactical phase
        UpdateBotState();

        // Execute behavior based on current tactical phase
        switch (CurrentState)
        {
            case BotState.AttackingWithBall:
                ExecuteAttackingBehavior();
                break;

            case BotState.DefendingOpponent:
                ExecuteDefendingBehavior();
                break;

            case BotState.ChasingBall:
            default:
                ExecuteChasingBehavior();
                break;
        }
    }

    /// <summary>
    /// Evaluates match dynamics to set current tactical role.
    /// </summary>
    private void UpdateBotState()
    {
        if (agent.HasPossession)
        {
            CurrentState = BotState.AttackingWithBall;
        }
        else if (opponentAgent != null && opponentAgent.HasPossession)
        {
            CurrentState = BotState.DefendingOpponent;
        }
        else
        {
            CurrentState = BotState.ChasingBall;
        }
    }

    // =========================================================================
    // OFFENSIVE BEHAVIOR (WITH BALL POSSESSION)
    // =========================================================================

    private void ExecuteAttackingBehavior()
    {
        Vector3 myPos = transform.position;
        Vector3 goalPos = opponentGoal.position;
        float distToGoal = Vector3.Distance(myPos, goalPos);
        float distToOwnGoal = ownGoal != null ? Vector3.Distance(myPos, ownGoal.position) : 50f;
        float distToOpponent = opponent != null ? Vector3.Distance(myPos, opponent.position) : 999f;

        // 1. DANGER CLEARANCE: If possessing deep in defensive third under immediate opponent press
        if (distToOwnGoal < clearanceDangerRadius && distToOpponent < 6.5f)
        {
            ExecuteDefensiveClearance();
            return;
        }

        // 2. SHOOTING OPPORTUNITY EVALUATION
        if (distToGoal <= maxShootDistance)
        {
            if (TryEvaluateAndExecuteShot(myPos, goalPos, distToGoal, distToOpponent))
            {
                return;
            }
        }

        // 3. BURST / SELF-PASS TOUCH (Escape aggressive tackle from defender)
        if (enableBurstKnockAhead && burstTouchCooldown <= 0f && distToOpponent < burstTriggerDistance && distToGoal > 12f)
        {
            if (TryExecuteBurstTouch(myPos, goalPos))
            {
                burstTouchCooldown = 1.5f;
                return;
            }
        }

        // 4. DRIBBLE & OPPONENT EVASION
        Vector3 moveDirection = CalculateDribbleDirection(myPos, goalPos);
        currentTargetPosition = myPos + moveDirection * 5f;

        // Apply movement forces
        agent.ApplyMovement(new Vector2(moveDirection.x, moveDirection.z));

        // Smoothly orient body towards movement vector or goal alignment
        Vector3 lookDir = new Vector3(moveDirection.x, 0, moveDirection.z).normalized;
        if (lookDir.sqrMagnitude > 0.001f)
        {
            transform.forward = Vector3.Slerp(transform.forward, lookDir, turnSpeed * Time.fixedDeltaTime);
        }
    }

    /// <summary>
    /// Evaluates the goal mouth corners and executes a precision shot (low driven or chipped over defender).
    /// </summary>
    private bool TryEvaluateAndExecuteShot(Vector3 myPos, Vector3 goalPos, float distToGoal, float distToOpponent)
    {
        // Select best target corner on the goal line
        Vector3 bestTarget = EvaluateBestGoalTarget(myPos, goalPos);
        Vector3 toTarget = (bestTarget - myPos).normalized;

        float angleToTarget = Vector3.SignedAngle(transform.forward, toTarget, Vector3.up);

        // Turn body towards the target to align for the kick
        Vector3 desiredFacing = new Vector3(toTarget.x, 0, toTarget.z).normalized;
        transform.forward = Vector3.Slerp(transform.forward, desiredFacing, turnSpeed * 1.5f * Time.fixedDeltaTime);

        // If not aligned enough within kick cone, wait a frame while steering towards it
        if (Mathf.Abs(angleToTarget) > kickAlignmentThreshold)
        {
            // Move gently towards the shot line
            agent.ApplyMovement(new Vector2(desiredFacing.x * 0.7f, desiredFacing.z * 0.7f));
            return true; // Still preparing the shot
        }

        // Calculate passAngle.x in [-1, 1] relative to transform.forward (scaled to 45 deg max in FootballAgent)
        float passAngleX = Mathf.Clamp(angleToTarget / 45f, -1f, 1f);

        // Check if defender is directly in the shooting line to determine elevation
        bool defenderInLane = false;
        if (opponent != null)
        {
            Vector3 myToTarget = bestTarget - myPos;
            Vector3 myToOpp = opponent.position - myPos;
            float proj = Vector3.Dot(myToOpp, myToTarget.normalized);

            if (proj > 0f && proj < distToGoal)
            {
                Vector3 closestPointOnShotLine = myPos + myToTarget.normalized * proj;
                float perpDist = Vector3.Distance(opponent.position, closestPointOnShotLine);
                if (perpDist < 1.8f && proj < 7f)
                {
                    defenderInLane = true;
                }
            }
        }

        // Determine kick height (elevation) & power
        float passAngleY;
        float kickPower;

        if (defenderInLane)
        {
            // Chip over the blocking defender (elevation ~0.38 - 0.48)
            // Power scaled so it clears defender but dips into the goal mouth
            passAngleY = Mathf.Lerp(0.38f, 0.46f, Mathf.Clamp01(distToOpponent / 5f));
            kickPower = Mathf.Lerp(0.65f, 0.95f, Mathf.Clamp01(distToGoal / maxShootDistance));
        }
        else
        {
            // Lane is clear: Low driven finish into the bottom corner
            // Hardest to save, fast ground/low-air velocity, cannot fly over the crossbar (2.44m)
            if (distToGoal < closeRangeShotDistance)
            {
                passAngleY = 0.08f;
                kickPower = 0.82f; // Controlled placement
            }
            else
            {
                // Medium-long range shot: slight rise
                passAngleY = Mathf.Lerp(0.12f, 0.22f, (distToGoal - closeRangeShotDistance) / (maxShootDistance - closeRangeShotDistance));
                kickPower = 1.0f; // Maximum power strike
            }
        }

        // Execute Kick with modulated power and elevation
        agent.ExecuteKick(new Vector2(passAngleX, passAngleY), kickPower);
        return true;
    }

    /// <summary>
    /// Finds the optimal corner of the goal mouth (far-post vs near-post) that minimizes interception risk.
    /// </summary>
    private Vector3 EvaluateBestGoalTarget(Vector3 myPos, Vector3 goalPos)
    {
        float halfW = (goalWidth * 0.5f) - goalCornerInset;

        // Determine the horizontal axis of the goal (assumed along Z axis in this stadium)
        Vector3 leftPost = goalPos + Vector3.forward * halfW;
        Vector3 rightPost = goalPos - Vector3.forward * halfW;
        Vector3 center = goalPos;

        Vector3[] candidates = new Vector3[] { leftPost, rightPost, center };
        Vector3 bestCandidate = center;
        float bestScore = float.MinValue;

        foreach (var target in candidates)
        {
            float score = 0f;

            if (opponent != null)
            {
                // Measure distance from opponent to this shot segment
                Vector3 seg = target - myPos;
                Vector3 toOpp = opponent.position - myPos;
                float t = Mathf.Clamp01(Vector3.Dot(toOpp, seg) / seg.sqrMagnitude);
                Vector3 nearestPoint = myPos + seg * t;
                float oppDist = Vector3.Distance(opponent.position, nearestPoint);

                score += oppDist * 3f; // High weight on open space
            }

            // Prefer corner targets over dead center for finishing
            if (target != center) score += 1.5f;

            // Minor penalty for severe acute angles from current forward
            Vector3 toTarget = (target - myPos).normalized;
            float angle = Vector3.Angle(transform.forward, toTarget);
            score -= (angle / 90f);

            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = target;
            }
        }

        return bestCandidate;
    }

    /// <summary>
    /// Calculates steering vector to progress towards goal while actively bypassing the defender.
    /// </summary>
    private Vector3 CalculateDribbleDirection(Vector3 myPos, Vector3 goalPos)
    {
        Vector3 toGoal = (goalPos - myPos);
        toGoal.y = 0f;
        Vector3 dirToGoal = toGoal.normalized;

        Vector3 avoidanceForce = Vector3.zero;

        if (opponent != null)
        {
            Vector3 toOpp = (opponent.position - myPos);
            toOpp.y = 0f;
            float oppDist = toOpp.magnitude;

            if (oppDist < evasionTriggerDistance)
            {
                // Check if opponent is ahead of us along our goal line
                float forwardProj = Vector3.Dot(toOpp, dirToGoal);
                if (forwardProj > 0.5f)
                {
                    // Calculate lateral distance from path
                    Vector3 lateralOffset = toOpp - dirToGoal * forwardProj;
                    float lateralDist = lateralOffset.magnitude;

                    if (lateralDist < dribbleCorridorWidth)
                    {
                        // Opponent is blocking the path. Calculate left and right perpendicular vectors
                        Vector3 leftNormal = new Vector3(-dirToGoal.z, 0, dirToGoal.x);
                        Vector3 rightNormal = new Vector3(dirToGoal.z, 0, -dirToGoal.x);

                        // Decide which side has more space and is safer from pitch boundaries
                        float leftZ = myPos.z + leftNormal.z * 4f;
                        float rightZ = myPos.z + rightNormal.z * 4f;

                        bool leftInBounds = Mathf.Abs(leftZ) < (pitchHalfWidthZ - boundaryBuffer);
                        bool rightInBounds = Mathf.Abs(rightZ) < (pitchHalfWidthZ - boundaryBuffer);

                        Vector3 chosenNormal;
                        // Steer away from whichever side opponent is leaning towards
                        bool oppIsRight = Vector3.Dot(toOpp, rightNormal) > 0f;

                        if (oppIsRight && leftInBounds)
                        {
                            chosenNormal = leftNormal;
                        }
                        else if (!oppIsRight && rightInBounds)
                        {
                            chosenNormal = rightNormal;
                        }
                        else if (leftInBounds)
                        {
                            chosenNormal = leftNormal;
                        }
                        else
                        {
                            chosenNormal = rightNormal;
                        }

                        float urgency = 1f - Mathf.Clamp01(oppDist / evasionTriggerDistance);
                        avoidanceForce = chosenNormal * (evasionStrength * urgency);
                    }
                }
            }
        }

        // Apply soft pitch boundary repulsion
        Vector3 boundaryRepulsion = CalculateBoundaryRepulsion(myPos);

        Vector3 combined = (dirToGoal + avoidanceForce + boundaryRepulsion).normalized;
        return combined;
    }

    /// <summary>
    /// Executes a soft knock-ahead burst into open space when a defender rushes within tackling range,
    /// allowing the bot to shed the dribble penalty and recover the ball at full unencumbered sprint speed.
    /// </summary>
    private bool TryExecuteBurstTouch(Vector3 myPos, Vector3 goalPos)
    {
        Vector3 toGoal = (goalPos - myPos).normalized;
        Vector3 toOpp = (opponent.position - myPos).normalized;

        // If defender is charging straight at us
        if (Vector3.Dot(toGoal, toOpp) > 0.6f)
        {
            // Knock ball at an angle past the defender
            Vector3 lateral = Vector3.Cross(toGoal, Vector3.up).normalized;
            // Pick side with more room
            if (myPos.z < 0) lateral = -lateral;

            Vector3 burstDir = (toGoal * 0.8f + lateral * 0.6f).normalized;
            float angleOffset = Vector3.SignedAngle(transform.forward, burstDir, Vector3.up);
            float passAngleX = Mathf.Clamp(angleOffset / 45f, -1f, 1f);

            // Light, controlled touch (power ~0.35, low elevation)
            agent.ExecuteKick(new Vector2(passAngleX, 0.12f), 0.38f);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lofts the ball high and far into the opponent's half when trapped under defensive pressure.
    /// </summary>
    private void ExecuteDefensiveClearance()
    {
        Vector3 clearanceTarget = opponentGoal.position;
        // Bias towards wings for safer clearance
        clearanceTarget.z += (transform.position.z > 0 ? 12f : -12f);

        Vector3 toTarget = (clearanceTarget - transform.position).normalized;
        float angle = Vector3.SignedAngle(transform.forward, toTarget, Vector3.up);
        float passAngleX = Mathf.Clamp(angle / 45f, -1f, 1f);

        // High loft (elevation ~0.48) with maximum force (power = 1.0)
        agent.ExecuteKick(new Vector2(passAngleX, 0.48f), 1.0f);
    }

    // =========================================================================
    // DEFENSIVE BEHAVIOR (OPPONENT HAS POSSESSION)
    // =========================================================================

    private void ExecuteDefendingBehavior()
    {
        if (opponent == null || ownGoal == null)
        {
            ExecuteChasingBehavior();
            return;
        }

        Vector3 myPos = transform.position;
        Vector3 oppPos = opponent.position;
        Vector3 myGoalPos = ownGoal.position;

        float distToOpp = Vector3.Distance(myPos, oppPos);
        float distOppToGoal = Vector3.Distance(oppPos, myGoalPos);

        // 1. TACKLE COMMITMENT:
        // In FootballAgent, colliding with player strips possession!
        // We move at max 8 m/s while possessing opponent moves at only 5 m/s.
        // If close enough or opponent is entering the danger zone, sprint straight at opponent to tackle!
        bool shouldCommitTackle = (distToOpp <= tackleCommitDistance) || (distOppToGoal < dangerZoneRadius && distToOpp < 6f);

        Vector3 targetMovePos;

        if (shouldCommitTackle)
        {
            // Intercept opponent's future position using their velocity
            Vector3 oppVel = opponentRb != null ? opponentRb.linearVelocity : Vector3.zero;
            targetMovePos = oppPos + new Vector3(oppVel.x, 0, oppVel.z) * 0.25f;
        }
        else
        {
            // 2. GOAL-SIDE JOCKEYING:
            // Stand between the opponent and our own goal to block shooting and direct runs
            Vector3 oppToGoalDir = (myGoalPos - oppPos).normalized;
            // Position in the passing/shooting lane, roughly jockeyDistance ahead of opponent
            targetMovePos = oppPos + oppToGoalDir * jockeyDistance;

            // Ensure jockey position doesn't retreat behind our own goal line
            float minAllowedX = myGoalPos.x > 0 ? (myGoalPos.x - 3f) : (myGoalPos.x + 3f);
            if (myGoalPos.x < 0 && targetMovePos.x < minAllowedX) targetMovePos.x = minAllowedX;
            if (myGoalPos.x > 0 && targetMovePos.x > minAllowedX) targetMovePos.x = minAllowedX;
        }

        currentTargetPosition = targetMovePos;

        // Apply movement
        Vector3 moveDir = (targetMovePos - myPos);
        moveDir.y = 0f;
        Vector3 moveDirNorm = moveDir.normalized;

        agent.ApplyMovement(new Vector2(moveDirNorm.x, moveDirNorm.z));

        // Face the opponent to be ready to react and block shots
        Vector3 faceDir = (oppPos - myPos);
        faceDir.y = 0f;
        if (faceDir.sqrMagnitude > 0.01f)
        {
            transform.forward = Vector3.Slerp(transform.forward, faceDir.normalized, turnSpeed * Time.fixedDeltaTime);
        }
    }

    // =========================================================================
    // LOOSE BALL PURSUIT & INTERCEPTION
    // =========================================================================

    private void ExecuteChasingBehavior()
    {
        Vector3 myPos = transform.position;
        Vector3 ballPos = targetBall.position;

        // 1. Trajectory and landing point prediction
        Vector3 interceptPoint = CalculateBallInterceptionPoint(myPos);
        currentTargetPosition = interceptPoint;

        // 2. Goal-Side approach curvature:
        // If the ball is loose near our own defensive area, avoid approaching from behind towards own goal
        Vector3 moveDir = (interceptPoint - myPos);
        moveDir.y = 0f;

        if (ownGoal != null)
        {
            float distBallToOwnGoal = Vector3.Distance(ballPos, ownGoal.position);
            if (distBallToOwnGoal < dangerZoneRadius)
            {
                // Ensure we approach the ball from the defensive side (facing upfield)
                Vector3 defensiveBias = (ballPos - ownGoal.position).normalized * 1.5f;
                Vector3 safeApproachPoint = ballPos + defensiveBias;
                if (Vector3.Distance(myPos, safeApproachPoint) > 2.0f && Vector3.Dot(moveDir.normalized, (ownGoal.position - myPos).normalized) > 0.3f)
                {
                    moveDir = (safeApproachPoint - myPos);
                    moveDir.y = 0f;
                }
            }
        }

        Vector3 moveDirNorm = moveDir.normalized;

        // Apply movement towards predicted intercept point
        agent.ApplyMovement(new Vector2(moveDirNorm.x, moveDirNorm.z));

        // Smoothly orient facing towards ball or intercept point
        if (moveDirNorm.sqrMagnitude > 0.001f)
        {
            transform.forward = Vector3.Slerp(transform.forward, moveDirNorm, turnSpeed * Time.fixedDeltaTime);
        }
    }

    /// <summary>
    /// Predicts where the ball will be when the agent can reach it,
    /// accounting for ball parabolic flight (gravity) and ground rolling.
    /// </summary>
    private Vector3 CalculateBallInterceptionPoint(Vector3 myPos)
    {
        if (ballRb == null) return targetBall.position;

        Vector3 ballPos = targetBall.position;
        Vector3 ballVel = ballRb.linearVelocity;

        // If ball is airborne (y > 1.2m or moving upward), calculate landing spot
        if (ballPos.y > 1.2f || ballVel.y > 1.0f)
        {
            float g = Mathf.Abs(Physics.gravity.y);
            if (g < 0.1f) g = 9.81f;

            // Solve y(t) = y0 + vy*t - 0.5*g*t^2 = 0.5 (ground level)
            float vy = ballVel.y;
            float y0 = ballPos.y;
            float targetY = 0.5f;

            float discriminant = vy * vy + 2f * g * (y0 - targetY);
            if (discriminant > 0f)
            {
                float tLand = (vy + Mathf.Sqrt(discriminant)) / g;
                if (tLand > 0.05f)
                {
                    Vector3 landingPoint = new Vector3(
                        ballPos.x + ballVel.x * tLand,
                        targetY,
                        ballPos.z + ballVel.z * tLand
                    );
                    return ClampToPitch(landingPoint);
                }
            }
        }

        // For ground rolling / low bounce: iterate to find earliest reachable interception time
        float maxSpeed = agent.maxVelocityBase; // 8.0 m/s unencumbered
        float bestTime = 0.1f;
        Vector3 bestPoint = ballPos;

        // Sample up to 2.0 seconds into the future
        for (float t = 0.1f; t <= 2.0f; t += 0.15f)
        {
            // Simple drag decay approximation
            Vector3 predictedBallPos = ballPos + new Vector3(ballVel.x, 0, ballVel.z) * t * Mathf.Exp(-0.1f * t);
            predictedBallPos.y = 0.5f;

            float distanceToPoint = Vector3.Distance(new Vector3(myPos.x, 0, myPos.z), new Vector3(predictedBallPos.x, 0, predictedBallPos.z));
            float agentTravelDistance = maxSpeed * t;

            if (agentTravelDistance >= distanceToPoint)
            {
                bestPoint = predictedBallPos;
                bestTime = t;
                break;
            }
        }

        return ClampToPitch(bestPoint);
    }

    /// <summary>
    /// Soft pitch boundary repulsion so the bot stays comfortably within active bounds.
    /// </summary>
    private Vector3 CalculateBoundaryRepulsion(Vector3 pos)
    {
        Vector3 repulsion = Vector3.zero;

        // Z-axis (sidelines)
        if (pos.z > (pitchHalfWidthZ - boundaryBuffer))
        {
            float excess = pos.z - (pitchHalfWidthZ - boundaryBuffer);
            repulsion.z -= (excess / boundaryBuffer) * 2.0f;
        }
        else if (pos.z < -(pitchHalfWidthZ - boundaryBuffer))
        {
            float excess = -pos.z - (pitchHalfWidthZ - boundaryBuffer);
            repulsion.z += (excess / boundaryBuffer) * 2.0f;
        }

        // X-axis (end lines)
        if (pos.x > (pitchHalfLengthX - boundaryBuffer))
        {
            float excess = pos.x - (pitchHalfLengthX - boundaryBuffer);
            repulsion.x -= (excess / boundaryBuffer) * 2.0f;
        }
        else if (pos.x < -(pitchHalfLengthX - boundaryBuffer))
        {
            float excess = -pos.x - (pitchHalfLengthX - boundaryBuffer);
            repulsion.x += (excess / boundaryBuffer) * 2.0f;
        }

        return repulsion;
    }

    private Vector3 ClampToPitch(Vector3 pos)
    {
        pos.x = Mathf.Clamp(pos.x, -pitchHalfLengthX + 1f, pitchHalfLengthX - 1f);
        pos.z = Mathf.Clamp(pos.z, -pitchHalfWidthZ + 1f, pitchHalfWidthZ - 1f);
        return pos;
    }

    // =========================================================================
    // DEBUG VISUALIZATION (SCENE VIEW GIZMOS)
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        Vector3 myPos = transform.position;

        // Draw tactical state target
        Gizmos.color = CurrentState switch
        {
            BotState.AttackingWithBall => Color.green,
            BotState.DefendingOpponent => Color.red,
            _ => Color.cyan
        };
        Gizmos.DrawWireSphere(currentTargetPosition, 0.6f);
        Gizmos.DrawLine(myPos, currentTargetPosition);

        // Draw goal mouth corners if opponent goal exists
        if (opponentGoal != null)
        {
            float halfW = (goalWidth * 0.5f) - goalCornerInset;
            Vector3 leftPost = opponentGoal.position + Vector3.forward * halfW;
            Vector3 rightPost = opponentGoal.position - Vector3.forward * halfW;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(leftPost, 0.5f);
            Gizmos.DrawWireSphere(rightPost, 0.5f);
            Gizmos.DrawLine(leftPost, rightPost);
        }

        // Draw pitch boundary limits
        Gizmos.color = new Color(1f, 1f, 1f, 0.2f);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(pitchHalfLengthX * 2f, 1f, pitchHalfWidthZ * 2f));
    }
}
