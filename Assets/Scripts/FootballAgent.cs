using UnityEngine;

/// <summary>
/// Implements pure-physics agent locomotion and discrete tap-and-chase dribbling:
/// - Discrete push-and-run dribble: Agent rhythmically taps the ball ahead and sprints after it
/// - Dynamic stamina/velocity decay across the entire possession lifecycle:
///   V_max(t) = max(V_min, V_base - kappa - alpha * t_possess)
/// - Ball has natural linear damping/drag (configured in Unity Rigidbody)
/// - Parameterized 2D power kicks: A_pass_angle in [-1, 1]^2
/// - Zero kinematic locks: The ball is 100% dynamic PhysX at all times
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class FootballAgent : MonoBehaviour
{
    [Header("Locomotion Dynamics (proposal.md Section 3.3)")]
    [Tooltip("Base unencumbered maximum speed (V_base).")]
    public float maxVelocityBase = 8f;

    [Tooltip("Acceleration rate in m/s^2 for athletic responsiveness.")]
    public float accelerationRate = 35f;

    [Tooltip("Active braking deceleration in m/s^2 when stopping or reversing (cleat friction).")]
    public float brakingRate = 50f;

    [Tooltip("Rotation speed in degrees per second for smooth 360-degree heading alignment.")]
    public float turnSpeed = 720f;

    [Header("Dribble Fatigue Decay (proposal.md Section 3.3)")]
    [Tooltip("Initial speed reduction penalty when taking possession (kappa).")]
    public float dribblePenaltyBase = 2f;

    [Tooltip("Continuous velocity decay rate per second of ball retention (alpha).")]
    public float possessionDecayRate = 0.8f;

    [Tooltip("Minimum allowable maximum velocity when heavily encumbered (V_min).")]
    public float minPossessionVelocity = 2.5f;

    [Header("Discrete Tap-and-Chase Dribble")]
    [Tooltip("Maximum reach distance to the ball for executing a dribble tap.")]
    public float dribbleReach = 1.5f;

    [Tooltip("Discrete impulse force applied to tap the ball ahead.")]
    public float dribbleTapForce = 5.0f;

    [Tooltip("Minimum time cadence in seconds between consecutive dribble taps.")]
    public float dribbleCadence = 0.35f;

    [Tooltip("How directly the agent must face AND run into the ball for a dribble tap (cosine; 0.8 = within ~37 degrees). Prevents sideways taps while walking around the ball.")]
    [Range(0f, 1f)]
    public float tapAlignment = 0.8f;

    [Tooltip("Maximum distance the ball can roll ahead before possession is considered lost.")]
    public float looseBallDistance = 3.5f;

    [Tooltip("Timeout in seconds without touching the ball before possession is forfeited.")]
    public float possessionTimeout = 1.5f;

    [Header("Power Kick Action Space (proposal.md Section 3.1)")]
    [Tooltip("Distance threshold within which the agent can execute a power kick.")]
    public float kickRadius = 1.8f;

    [Tooltip("Maximum impulse force for power kicks/shots.")]
    public float maxKickForce = 22f;

    [Tooltip("Minimum impulse force for soft passes.")]
    public float minKickForce = 6f;

    [Tooltip("Maximum horizontal deviation angle in degrees for pass_angle.x in [-1, 1].")]
    public float maxHorizontalAngle = 45f;

    [Tooltip("Maximum vertical elevation angle in degrees for pass_angle.y in [0, 1].")]
    public float maxElevationAngle = 35f;

    [Header("Ball Reference")]
    public Rigidbody ballRb;

    [Header("State")]
    public bool HasPossession { get; private set; }
    public float CurrentPossessionTime { get; private set; }
    public float CurrentMaxVelocity { get; private set; }

    // Static global carrier tracker ensures single-owner possession across all bots
    public static FootballAgent activeBallCarrier = null;

    private Rigidbody agentRb;
    private Vector2 currentMoveInput = Vector2.zero;
    private float dribbleCooldown = 0f;
    private float powerKickCooldown = 0f;
    private float timeSinceLastTouch = 999f;

    private void Awake()
    {
        agentRb = GetComponent<Rigidbody>();

        // Lock vertical position and tilt so the agent stays firmly grounded on the pitch
        agentRb.constraints = RigidbodyConstraints.FreezePositionY | 
                              RigidbodyConstraints.FreezeRotationX | 
                              RigidbodyConstraints.FreezeRotationZ;

        CurrentMaxVelocity = maxVelocityBase;

        FindBallReference();
    }

    private void Start()
    {
        FindBallReference();
    }

    private void FindBallReference()
    {
        if (ballRb == null)
        {
            GameObject bObj = GameObject.FindWithTag("Ball");
            if (bObj == null) bObj = GameObject.Find("GlowBall");
            if (bObj != null) ballRb = bObj.GetComponent<Rigidbody>();
        }
    }

    /// <summary>
    /// Applies continuous movement vector in [-1, 1]^2 as defined in proposal.md Section 3.1.
    /// Provides full 360-degree omnidirectional movement and analog speed modulation.
    /// </summary>
    public void ApplyMovement(Vector2 moveInput)
    {
        currentMoveInput = moveInput;
    }

    private void Update()
    {
        if (dribbleCooldown > 0f) dribbleCooldown -= Time.deltaTime;
        if (powerKickCooldown > 0f) powerKickCooldown -= Time.deltaTime;
        timeSinceLastTouch += Time.deltaTime;
    }

    private void FixedUpdate()
    {
        FindBallReference();

        // 1. Evaluate Dynamic Possession & Dribble Fatigue Decay
        EvaluatePossessionState();

        // 2. Full 360-Degree Continuous Locomotion with Athletic Turf Traction
        PerformLocomotion();

        // 3. Discrete Tap-and-Chase Dribble Execution
        PerformDribbleTap();
    }

    /// <summary>
    /// Evaluates possession under the discrete tap-and-chase paradigm.
    /// An agent retains possession while the ball is rolling ahead, as long as it stays
    /// within looseBallDistance and hasn't timed out or been intercepted by an opponent.
    /// </summary>
    private void EvaluatePossessionState()
    {
        if (ballRb == null)
        {
            SetPossession(false);
            return;
        }

        float distToBall = Vector3.Distance(transform.position, ballRb.position);

        // If this agent is the active carrier
        if (activeBallCarrier == this)
        {
            // Forfeit possession if ball escapes too far or timeout expires
            if (distToBall > looseBallDistance || timeSinceLastTouch > possessionTimeout)
            {
                activeBallCarrier = null;
                SetPossession(false);
                return;
            }

            // Still in possession during tap-and-chase!
            SetPossession(true);
        }
        else
        {
            SetPossession(false);
        }
    }

    private void SetPossession(bool hasIt)
    {
        HasPossession = hasIt;

        if (HasPossession)
        {
            CurrentPossessionTime += Time.fixedDeltaTime;

            // Mathematical decay: V_max(t) = max(V_min, V_base - kappa - alpha * t_possess)
            float penalizedV = maxVelocityBase - dribblePenaltyBase - (possessionDecayRate * CurrentPossessionTime);
            CurrentMaxVelocity = Mathf.Max(minPossessionVelocity, penalizedV);
        }
        else
        {
            CurrentPossessionTime = 0f;
            CurrentMaxVelocity = maxVelocityBase;
        }
    }

    private void PerformLocomotion()
    {
        float inputMag = Mathf.Clamp01(currentMoveInput.magnitude);
        Vector3 currentHorizVel = new Vector3(agentRb.linearVelocity.x, 0f, agentRb.linearVelocity.z);

        if (inputMag > 0.05f)
        {
            // Continuous 360-degree movement direction vector
            Vector3 moveDir = new Vector3(currentMoveInput.x, 0f, currentMoveInput.y).normalized;
            float targetSpeed = inputMag * CurrentMaxVelocity;
            Vector3 targetVel = moveDir * targetSpeed;

            // Smooth 360-degree heading rotation via Rigidbody.MoveRotation (PhysX compliant)
            Quaternion targetRot = Quaternion.LookRotation(moveDir, Vector3.up);
            Quaternion newRot = Quaternion.RotateTowards(agentRb.rotation, targetRot, turnSpeed * Time.fixedDeltaTime);
            agentRb.MoveRotation(newRot);

            // Turf traction: accelerate or apply hard cleat braking when cutting back/reversing
            bool isCuttingBack = currentHorizVel.sqrMagnitude > 0.1f && Vector3.Dot(currentHorizVel.normalized, moveDir) < 0f;
            float rate = isCuttingBack ? brakingRate : accelerationRate;
            Vector3 newHorizVel = Vector3.MoveTowards(currentHorizVel, targetVel, rate * Time.fixedDeltaTime);
            agentRb.linearVelocity = new Vector3(newHorizVel.x, 0f, newHorizVel.z);
        }
        else
        {
            // Active braking to stop cleanly without ice-skating
            Vector3 stoppedVel = Vector3.MoveTowards(currentHorizVel, Vector3.zero, brakingRate * Time.fixedDeltaTime);
            agentRb.linearVelocity = new Vector3(stoppedVel.x, 0f, stoppedVel.z);
        }
    }

    /// <summary>
    /// Discrete Tap-and-Chase: When running near the ball, the agent delivers a punchy discrete
    /// tap that sends the ball rolling ahead. The ball decelerates via Unity linear damping (drag),
    /// and the agent sprints to catch up before tapping it again.
    /// </summary>
    private void PerformDribbleTap()
    {
        if (ballRb == null || dribbleCooldown > 0f || powerKickCooldown > 0f) return;

        Vector3 toBall = ballRb.position - transform.position;
        toBall.y = 0f;
        float dist = toBall.magnitude;

        // Player must be within foot reach to interact with the ball
        if (dist > dribbleReach) return;

        // Ball must be squarely in front of where the player is facing
        if (toBall.sqrMagnitude < 0.0001f) return;
        Vector3 toBallDir = toBall / dist;
        if (Vector3.Dot(transform.forward, toBallDir) < tapAlignment) return;

        if (currentMoveInput.sqrMagnitude > 0.05f)
        {
            Vector3 moveDir = new Vector3(currentMoveInput.x, 0f, currentMoveInput.y).normalized;

            // Player must be running INTO the ball (not past it or around it)
            if (Vector3.Dot(moveDir, toBallDir) < tapAlignment) return;

            dribbleCooldown = dribbleCadence;
            timeSinceLastTouch = 0f;

            if (activeBallCarrier != null && activeBallCarrier != this)
            {
                activeBallCarrier.SetPossession(false);
            }
            activeBallCarrier = this;
            SetPossession(true);

            // Calculate tap impulse: enough to pop the ball 1.2 to 2 meters ahead
            float playerSpeed = new Vector3(agentRb.linearVelocity.x, 0f, agentRb.linearVelocity.z).magnitude;
            float finalTapImpulse = Mathf.Max(dribbleTapForce, playerSpeed * 0.8f);

            // KICK IN THE FORWARD DIRECTION (transform.forward)
            ballRb.linearVelocity = new Vector3(0f, ballRb.linearVelocity.y, 0f);
            ballRb.AddForce(transform.forward * finalTapImpulse, ForceMode.Impulse);
        }
        else if (currentMoveInput.sqrMagnitude <= 0.05f && dist <= dribbleReach * 0.8f)
        {
            // Player is standing right over the ball and standing still: gently trap the ball
            Vector3 horizBallVel = new Vector3(ballRb.linearVelocity.x, 0f, ballRb.linearVelocity.z);
            Vector3 trappedVel = Vector3.MoveTowards(horizBallVel, Vector3.zero, 12f * Time.fixedDeltaTime);
            ballRb.linearVelocity = new Vector3(trappedVel.x, ballRb.linearVelocity.y, trappedVel.z);
            timeSinceLastTouch = 0f;

            if (activeBallCarrier != null && activeBallCarrier != this)
            {
                activeBallCarrier.SetPossession(false);
            }
            activeBallCarrier = this;
            SetPossession(true);
        }
    }

    /// <summary>
    /// Explicitly kicks the ball in the agent's forward direction.
    /// Used by AI brains or controllers.
    /// </summary>
    public bool KickForward(float force = 5.0f)
    {
        if (ballRb == null || dribbleCooldown > 0f || powerKickCooldown > 0f) return false;

        Vector3 toBall = ballRb.position - transform.position;
        toBall.y = 0f;
        if (toBall.magnitude > dribbleReach) return false;

        // Ball must be in front of the agent
        if (toBall.sqrMagnitude > 0.01f && Vector3.Dot(transform.forward, toBall.normalized) < tapAlignment) return false;

        dribbleCooldown = dribbleCadence;
        timeSinceLastTouch = 0f;

        if (activeBallCarrier != null && activeBallCarrier != this)
        {
            activeBallCarrier.SetPossession(false);
        }
        activeBallCarrier = this;
        SetPossession(true);

        ballRb.linearVelocity = new Vector3(0f, ballRb.linearVelocity.y, 0f);
        ballRb.AddForce(transform.forward * force, ForceMode.Impulse);
        return true;
    }

    /// <summary>
    /// Checks whether the ball is within reach for a power kick.
    /// </summary>
    public bool CanKickBall()
    {
        if (ballRb == null || powerKickCooldown > 0f) return false;
        Vector3 toBall = ballRb.position - transform.position;
        toBall.y = 0f;
        return toBall.magnitude <= kickRadius;
    }

    /// <summary>
    /// Executes a power pass or shot adhering to proposal.md Section 3.1:
    /// passAngle.x in [-1, 1] -> Horizontal angle deviation [-maxHorizontalAngle, +maxHorizontalAngle]
    /// passAngle.y in [0, 1]  -> Vertical elevation angle [0, maxElevationAngle]
    /// forceFraction in [0, 1] -> Interpolates between minKickForce and maxKickForce
    /// </summary>
    public void ExecuteKick(Vector2 passAngle, float forceFraction = 1f)
    {
        if (!CanKickBall()) return;

        powerKickCooldown = 0.45f;
        dribbleCooldown = 0.5f;
        timeSinceLastTouch = 999f;

        if (activeBallCarrier == this)
        {
            activeBallCarrier = null;
        }
        SetPossession(false);

        // 1. Horizontal direction deviation: passAngle.x in [-1, 1]
        float horizAngle = Mathf.Clamp(passAngle.x, -1f, 1f) * maxHorizontalAngle;
        Vector3 horizDir = Quaternion.Euler(0f, horizAngle, 0f) * transform.forward;

        // 2. Vertical elevation angle: passAngle.y in [0, 1]
        float elevationAngle = Mathf.Clamp01(passAngle.y) * maxElevationAngle;
        Vector3 rightAxis = Vector3.Cross(Vector3.up, horizDir).normalized;
        Vector3 trajectory = Quaternion.AngleAxis(-elevationAngle, rightAxis) * horizDir;

        // 3. Force calculation
        float kickForce = Mathf.Lerp(minKickForce, maxKickForce, Mathf.Clamp01(forceFraction));
        ballRb.linearVelocity = Vector3.zero; // Clear previous velocity for a crisp, punchy kick
        ballRb.AddForce(trajectory.normalized * kickForce, ForceMode.Impulse);
    }

    /// <summary>
    /// High-level utility for AI brains to kick towards a target world position.
    /// </summary>
    public void KickTowards(Vector3 targetWorldPos, float elevation = 0.15f, float forceFraction = 1f)
    {
        if (!CanKickBall()) return;

        Vector3 toTarget = targetWorldPos - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.01f) return;

        float signedAngle = Vector3.SignedAngle(transform.forward, toTarget.normalized, Vector3.up);
        float normalizedX = Mathf.Clamp(signedAngle / maxHorizontalAngle, -1f, 1f);

        ExecuteKick(new Vector2(normalizedX, elevation), forceFraction);
    }

    public static void ClearGlobalPossession()
    {
        activeBallCarrier = null;
    }

    public void ReleasePossession()
    {
        if (activeBallCarrier == this) activeBallCarrier = null;
        SetPossession(false);
    }

    public void LosePossession() => ReleasePossession();
    public void SetPossessionCooldown(float d) { powerKickCooldown = Mathf.Max(powerKickCooldown, d); }
}