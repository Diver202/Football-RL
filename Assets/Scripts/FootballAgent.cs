using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class FootballAgent : MonoBehaviour
{
    [Header("Movement Dynamics")]
    public float baseForce = 20f;
    public float maxVelocityBase = 8f; 
    public float dribblePenalty = 3f;

    [Header("Possession & Kicking")]
    public Transform ballHoldPosition; 
    public float maxKickForce = 20f;
    
    public bool HasPossession { get; private set; }
    
    private Rigidbody agentRb;
    private GameObject currentBall;
    private Rigidbody ballRb;
    
    // To prevent immediate re-possession after kicking or losing the ball
    private float possessionCooldown = 0f;

    private void Awake()
    {
        agentRb = GetComponent<Rigidbody>();
    }

    public void ApplyMovement(Vector2 moveInput)
    {
        Vector3 force = new Vector3(moveInput.x, 0, moveInput.y).normalized * baseForce;
        agentRb.AddForce(force, ForceMode.Acceleration);
    }

    private void FixedUpdate()
    {
        if (possessionCooldown > 0)
        {
            possessionCooldown -= Time.fixedDeltaTime;
        }

        // Implementation of V_max(t) = V_base - kappa * I_possess(i,t)
        float currentMaxV = HasPossession ? (maxVelocityBase - dribblePenalty) : maxVelocityBase;
        
        Vector3 horizontalVelocity = new Vector3(agentRb.linearVelocity.x, 0, agentRb.linearVelocity.z);
        if (horizontalVelocity.magnitude > currentMaxV)
        {
            Vector3 clampedVelocity = horizontalVelocity.normalized * currentMaxV;
            agentRb.linearVelocity = new Vector3(clampedVelocity.x, agentRb.linearVelocity.y, clampedVelocity.z);
        }
    }

    public void ExecuteKick(Vector2 passAngle)
    {
        if (!HasPossession || currentBall == null) return;

        Rigidbody bRb = ballRb;
        LosePossession();
        
        // Cooldown so agent doesn't instantly re-possess the ball it just kicked
        possessionCooldown = 0.5f;
        
        Vector3 forwardDir = transform.forward;
        Vector3 horizontalDir = Quaternion.Euler(0, passAngle.x * 45f, 0) * forwardDir;
        Vector3 trajectory = Vector3.Slerp(horizontalDir, Vector3.up, Mathf.Clamp01(passAngle.y)).normalized;

        bRb.AddForce(trajectory * maxKickForce, ForceMode.Impulse);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Ball") && !HasPossession && possessionCooldown <= 0f)
        {
            TakePossession(collision.gameObject);
        }
        else if (collision.gameObject.CompareTag("Player") && HasPossession)
        {
            // If tackled by another player, lose possession
            LosePossession();
            possessionCooldown = 1.0f; // Brief cooldown before they can grab it again
        }
    }

    private void TakePossession(GameObject ball)
    {
        HasPossession = true;
        currentBall = ball;
        ballRb = ball.GetComponent<Rigidbody>();
        
        // Snap ball to hold position
        currentBall.transform.position = ballHoldPosition.position;
        
        // Use a FixedJoint to attach the ball physically without disabling its colliders
        // This stops the ball from going through walls while possessed
        FixedJoint joint = currentBall.AddComponent<FixedJoint>();
        joint.connectedBody = agentRb;
        
        // The joint inherently allows the ball to push against the environment 
        // without passing through colliders, unlike the kinematic parenting approach.
    }

    public void LosePossession()
    {
        if (!HasPossession || currentBall == null) return;
        HasPossession = false;
        
        FixedJoint joint = currentBall.GetComponent<FixedJoint>();
        if (joint != null)
        {
            Destroy(joint);
        }
        
        currentBall = null;
        ballRb = null;
    }
}