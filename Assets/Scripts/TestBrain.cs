using UnityEngine;

[RequireComponent(typeof(FootballAgent))]
public class HeuristicGreedyBrain : MonoBehaviour
{
    public Transform targetBall; // Assign the GlowBall here
    public Transform opponentGoal; // Assign the opposing GoalPost here
    
    private FootballAgent agent;

    private void Awake()
    {
        agent = GetComponent<FootballAgent>();
    }

    private void FixedUpdate()
    {
        if (agent.HasPossession)
        {
            // Egoistic policy: ignore teammates, drive to goal
            Vector3 dirToGoal = (opponentGoal.position - transform.position).normalized;
            agent.ApplyMovement(new Vector2(dirToGoal.x, dirToGoal.z));
            
            // Rotate capsule to face movement direction
            transform.forward = Vector3.Slerp(transform.forward, new Vector3(dirToGoal.x, 0, dirToGoal.z), 0.1f);

            // Shoot when within range (simulating pass_flag = 1 for a shot)
            if (Vector3.Distance(transform.position, opponentGoal.position) < 15f)
            {
                // Shoot with 0 horizontal offset and 30% elevation
                agent.ExecuteKick(new Vector2(0f, 0.3f)); 
            }
        }
        else
        {
            // Chase ball
            Vector3 dirToBall = (targetBall.position - transform.position).normalized;
            agent.ApplyMovement(new Vector2(dirToBall.x, dirToBall.z));
            
            transform.forward = Vector3.Slerp(transform.forward, new Vector3(dirToBall.x, 0, dirToBall.z), 0.1f);
        }
    }
}