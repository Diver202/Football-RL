using UnityEngine;

/// <summary>
/// Simple testing brain that drives the FootballAgent:
/// 1. Pursues the ball and takes possession.
/// 2. Dribbles towards the opponent goal (experiencing the mathematical velocity decay).
/// 3. Shoots towards the goal when within shooting range.
/// </summary>
[RequireComponent(typeof(FootballAgent))]
public class TestBrain : MonoBehaviour
{
    [Header("Target References")]
    public Transform targetBall;   // GlowBall
    public Transform opponentGoal; // Opponent GoalPost

    [Header("Behavior Parameters")]
    public float possessionDistance = 1.3f;
    public float shootingRange = 18f;
    public float turnSpeed = 8f;
    [Range(0f, 1f)] public float kickElevation = 0.15f;
    [Range(0f, 1f)] public float kickForceFraction = 1.0f;

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
            // 1. CHASE THE BALL (360-degree continuous vector)
            Vector3 toBall = targetBall.position - transform.position;
            toBall.y = 0f;

            if (toBall.sqrMagnitude > 0.04f)
            {
                // Full 360-degree unit vector
                Vector2 moveInput = new Vector2(toBall.normalized.x, toBall.normalized.z);
                agent.ApplyMovement(moveInput);
            }
            else
            {
                agent.ApplyMovement(Vector2.zero);
            }

            // 2. TAKE POSSESSION WHEN CLOSE
            if (toBall.magnitude <= possessionDistance)
            {
                agent.TakePossession(targetBall.gameObject);
            }
        }
        else
        {
            // 3. DRIVE TOWARDS OPPONENT GOAL (360-degree continuous vector)
            Vector3 toGoal = opponentGoal.position - transform.position;
            toGoal.y = 0f;

            if (toGoal.sqrMagnitude > 0.04f)
            {
                Vector2 moveInput = new Vector2(toGoal.normalized.x, toGoal.normalized.z);
                agent.ApplyMovement(moveInput);
            }
            else
            {
                agent.ApplyMovement(Vector2.zero);
            }

            // 4. KICK TOWARDS GOAL WHEN IN RANGE
            if (toGoal.magnitude <= shootingRange)
            {
                agent.KickTowards(opponentGoal.position, kickElevation, kickForceFraction);
            }
        }
    }
}