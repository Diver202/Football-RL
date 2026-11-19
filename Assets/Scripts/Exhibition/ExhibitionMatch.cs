using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Runs an exhibition match in the FinalBuild scene:
///   PRE-GAME   pick the Red and Blue teams (trained models or TestBrain bots), then Kick Off.
///   MATCH      matchLengthSeconds of play (5 minutes) with no training resets; after a goal a banner
///              shows for a moment and play restarts from kick-off.
///   FULL TIME  final score and a breakdown of the match (MatchStats); New Match or Quit.
///
/// All screens are ordinary objects in the scene; this component only shows / hides them and fills in
/// the text. The teams on offer are the "Teams" list below (edit it in the Inspector to use other models).
///
/// A team of trained models: the players use the team's players model, the team's coach (created by
/// MatchController in Learned coach mode) uses the coach model. A TestBrain team: the players' ML-Agents
/// components are switched off and their TestBrain components switched on.
/// </summary>
public class ExhibitionMatch : MonoBehaviour
{
    [System.Serializable]
    public class TeamOption
    {
        public string displayName = "AllPass";
        [Tooltip("Short line under the name, e.g. TEAM REWARD.")]
        public string tagline = "";
        [TextArea(2, 4)] public string description = "";
        [Tooltip("Played by the hand-coded TestBrain bots instead of trained models.")]
        public bool scriptedBots;
        public ModelAsset playersModel;
        public ModelAsset coachModel;
        [Tooltip("Whether this team's coach may pick the Press formation (untick for the no-Press models).")]
        public bool coachAllowPress;
    }

    public enum Phase { PreGame, Playing, GoalScored, Paused, FullTime }

    [Header("Match")]
    public MatchController match;
    public MatchStats stats;
    [Tooltip("Length of a match in seconds (300 = 5 minutes).")]
    [Min(10f)] public float matchLengthSeconds = 300f;
    [Tooltip("How long the goal banner shows before the kick-off (seconds, real time).")]
    [Min(0f)] public float goalPauseSeconds = 2f;
    public Color redColor = new Color(0.87f, 0.15f, 0.08f);
    public Color blueColor = new Color(0.32f, 0.28f, 1f);

    [Header("Teams")]
    public List<TeamOption> teams = new List<TeamOption>();
    public int redChoice;
    public int blueChoice = 1;

    [Header("In-game HUD")]
    [Tooltip("Shown only while a match is being played.")]
    public GameObject hud;
    public TMP_Text timerText;
    public TMP_Text hudRedName;
    public TMP_Text hudBlueName;

    [Header("Pre-game screen")]
    public GameObject preGameScreen;
    public TMP_Text redName;
    public TMP_Text redTagline;
    public TMP_Text redDescription;
    public TMP_Text blueName;
    public TMP_Text blueTagline;
    public TMP_Text blueDescription;
    public TMP_Text matchLengthLabel;

    [Header("Pause screen (Esc)")]
    public GameObject pauseScreen;

    [Header("Camera")]
    [Tooltip("The fly camera (URP FreeCamera) on the Main Camera. It is switched on only while a match is " +
             "being played, so moving the mouse on a menu does not swing the camera.")]
    public Behaviour freeCamera;
    [Tooltip("Camera that Q returns to its starting (broadcast) position. Defaults to the Main Camera.")]
    public Transform cameraTransform;

    [Header("Goal banner")]
    public GameObject goalBanner;
    public TMP_Text goalBannerTeam;
    public Graphic[] goalBannerAccents;

    [Header("Full-time screen")]
    public GameObject fullTimeScreen;
    public TMP_Text finalScore;
    public TMP_Text finalRedName;
    public TMP_Text finalBlueName;
    public TMP_Text resultText;
    public StatRowView possessionRow;
    public StatRowView shotsRow;
    public StatRowView passesRow;
    public StatRowView interceptionsRow;
    public StatRowView tacklesRow;
    public StatRowView territoryRow;
    public StatRowView distanceRow;
    public TMP_Text redFormation;
    public TMP_Text blueFormation;

    public Phase CurrentPhase { get; private set; } = Phase.PreGame;

