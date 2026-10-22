using UnityEngine;
using TMPro; // Needed for TextMeshPro

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("UI References")]
    public TextMeshProUGUI redScoreText;
    public TextMeshProUGUI blueScoreText;

    [HideInInspector]
    public int teamRedScore = 0;
    [HideInInspector]
    public int teamBlueScore = 0;

    private void Awake()
    {
        if (Instance == null) 
        {
            Instance = this;
        }
        else 
        {
            Destroy(gameObject);
        }
    }

    public void GoalScored(string team)
    {
        if (team == "TeamRed")
        {
            teamRedScore++;
            if (redScoreText != null) redScoreText.text = teamRedScore.ToString();
        }
        else if (team == "TeamBlue")
        {
            teamBlueScore++;
            if (blueScoreText != null) blueScoreText.text = teamBlueScore.ToString();
        }
            
        Debug.Log($"Goal! Current Score -> Team Red: {teamRedScore} | Team Blue: {teamBlueScore}");
        
        // Notify active RL agents to grant rewards and trigger episode reset
        FootballAgentML[] agents = FindObjectsByType<FootballAgentML>(FindObjectsSortMode.None);
        foreach (var agent in agents)
        {
            bool scored = agent.CompareTag(team);
            agent.OnGoalScored(scored);
        }
    }
}
