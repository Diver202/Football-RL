using UnityEngine;

/// <summary>
/// Rule-based sparring brain that drives the FootballAgent:
/// 1. Pursues the ball using pure physics locomotion.
/// 2. Naturally micro-dribbles the ball towards the opponent goal.
/// 3. Executes power shots when within shooting range.
/// </summary>
[RequireComponent(typeof(FootballAgent))]
public class TestBrain : MonoBehaviour
{
    [Header("Target References")]
    public Transform targetBall;   // GlowBall
    public Transform opponentGoal; // Opponent GoalPost

    [Header("Behavior Parameters")]
    public float shootingRange = 18f;
    [Range(0f, 1f)] public float kickElevation = 0.15f;
    [Range(0f, 1f)] public float kickForceFraction = 1.0f;

    private FootballAgent agent;

    private void Awake()
    {
        agent = GetComponent<FootballAgent>();
    }

    private void FixedUpdate()
    {
        if (targetBall == null || opponentGoal == null || agent == null) return;

        if (!agent.HasPossession)
        {
            // 1. CHASE THE BALL (Pure-physics continuous direction vector)
            Vector3 toBall = targetBall.position - transform.position;
            toBall.y = 0f;

            if (toBall.sqrMagnitude > 0.04f)
            {
                Vector2 moveInput = new Vector2(toBall.normalized.x, toBall.normalized.z);
                agent.ApplyMovement(moveInput);
            }
            else
            {
                agent.ApplyMovement(Vector2.zero);
            }
        }
        else
        {
            // 2. DRIVE TOWARDS OPPONENT GOAL (Micro-kicks naturally dribble the ball along)
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

            // 3. EXECUTE POWER KICK WHEN IN SHOOTING RANGE
            if (toGoal.magnitude <= shootingRange && agent.CanKickBall())
            {
                agent.KickTowards(opponentGoal.position, kickElevation, kickForceFraction);
            }
        }
    }
}