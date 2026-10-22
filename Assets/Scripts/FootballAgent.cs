using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class FootballAgent : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 15f;
    public float maxVelocity = 8f;
    public float dribbleOffset = 0.85f;
    public float ballRadius = 0.15f;

    [Header("Possession State")]
    public bool HasPossession = false;
    public GameObject currentBall;

    private Rigidbody agentRb;
    private Collider agentCollider;
    private Rigidbody ballRb;
    private Collider ballCollider;
    private float possessionCooldown = 0f;

    private void Awake()
    {
        agentRb = GetComponent<Rigidbody>();
        agentCollider = GetComponent<Collider>();

        // Lock vertical position and tilt so the agent stays firmly grounded
        agentRb.constraints = RigidbodyConstraints.FreezePositionY | 
                              RigidbodyConstraints.FreezeRotationX | 
                              RigidbodyConstraints.FreezeRotationZ;
    }

    public void ApplyMovement(Vector2 moveInput)
    {
        Vector3 force = new Vector3(moveInput.x, 0, moveInput.y).normalized * moveSpeed;
        agentRb.AddForce(force, ForceMode.Acceleration);

        // Clamp horizontal velocity
        Vector3 horiz = new Vector3(agentRb.linearVelocity.x, 0, agentRb.linearVelocity.z);
        if (horiz.magnitude > maxVelocity)
        {
            Vector3 clamped = horiz.normalized * maxVelocity;
            agentRb.linearVelocity = new Vector3(clamped.x, 0, clamped.z);
        }
    }

    private void Update()
    {
        if (possessionCooldown > 0)
        {
            possessionCooldown -= Time.deltaTime;
        }

        // KEEP BALL DIRECTLY IN FRONT AT GROUND LEVEL WHILE POSSESSED
        if (HasPossession && currentBall != null)
        {
            Vector3 frontPos = transform.position + transform.forward * dribbleOffset;
            frontPos.y = ballRadius;
            currentBall.transform.position = frontPos;

            // Check if agent/ball is nearing boundary barriers
            if (Mathf.Abs(frontPos.x) > 50f || Mathf.Abs(frontPos.z) > 34f)
            {
                // Release possession near barriers
                LosePossession();
                possessionCooldown = 1.0f;
            }
        }
    }

    public void TakePossession(GameObject ball)
    {
        if (possessionCooldown > 0f || HasPossession) return;

        HasPossession = true;
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

        // Place directly in front of the agent at ground level
        Vector3 frontPos = transform.position + transform.forward * dribbleOffset;
        frontPos.y = ballRadius;
        currentBall.transform.position = frontPos;
    }

    public void LosePossession()
    {
        if (!HasPossession && currentBall == null) return;
        HasPossession = false;

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

    public void KickTowards(Vector3 targetPosition, float force = 20f, float upwardArc = 0.15f)
    {
        if (!HasPossession || currentBall == null) return;

        Vector3 kickDir = (targetPosition - transform.position);
        kickDir.y = 0f;
        Kick(kickDir.normalized, force, upwardArc);
    }

    public void Kick(Vector3 direction, float force = 20f, float upwardArc = 0.15f)
    {
        if (!HasPossession || currentBall == null) return;

        Rigidbody bRb = ballRb;
        LosePossession();
        possessionCooldown = 0.5f;

        if (bRb != null)
        {
            bRb.isKinematic = false;
            bRb.linearVelocity = Vector3.zero;
            bRb.angularVelocity = Vector3.zero;

            Vector3 traj = (direction.normalized + Vector3.up * upwardArc).normalized;
            bRb.AddForce(traj * force, ForceMode.Impulse);
        }
    }

    public void ExecuteKick(Vector2 passAngle)
    {
        Vector3 forwardDir = transform.forward;
        Vector3 horizDir = Quaternion.Euler(0, passAngle.x * 45f, 0) * forwardDir;
        Kick(horizDir, 20f, Mathf.Clamp01(passAngle.y));
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