    private float elapsed;
    private float playTimeScale = 1f;                                    // > 1 only in tournament mode
    private readonly List<(float time, bool red)> goals = new List<(float, bool)>();   // this match's goals
    private Vector3 defaultCameraPosition;
    private Quaternion defaultCameraRotation;

    // ------------------------------------------------------------------ lifecycle

    private void OnEnable() { GameManager.OnGoalScoredEvent += HandleGoal; }
    private void OnDisable() { GameManager.OnGoalScoredEvent -= HandleGoal; }

    private void Start()
    {
        if (match == null) match = FindFirstObjectByType<MatchController>();
        if (stats == null) stats = GetComponent<MatchStats>();

        // The menus' canvas may have been switched off in the Editor to keep the Scene view clear.
        if (preGameScreen != null) preGameScreen.transform.root.gameObject.SetActive(true);

        if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        if (cameraTransform != null)
        {
            // The camera's position in the scene is the default (broadcast) view that Q returns to.
            defaultCameraPosition = cameraTransform.position;
            defaultCameraRotation = cameraTransform.rotation;
        }

        ShowPreGame();

        string shots = AutoTestFolder();
        if (shots != null) StartCoroutine(AutoTest(shots));
        if (Arg("-tournament") != null) StartCoroutine(Tournament());
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            if (CurrentPhase == Phase.Playing) Pause();
            else if (CurrentPhase == Phase.Paused) Resume();
        }

