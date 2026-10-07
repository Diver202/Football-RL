using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    public int teamAScore = 0;
    public int teamBScore = 0;

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
        if (team == "TeamA")
            teamAScore++;
        else if (team == "TeamB")
            teamBScore++;
            
        Debug.Log($"Goal! Current Score -> Team A: {teamAScore} | Team B: {teamBScore}");
        
        // TODO: Reset the environment positions for the next episode/round here
        // (This would typically hook into the ML-Agents EndEpisode mechanics or reset ball position)
    }

    private void OnGUI()
    {
        // Display score at the top center of the screen
        GUIStyle style = new GUIStyle();
        style.fontSize = 48;
        style.normal.textColor = Color.white;
        style.alignment = TextAnchor.UpperCenter;
        style.fontStyle = FontStyle.Bold;

        // Add a slight drop shadow for readability
        GUIStyle shadowStyle = new GUIStyle(style);
        shadowStyle.normal.textColor = Color.black;

        string scoreText = $"{teamAScore} - {teamBScore}";
        
        Rect rect = new Rect(0, 30, Screen.width, 100);
        Rect shadowRect = new Rect(2, 32, Screen.width, 100);
        
        GUI.Label(shadowRect, scoreText, shadowStyle);
        GUI.Label(rect, scoreText, style);
    }
}
