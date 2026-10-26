using UnityEngine;

/// <summary>
/// Implements pure-physics agent locomotion and ball mechanics formulated in proposal.md:
/// - Continuous 2D force movement: A_move in [-1, 1]^2 with athletic turf traction
/// - Pure physics dribbling: Soft micro-kicks keep the ball rolling naturally with the agent
/// - Zero kinematic locks: The ball is 100% dynamic PhysX at all times (no child-snapping)
/// - Dribble velocity decay: V_max(t) = max(V_min, V_base - kappa - alpha * t_possess)
/// - Parameterized 2D power kicks: A_pass_angle in [-1, 1]^2
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

    [Header("Pure-Physics Dribble Mechanics")]
    [Tooltip("Distance threshold from agent center to ball center for dribble micro-kicks.")]
    public float dribbleRadius = 1.6f;

    [Tooltip("Strength of the soft rolling micro-nudge applied to keep ball with player.")]
    public float dribbleNudgeStrength = 25f;

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

    private Rigidbody agentRb;
    private Vector2 currentMoveInput = Vector2.zero;
    private float kickCooldown = 0f;

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
        if (kickCooldown > 0f)
        {
            kickCooldown -= Time.deltaTime;
        }
    }

    private void FixedUpdate()
    {
        FindBallReference();

        // 1. Evaluate Dynamic Possession & Dribble Fatigue Decay (proposal.md Section 3.3)
        EvaluatePossession();

        // 2. Full 360-Degree Continuous Locomotion with Athletic Turf Traction
        PerformLocomotion();

        // 3. Pure Physics Dribble Micro-Kicks (Soft Nudges)
        PerformDribbleNudge();
    }

    private void EvaluatePossession()
    {
        if (ballRb == null)
        {
            HasPossession = false;
            CurrentPossessionTime = 0f;
            CurrentMaxVelocity = maxVelocityBase;
            return;
        }

        Vector3 toBall = ballRb.position - transform.position;
        toBall.y = 0f;
        float dist = toBall.magnitude;
        float forwardAlignment = toBall.sqrMagnitude > 0.01f ? Vector3.Dot(transform.forward, toBall.normalized) : 0f;

        // An agent has possession if ball is within dribble radius, in front of the agent, and not in kick cooldown
        bool inControl = dist <= dribbleRadius && forwardAlignment > 0.1f && kickCooldown <= 0f;

        if (inControl)
        {
            HasPossession = true;
            CurrentPossessionTime += Time.fixedDeltaTime;

            // Mathematical decay: V_max(t) = max(V_min, V_base - kappa - alpha * t_possess)
            float penalizedV = maxVelocityBase - dribblePenaltyBase - (possessionDecayRate * CurrentPossessionTime);
            CurrentMaxVelocity = Mathf.Max(minPossessionVelocity, penalizedV);
        }
        else
        {
            HasPossession = false;
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
    /// Pure Physics Dribble: When running near the ball, the player's feet softly nudge the ball
    /// forward so it rolls naturally with the player, eliminating any kinematic holding.
    /// </summary>
    private void PerformDribbleNudge()
    {
        if (!HasPossession || ballRb == null || kickCooldown > 0f) return;

        // If the agent is actively moving, nudge the ball forward at slightly higher speed than the agent
        if (currentMoveInput.sqrMagnitude > 0.01f)
        {
            Vector3 desiredNudgeDir = transform.forward;
            float agentSpeed = new Vector3(agentRb.linearVelocity.x, 0f, agentRb.linearVelocity.z).magnitude;
            float targetBallSpeed = Mathf.Max(agentSpeed + 0.8f, 2.5f);

            Vector3 targetBallVel = desiredNudgeDir * targetBallSpeed;
            targetBallVel.y = ballRb.linearVelocity.y; // Keep natural gravity intact

            // Gently adjust ball velocity toward desired forward roll
            ballRb.linearVelocity = Vector3.MoveTowards(ballRb.linearVelocity, targetBallVel, dribbleNudgeStrength * Time.fixedDeltaTime);
        }
    }

    /// <summary>
    /// Checks whether the ball is within reach for a power kick.
    /// </summary>
    public bool CanKickBall()
    {
        if (ballRb == null || kickCooldown > 0f) return false;
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

        kickCooldown = 0.3f; // Short cooldown so dribble nudge doesn't immediately overwrite the big kick
        HasPossession = false;
        CurrentPossessionTime = 0f;

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
    /// Calculates the required passAngle relative to agent forward.
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

    public void SetCooldown(float duration)
    {
        kickCooldown = Mathf.Max(kickCooldown, duration);
    }

    // Retained for backward compatibility with external scripts
    public void ReleasePossession() => HasPossession = false;
    public void LosePossession() => HasPossession = false;
    public void SetPossessionCooldown(float d) => SetCooldown(d);
}