        if (CurrentPhase == Phase.Playing) UpdateClock();
    }

    private void FixedUpdate()
    {
        // The match clock runs on physics time, so a match is exactly matchLengthSeconds of simulation at any
        // speed (the tournament runs much faster than real time).
        if (CurrentPhase != Phase.Playing) return;
        elapsed = Mathf.Min(elapsed + Time.fixedDeltaTime, matchLengthSeconds);
        if (elapsed >= matchLengthSeconds)
        {
            UpdateClock();
            EndMatch();
        }
    }

    private void LateUpdate()
    {
        // Q: back to the default view. Held down, it keeps the camera there (FreeCamera itself also uses
        // Q to fly down, which this overrides), after FreeCamera has moved it this frame.
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.qKey.isPressed) ResetCamera();
    }

    // ------------------------------------------------------------------ buttons (wired in the scene)

    public void RedPrevious() { redChoice = Wrap(redChoice - 1); RefreshSelection(); }
    public void RedNext() { redChoice = Wrap(redChoice + 1); RefreshSelection(); }
    public void BluePrevious() { blueChoice = Wrap(blueChoice - 1); RefreshSelection(); }
    public void BlueNext() { blueChoice = Wrap(blueChoice + 1); RefreshSelection(); }

    public void KickOff()
    {
        if (CurrentPhase != Phase.PreGame || teams.Count == 0) return;
        TeamOption red = teams[redChoice], blue = teams[blueChoice];
        ApplyTeam(match.redTeam, red, match.blueGoal, match.RedCoachAgent);
        ApplyTeam(match.blueTeam, blue, match.redGoal, match.BlueCoachAgent);

        ResetScores();
        match.ResetScene();
        if (stats != null) stats.BeginMatch(match, !red.scriptedBots, !blue.scriptedBots);

        elapsed = 0f;
        UpdateClock();
        SetText(hudRedName, red.displayName);
        SetText(hudBlueName, blue.displayName);
        SetActive(preGameScreen, false);
        SetActive(fullTimeScreen, false);
        SetActive(goalBanner, false);
        SetActive(pauseScreen, false);
        SetActive(hud, true);
        ResetCamera();
        goals.Clear();
        CurrentPhase = Phase.Playing;
        Time.timeScale = playTimeScale;
        SetPlayControls(true);
    }

    public void NewMatch()
    {
        if (goalRoutine != null) StopCoroutine(goalRoutine);
        goalRoutine = null;
        ResetScores();
        match.ResetScene();
        ResetCamera();
        ShowPreGame();
    }

    /// <summary>Esc during a match: freeze everything and show the pause screen.</summary>
    public void Pause()
    {
        if (CurrentPhase != Phase.Playing) return;
        CurrentPhase = Phase.Paused;
        Time.timeScale = 0f;
        SetActive(pauseScreen, true);
        SetPlayControls(false);
    }

    /// <summary>Resume button, or Esc again.</summary>
    public void Resume()
    {
        if (CurrentPhase != Phase.Paused) return;
        SetActive(pauseScreen, false);
        CurrentPhase = Phase.Playing;
        Time.timeScale = playTimeScale;
        SetPlayControls(true);
    }

    /// <summary>Puts the camera back on its starting (broadcast) view.</summary>
    public void ResetCamera()
    {
        if (cameraTransform != null) cameraTransform.SetPositionAndRotation(defaultCameraPosition, defaultCameraRotation);
    }

    /// <summary>Playing: cursor hidden and locked, fly camera on. Any menu: cursor shown, fly camera off.</summary>
    private void SetPlayControls(bool playing)
    {
        Cursor.visible = !playing;
        Cursor.lockState = playing ? CursorLockMode.Locked : CursorLockMode.None;
        if (freeCamera != null) freeCamera.enabled = playing;
    }

    public void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ------------------------------------------------------------------ flow

    private void ShowPreGame()
    {
        CurrentPhase = Phase.PreGame;
        Time.timeScale = 0f;   // nothing moves behind the menu
        if (match != null) match.timeRemainingOverride = 1f;
        redChoice = Wrap(redChoice);
        blueChoice = Wrap(blueChoice);
        RefreshSelection();
        SetText(matchLengthLabel, "MATCH LENGTH  " + FormatClock(matchLengthSeconds));
        SetActive(hud, false);
        SetActive(goalBanner, false);
        SetActive(fullTimeScreen, false);
        SetActive(pauseScreen, false);
        SetActive(preGameScreen, true);
        SetPlayControls(false);
    }

    private void HandleGoal(string scoringTeam)
    {
        // Goals after the final whistle do not count. (At high time scales Unity still runs the physics steps
        // already queued for the frame in which the clock reached full time: up to ~10 s of extra play.)
        if (CurrentPhase != Phase.Playing) return;
        goals.Add((elapsed, scoringTeam == "TeamRed"));
        if (goalPauseSeconds <= 0f) return;   // tournament: straight back to kick-off, no banner
        goalRoutine = StartCoroutine(GoalPause(scoringTeam == "TeamRed"));
    }

    private Coroutine goalRoutine;

    private IEnumerator GoalPause(bool red)
    {
        // MatchController has already put everyone back on their kick-off spots.
        CurrentPhase = Phase.GoalScored;
        Color c = red ? redColor : blueColor;
        TeamOption team = teams[red ? redChoice : blueChoice];
        SetText(goalBannerTeam, (red ? "RED  -  " : "BLUE  -  ") + team.displayName.ToUpperInvariant());
        if (goalBannerTeam != null) goalBannerTeam.color = c;
        if (goalBannerAccents != null) foreach (var g in goalBannerAccents) if (g != null) g.color = c;
        SetActive(goalBanner, true);
        Time.timeScale = 0f;
        yield return new WaitForSecondsRealtime(goalPauseSeconds);
        SetActive(goalBanner, false);
        if (CurrentPhase == Phase.GoalScored)
        {
            CurrentPhase = Phase.Playing;
            Time.timeScale = playTimeScale;
        }
        goalRoutine = null;
    }

    private void EndMatch()
    {
        CurrentPhase = Phase.FullTime;
        Time.timeScale = 0f;
        match.timeRemainingOverride = 0f;
        if (stats != null) stats.EndMatch();
        FillFullTime();
        SetActive(hud, false);
        SetActive(goalBanner, false);
        SetActive(pauseScreen, false);
        SetActive(fullTimeScreen, true);
        SetPlayControls(false);
    }

    // ------------------------------------------------------------------ teams

    private void ApplyTeam(List<FootballAgentML> players, TeamOption option, Transform attackGoal, TeamCoachAgent coach)
    {
        foreach (var p in players)
        {
            if (p == null) continue;
            var brain = p.GetComponent<TestBrain>();
            var requester = p.GetComponent<DecisionRequester>();
            if (option.scriptedBots)
            {
                p.enabled = false;
                if (requester != null) requester.enabled = false;
                if (brain != null)
                {
                    brain.opponentGoal = attackGoal;
                    brain.enabled = true;
                }
            }
            else
            {
                if (brain != null) brain.enabled = false;
                p.enabled = true;
                if (requester != null) requester.enabled = true;
                var bp = p.GetComponent<BehaviorParameters>();
                if (option.playersModel != null && bp != null) p.SetModel(bp.BehaviorName, option.playersModel);
            }
        }

        if (coach != null)
        {
            coach.allowPressOverride = option.coachAllowPress ? 1 : 0;
            if (!option.scriptedBots && option.coachModel != null)
                coach.SetModel(TeamCoachAgent.BehaviorNameValue, option.coachModel);
        }
    }

    private void RefreshSelection()
    {
        if (teams.Count == 0) return;
        ShowOption(teams[redChoice], redName, redTagline, redDescription);
        ShowOption(teams[blueChoice], blueName, blueTagline, blueDescription);
    }

    private static void ShowOption(TeamOption o, TMP_Text name, TMP_Text tagline, TMP_Text description)
    {
        SetText(name, o.displayName);
        SetText(tagline, o.tagline);
        SetText(description, o.description);
    }

    // ------------------------------------------------------------------ full time

    private void FillFullTime()
    {
        // The score as it stood at the final whistle (recorded by MatchStats.EndMatch).
        int red = stats != null ? stats.Red.goals : GameManager.Instance != null ? GameManager.Instance.teamRedScore : 0;
        int blue = stats != null ? stats.Blue.goals : GameManager.Instance != null ? GameManager.Instance.teamBlueScore : 0;
        TeamOption redTeam = teams[redChoice], blueTeam = teams[blueChoice];

        SetText(finalScore, $"{red}  -  {blue}");
        SetText(finalRedName, redTeam.displayName);
        SetText(finalBlueName, blueTeam.displayName);
        if (resultText != null)
        {
            resultText.text = red > blue ? "RED WINS" : blue > red ? "BLUE WINS" : "DRAW";
            resultText.color = red > blue ? redColor : blue > red ? blueColor : Color.white;
        }

        if (stats == null) return;
        MatchStats.TeamStats r = stats.Red, b = stats.Blue;

        float possession = r.possessionTime + b.possessionTime;
        int redPossession = possession > 0f ? Mathf.RoundToInt(100f * r.possessionTime / possession) : 50;
        ShowRow(possessionRow, redPossession.ToString(), (100 - redPossession).ToString(), redPossession, 100 - redPossession);
        ShowRow(shotsRow, r.shots, b.shots);
        ShowRow(passesRow, r.passes, b.passes);
        ShowRow(interceptionsRow, r.interceptions, b.interceptions);
        ShowRow(tacklesRow, r.tackles, b.tackles);

        float territory = r.attackingHalfTime + b.attackingHalfTime;
        int redTerritory = territory > 0f ? Mathf.RoundToInt(100f * r.attackingHalfTime / territory) : 50;
        ShowRow(territoryRow, redTerritory.ToString(), (100 - redTerritory).ToString(), redTerritory, 100 - redTerritory);
        ShowRow(distanceRow, (r.distance / 1000f).ToString("0.00") + " KM", (b.distance / 1000f).ToString("0.00") + " KM",
                r.distance, b.distance);

        SetText(redFormation, FormationText(r));
        SetText(blueFormation, FormationText(b));
    }

    private static string FormationText(MatchStats.TeamStats s)
    {
        if (!s.coached) return "NO COACH";
        int f = s.MainFormation();
        return f < 0 ? "-" : TeamCoach.FormationNames[f].ToUpperInvariant();
    }

    private static void ShowRow(StatRowView row, int red, int blue) => ShowRow(row, red.ToString(), blue.ToString(), red, blue);

    private static void ShowRow(StatRowView row, string red, string blue, float redAmount, float blueAmount)
    {
        if (row != null) row.Show(red, blue, redAmount, blueAmount);
    }

    // ------------------------------------------------------------------ helpers

    private void ResetScores()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.teamRedScore = 0;
        gm.teamBlueScore = 0;
        SetText(gm.redScoreText, "0");
        SetText(gm.blueScoreText, "0");
    }

    private void UpdateClock()
    {
        SetText(timerText, FormatClock(elapsed));
        if (match != null) match.timeRemainingOverride = Mathf.Clamp01(1f - elapsed / matchLengthSeconds);
    }

    private static string FormatClock(float seconds)
    {
        int s = Mathf.FloorToInt(seconds);
        return $"{s / 60:00}:{s % 60:00}";
    }

    private int Wrap(int i) => teams.Count == 0 ? 0 : ((i % teams.Count) + teams.Count) % teams.Count;

    private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
    private static void SetActive(GameObject g, bool on) { if (g != null) g.SetActive(on); }

    // ------------------------------------------------------------------ automatic screenshot test
    // Run the built game with  -autotest <folder>  to play a short scripted session and save screenshots
    // of every screen (used to check the UI). Normal play never uses this.

    // ------------------------------------------------------------------ headless tournament
    // PassOrDribble.exe -batchmode -nographics -tournament <out.csv> -pair NoPass,AllPass -matches 100
    //                   [-timescale 50] [-start 0]
    // Plays the two teams `matches` times back to back (sides swapped every match: the first team is Red in
    // even-numbered matches), full length, no goal banner, and appends one CSV row per match (flushed after
    // each match; -start resumes a stopped run). Quits when done. Same game, rules and physics as normal
    // play: only the speed differs (the physics step is fixed, so the speed does not change the simulation).

    private const string CsvHeader =
        "match,red_team,blue_team,red_goals,blue_goals,result,winner_team," +
        "red_possession_pct,blue_possession_pct,red_shots,blue_shots,red_passes,blue_passes," +
        "red_interceptions,blue_interceptions,red_tackles,blue_tackles,red_territory_pct,blue_territory_pct," +
        "red_distance_m,blue_distance_m,red_main_formation,blue_main_formation," +
        "red_formation_shares,blue_formation_shares,goals,match_seconds,real_seconds";

    private static string Arg(string name)
    {
        string[] a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length; i++) if (a[i] == name) return i + 1 < a.Length ? a[i + 1] : "";
        return null;
    }

    private int FindTeam(string name)
    {
        for (int i = 0; i < teams.Count; i++)
            if (teams[i].displayName.StartsWith(name, System.StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private IEnumerator Tournament()
    {
        var inv = CultureInfo.InvariantCulture;
        string outFile = Path.GetFullPath(Arg("-tournament"));
        string[] pair = (Arg("-pair") ?? "").Split(',');
        int count = int.TryParse(Arg("-matches"), out int m) ? m : 100;
        int start = int.TryParse(Arg("-start"), out int s) ? s : 0;
        float speed = float.TryParse(Arg("-timescale"), NumberStyles.Float, inv, out float t) ? t : 20f;
        int teamA = pair.Length == 2 ? FindTeam(pair[0].Trim()) : -1;
        int teamB = pair.Length == 2 ? FindTeam(pair[1].Trim()) : -1;
        if (teamA < 0 || teamB < 0)
        {
            Debug.LogError("Tournament: -pair must name two teams from the Teams list, e.g. -pair NoPass,AllPass");
            Application.Quit(2);
            yield break;
        }

        goalPauseSeconds = 0f;
        playTimeScale = speed;
        Directory.CreateDirectory(Path.GetDirectoryName(outFile));
        bool writeHeader = !File.Exists(outFile);
        yield return null;

        for (int i = start; i < count; i++)
        {
            bool aIsRed = i % 2 == 0;
            redChoice = aIsRed ? teamA : teamB;
            blueChoice = aIsRed ? teamB : teamA;
            float realStart = Time.realtimeSinceStartup;
            KickOff();
            while (CurrentPhase != Phase.FullTime) yield return null;

            string row = MatchRow(i, Time.realtimeSinceStartup - realStart);
            File.AppendAllText(outFile, (writeHeader ? CsvHeader + "\n" : "") + row + "\n");
            writeHeader = false;
            Debug.Log($"Tournament: match {i + 1}/{count}: {row}");
            NewMatch();
            yield return null;
        }
        Application.Quit(0);
    }

    private string MatchRow(int index, float realSeconds)
    {
        var inv = CultureInfo.InvariantCulture;
        MatchStats.TeamStats r = stats.Red, b = stats.Blue;
        int rg = r.goals, bg = b.goals;   // score at the final whistle (MatchStats.EndMatch)
        string result = rg > bg ? "red" : bg > rg ? "blue" : "draw";
        string winner = result == "red" ? teams[redChoice].displayName : result == "blue" ? teams[blueChoice].displayName : "";
        float possession = r.possessionTime + b.possessionTime;
        float redPossession = possession > 0f ? 100f * r.possessionTime / possession : 50f;
        float territory = r.attackingHalfTime + b.attackingHalfTime;
        float redTerritory = territory > 0f ? 100f * r.attackingHalfTime / territory : 50f;
        var goalList = new List<string>();
        foreach (var g in goals) goalList.Add((g.red ? "R" : "B") + g.time.ToString("0.0", inv));

        string F(float v) => v.ToString("0.0", inv);
        string Formation(MatchStats.TeamStats st)
        {
            if (!st.coached) return "none";
            int f = st.MainFormation();
            return f < 0 ? "none" : TeamCoach.FormationNames[f];
        }
        string Shares(MatchStats.TeamStats st)
        {
            if (!st.coached) return "";
            float total = 0f;
            foreach (float x in st.formationTime) total += x;
            var parts = new List<string>();
            for (int f = 0; f < st.formationTime.Length; f++)
                parts.Add(TeamCoach.FormationNames[f] + "=" + (total > 0f ? st.formationTime[f] / total : 0f).ToString("0.000", inv));
            return string.Join(";", parts);
        }

        return string.Join(",", new[]
        {
            index.ToString(inv), teams[redChoice].displayName, teams[blueChoice].displayName,
            rg.ToString(inv), bg.ToString(inv), result, winner,
            F(redPossession), F(100f - redPossession),
            r.shots.ToString(inv), b.shots.ToString(inv), r.passes.ToString(inv), b.passes.ToString(inv),
            r.interceptions.ToString(inv), b.interceptions.ToString(inv), r.tackles.ToString(inv), b.tackles.ToString(inv),
            F(redTerritory), F(100f - redTerritory), F(r.distance), F(b.distance),
            Formation(r), Formation(b), Shares(r), Shares(b),
            string.Join(";", goalList), F(stats.Duration), realSeconds.ToString("0.00", inv),
        });
    }

    private static string AutoTestFolder()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-autotest") return args[i + 1];
        return null;
    }

    private IEnumerator AutoTest(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        yield return new WaitForSecondsRealtime(2f);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "1_pregame.png"));
        yield return new WaitForSecondsRealtime(1f);
        RedNext();                       // Red: AllPass -> TestBrain Bots (tests the scripted-team path)
        yield return new WaitForSecondsRealtime(0.5f);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "2_pregame_changed.png"));
        yield return new WaitForSecondsRealtime(1f);
        KickOff();
        yield return new WaitForSecondsRealtime(8f);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "3_match.png"));
        yield return new WaitForSecondsRealtime(1f);
        Pause();
        yield return new WaitForSecondsRealtime(0.5f);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "3b_pause.png"));
        yield return new WaitForSecondsRealtime(1f);
        Resume();
        yield return new WaitForSecondsRealtime(0.5f);
        GameManager.Instance.GoalScored("TeamBlue");   // shows the goal banner
        yield return new WaitForSecondsRealtime(0.5f);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "4_goal.png"));
        yield return new WaitForSecondsRealtime(3f);
        Time.timeScale = 3f;                            // play a while faster, then jump to full time
        yield return new WaitForSecondsRealtime(20f);
        elapsed = matchLengthSeconds;
        yield return new WaitForSecondsRealtime(1f);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "5_fulltime.png"));
        yield return new WaitForSecondsRealtime(1f);
        NewMatch();
        yield return new WaitForSecondsRealtime(1f);
        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "6_newmatch.png"));
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit();
    }
}
