using UnityEngine;

[RequireComponent(typeof(FootballAgent))]
public class TestBrain : MonoBehaviour
{
    [Header("Target References")]
    public Transform targetBall;   // GlowBall
    public Transform opponentGoal; // Target GoalPost

    [Header("Behavior Tuning")]
    public float possessionDistance = 1.3f;
    public float shootingRange = 18f;
    public float kickForce = 22f;
    public float turnSpeed = 8f;

    private FootballAgent agent;

    private void Awake()
    {
        agent = GetComponent<FootballAgent>();
    }

    private void FixedUpdate()
    {
        if (targetBall == null || opponentGoal == null) return;

        if (!agent.HasPossession)
        {
            // 1. CHASE THE BALL
            Vector3 toBall = targetBall.position - transform.position;
            toBall.y = 0f;

            if (toBall.sqrMagnitude > 0.01f)
            {
                // Smoothly face the ball
                transform.forward = Vector3.Slerp(transform.forward, toBall.normalized, turnSpeed * Time.fixedDeltaTime);
                // Move towards the ball
                agent.ApplyMovement(new Vector2(toBall.normalized.x, toBall.normalized.z));
            }

            // 2. TAKE POSSESSION WHEN CLOSE
            if (toBall.magnitude <= possessionDistance)
            {
                agent.TakePossession(targetBall.gameObject);
            }
        }
        else
        {
            // 3. DRIVE TOWARDS OPPONENT GOAL
            Vector3 toGoal = opponentGoal.position - transform.position;
            toGoal.y = 0f;

            if (toGoal.sqrMagnitude > 0.01f)
            {
                // Smoothly face the goal
                transform.forward = Vector3.Slerp(transform.forward, toGoal.normalized, turnSpeed * Time.fixedDeltaTime);
                // Move towards the goal
                agent.ApplyMovement(new Vector2(toGoal.normalized.x, toGoal.normalized.z));
            }

            // 4. KICK TOWARDS GOAL WHEN IN RANGE
            if (toGoal.magnitude <= shootingRange)
            {
                agent.KickTowards(opponentGoal.position, kickForce, 0.15f);
            }
        }
    }
}