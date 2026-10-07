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
    private Collider ballCollider;

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

        HasPossession = false;
        currentBall.transform.parent = null;
        
        Vector3 forwardDir = transform.forward;
        Vector3 horizontalDir = Quaternion.Euler(0, passAngle.x * 45f, 0) * forwardDir;
        Vector3 trajectory = Vector3.Slerp(horizontalDir, Vector3.up, Mathf.Clamp01(passAngle.y)).normalized;

        // Re-enable physics and collision
        ballRb.isKinematic = false;
        if (ballCollider != null) ballCollider.enabled = true;
        
        ballRb.AddForce(trajectory * maxKickForce, ForceMode.Impulse);
        currentBall = null;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Ball") && !HasPossession)
        {
            TakePossession(collision.gameObject);
        }
        else if (collision.gameObject.CompareTag("Player") && HasPossession)
        {
            LosePossession();
        }
    }

    private void TakePossession(GameObject ball)
    {
        HasPossession = true;
        currentBall = ball;
        ballRb = ball.GetComponent<Rigidbody>();
        ballCollider = ball.GetComponent<Collider>();
        
        // Zero out velocities BEFORE setting kinematic to fix Unity 6 errors
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
        ballRb.isKinematic = true;

        // Disable collider while held to prevent depenetration physics explosions
        if (ballCollider != null) ballCollider.enabled = false;
        
        currentBall.transform.position = ballHoldPosition.position;
        currentBall.transform.parent = ballHoldPosition;
    }

    public void LosePossession()
    {
        if (!HasPossession) return;
        HasPossession = false;
        currentBall.transform.parent = null;
        
        ballRb.isKinematic = false;
        if (ballCollider != null) ballCollider.enabled = true;
        
        currentBall = null;
    }
}