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
        
        // TODO: Reset the environment positions for the next episode/round here
    }
}
