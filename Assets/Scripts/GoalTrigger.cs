using UnityEngine;

[RequireComponent(typeof(Collider))]
public class GoalTrigger : MonoBehaviour
{
    [Tooltip("The tag of the team that SCORES when the ball enters this trigger (e.g., 'TeamA' or 'TeamB')")]
    public string scoringTeam = "TeamA";

    private void Awake()
    {
        // Ensure the collider is set to be a trigger
        Collider col = GetComponent<Collider>();
        if (col != null)
        {
            col.isTrigger = true;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Ball"))
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.GoalScored(scoringTeam);
            }
            else
            {
                Debug.LogWarning("GameManager instance not found! Goal scored by: " + scoringTeam);
            }
        }
    }
}
