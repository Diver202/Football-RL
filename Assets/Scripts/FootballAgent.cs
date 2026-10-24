using UnityEngine;

/// <summary>
/// Implements the physical agent capabilities formulated in proposal.md:
/// - Continuous 2D force movement: A_move in [-1, 1]^2
/// - Possession dynamics with decaying maximum velocity:
///   V_max(t) = max(V_min, V_base - kappa - alpha * t_possess)
/// - Parameterized 2D kick action space: A_pass_angle in [-1, 1]^2
/// - Automatic possession release and boundary buffer protection near barriers
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class FootballAgent : MonoBehaviour
{
    [Header("Movement Dynamics (proposal.md Section 3.3)")]
    [Tooltip("Base unencumbered maximum speed (V_base).")]
    public float maxVelocityBase = 8f;

    [Tooltip("Acceleration rate in m/s^2 for athletic responsiveness.")]
    public float accelerationRate = 35f;

    [Tooltip("Active braking deceleration in m/s^2 when stopping or reversing (cleat friction).")]
    public float brakingRate = 50f;

    [Tooltip("Rotation speed in degrees per second for smooth 360-degree heading alignment.")]
    public float turnSpeed = 720f;

    [Tooltip("Initial speed reduction penalty when taking possession (kappa).")]
    public float dribblePenaltyBase = 2f;

    [Tooltip("Continuous velocity decay rate per second of ball retention (alpha).")]
    public float possessionDecayRate = 0.8f;

    [Tooltip("Minimum allowable maximum velocity when heavily encumbered (V_min).")]
    public float minPossessionVelocity = 2.5f;

    [Header("Ball Holding Geometry")]
    [Tooltip("Forward offset from agent center to ball center.")]
    public float dribbleOffset = 0.85f;

    [Tooltip("Fixed vertical height of ball above turf while possessed.")]
    public float ballGroundHeight = 0.15f;

    [Header("Kicking Action Space (proposal.md Section 3.1)")]
    [Tooltip("Maximum impulse force for kicks/passes.")]
    public float maxKickForce = 22f;

    [Tooltip("Minimum impulse force for soft passes.")]
    public float minKickForce = 6f;

    [Tooltip("Maximum horizontal deviation angle in degrees for pass_angle.x in [-1, 1].")]
    public float maxHorizontalAngle = 45f;

    [Tooltip("Maximum vertical elevation angle in degrees for pass_angle.y in [0, 1].")]
    public float maxElevationAngle = 35f;

    [Header("Boundary Buffer")]
    [Tooltip("Half-length boundary threshold where agent automatically releases ball.")]
    public float pitchBoundaryX = 51f;

    [Tooltip("Half-width boundary threshold where agent automatically releases ball.")]
    public float pitchBoundaryZ = 34f;

    [Header("State")]
    public bool HasPossession { get; private set; }
    public float CurrentPossessionTime { get; private set; }
    public float CurrentMaxVelocity { get; private set; }
    public GameObject currentBall;

    private Rigidbody agentRb;
    private Collider agentCollider;
    private Rigidbody ballRb;
    private Collider ballCollider;
    private float possessionCooldown = 0f;
    private Vector2 currentMoveInput = Vector2.zero;

    private void Awake()
    {
        agentRb = GetComponent<Rigidbody>();
        agentCollider = GetComponent<Collider>();

        // Lock vertical position and tilt so the agent stays firmly grounded on the pitch
        agentRb.constraints = RigidbodyConstraints.FreezePositionY | 
                              RigidbodyConstraints.FreezeRotationX | 
                              RigidbodyConstraints.FreezeRotationZ;

        CurrentMaxVelocity = maxVelocityBase;
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
        if (possessionCooldown > 0f)
        {
            possessionCooldown -= Time.deltaTime;
        }
    }

    private void FixedUpdate()
    {
        // 1. Calculate dynamic max speed decay based on possession time (proposal.md Section 3.3)
        if (HasPossession)
        {
            CurrentPossessionTime += Time.fixedDeltaTime;
            // V_max(t) = max(V_min, V_base - kappa - alpha * t_possess)
            float penalizedV = maxVelocityBase - dribblePenaltyBase - (possessionDecayRate * CurrentPossessionTime);
            CurrentMaxVelocity = Mathf.Max(minPossessionVelocity, penalizedV);
        }
        else
        {
            CurrentPossessionTime = 0f;
            CurrentMaxVelocity = maxVelocityBase;
        }

        // 2. Full 360-Degree Continuous Locomotion with Athletic Turf Traction
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

        // 3. Synchronize Possessed Ball in FixedUpdate (Eliminates Jitter & Desync)
        if (HasPossession && currentBall != null)
        {
            Vector3 frontPos = transform.position + transform.forward * dribbleOffset;
            frontPos.y = ballGroundHeight;
            currentBall.transform.position = frontPos;

            // Boundary protection: release possession if nearing barriers
            if (Mathf.Abs(frontPos.x) > pitchBoundaryX || Mathf.Abs(frontPos.z) > pitchBoundaryZ)
            {
                ReleaseNearBarrier(frontPos);
            }
        }
    }

    /// <summary>
    /// Takes possession of the ball: disables collision with agent, sets kinematic, and locks to front.
    /// </summary>
    public void TakePossession(GameObject ball)
    {
        if (possessionCooldown > 0f || HasPossession) return;

        HasPossession = true;
        CurrentPossessionTime = 0f;
        currentBall = ball;
        ballRb = ball.GetComponent<Rigidbody>();
        ballCollider = ball.GetComponent<Collider>();

        if (ballRb != null)
        {
            ballRb.isKinematic = true;
            ballRb.linearVelocity = Vector3.zero;
            ballRb.angularVelocity = Vector3.zero;
        }

        if (agentCollider != null && ballCollider != null)
        {
            Physics.IgnoreCollision(agentCollider, ballCollider, true);
        }

        Vector3 frontPos = transform.position + transform.forward * dribbleOffset;
        frontPos.y = ballGroundHeight;
        currentBall.transform.position = frontPos;
    }

    /// <summary>
    /// Releases possession and restores normal ball physics.
    /// </summary>
    public void ReleasePossession()
    {
        LosePossession();
    }

    /// <summary>
    /// Releases possession and restores normal ball physics.
    /// </summary>
    public void LosePossession()
    {
        if (!HasPossession && currentBall == null) return;
        HasPossession = false;
        CurrentPossessionTime = 0f;

        if (currentBall != null)
        {
            if (ballRb != null)
            {
                ballRb.isKinematic = false;
            }

            if (agentCollider != null && ballCollider != null)
            {
                Physics.IgnoreCollision(agentCollider, ballCollider, false);
            }
        }

        currentBall = null;
        ballRb = null;
        ballCollider = null;
    }

    /// <summary>
    /// Executes a pass or shot adhering to proposal.md Section 3.1:
    /// passAngle.x in [-1, 1] -> Horizontal angle deviation [-maxHorizontalAngle, +maxHorizontalAngle]
    /// passAngle.y in [0, 1]  -> Vertical elevation angle [0, maxElevationAngle]
    /// forceFraction in [0, 1] -> Interpolates between minKickForce and maxKickForce
    /// </summary>
    public void ExecuteKick(Vector2 passAngle, float forceFraction = 1f)
    {
        if (!HasPossession || currentBall == null) return;

        Rigidbody bRb = ballRb;
        LosePossession();
        possessionCooldown = 0.5f;

        if (bRb == null) return;

        bRb.isKinematic = false;
        bRb.linearVelocity = Vector3.zero;
        bRb.angularVelocity = Vector3.zero;

        // 1. Horizontal direction deviation: passAngle.x in [-1, 1]
        float horizAngle = Mathf.Clamp(passAngle.x, -1f, 1f) * maxHorizontalAngle;
        Vector3 horizDir = Quaternion.Euler(0f, horizAngle, 0f) * transform.forward;

        // 2. Vertical elevation angle: passAngle.y in [0, 1]
        float elevationAngle = Mathf.Clamp01(passAngle.y) * maxElevationAngle;
        Vector3 rightAxis = Vector3.Cross(Vector3.up, horizDir).normalized;
        Vector3 trajectory = Quaternion.AngleAxis(-elevationAngle, rightAxis) * horizDir;

        // 3. Force calculation
        float kickForce = Mathf.Lerp(minKickForce, maxKickForce, Mathf.Clamp01(forceFraction));
        bRb.AddForce(trajectory.normalized * kickForce, ForceMode.Impulse);
    }

    /// <summary>
    /// High-level utility for brain scripts to kick towards a target world position.
    /// Calculates the required passAngle relative to agent forward.
    /// </summary>
    public void KickTowards(Vector3 targetWorldPos, float elevation = 0.15f, float forceFraction = 1f)
    {
        if (!HasPossession || currentBall == null) return;

        Vector3 toTarget = targetWorldPos - transform.position;
        toTarget.y = 0f;

        if (toTarget.sqrMagnitude < 0.01f) return;

        // Calculate signed angle from transform.forward to target
        float signedAngle = Vector3.SignedAngle(transform.forward, toTarget.normalized, Vector3.up);
        float normalizedX = Mathf.Clamp(signedAngle / maxHorizontalAngle, -1f, 1f);

        ExecuteKick(new Vector2(normalizedX, elevation), forceFraction);
    }

    private void ReleaseNearBarrier(Vector3 frontPos)
    {
        Rigidbody bRb = ballRb;
        LosePossession();
        possessionCooldown = 1.0f;

        // Push ball back into the pitch away from the barrier
        if (bRb != null)
        {
            Vector3 inward = Vector3.zero;
            if (Mathf.Abs(frontPos.x) > pitchBoundaryX) inward.x = -Mathf.Sign(frontPos.x);
            if (Mathf.Abs(frontPos.z) > pitchBoundaryZ) inward.z = -Mathf.Sign(frontPos.z);
            inward = inward.normalized;

            bRb.linearVelocity = inward * 10f;
        }
    }

    public void SetPossessionCooldown(float duration)
    {
        possessionCooldown = Mathf.Max(possessionCooldown, duration);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Ball") && !HasPossession && possessionCooldown <= 0f)
        {
            TakePossession(collision.gameObject);
        }
        else if (collision.gameObject.name.Contains("Barrier") || collision.gameObject.name.Contains("Plane"))
        {
            if (HasPossession)
            {
                LosePossession();
                possessionCooldown = 1.0f;
            }
        }
    }
}