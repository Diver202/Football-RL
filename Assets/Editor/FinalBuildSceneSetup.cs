using System;
using System.Collections.Generic;
using TMPro;
using Unity.Sentis;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// ONE-OFF EDITOR TOOL: builds the exhibition-match UI into Assets/Scenes/FinalBuild.unity as ordinary
/// scene objects (nothing is created at runtime). After it has run, everything it made is normal scene
/// content: select, move, restyle and edit it in the Editor like anything else.
///
/// It creates / sets:
///   - "Menu Canvas" with the Pre-Game screen, the Goal banner and the Full-Time screen (Gameplay font),
///   - "Exhibition Match" (ExhibitionMatch + MatchStats) with every reference and button wired,
///   - a disabled TestBrain on each player (switched on when a team is played by TestBrain bots),
///   - MatchController: Learned coach mode, Move Gain 2, no 60 s training reset,
///   - the HUD: team-name labels under the scores, the timer renamed "Timer", and the HUD canvas
///     scaled with the screen (1920 x 1080 reference) so it looks the same at any resolution.
///
/// Running it again replaces what it made before (it does not duplicate). Menu: Football > Set Up Final
/// Build Scene. Command line (Unity closed): -executeMethod FinalBuildSceneSetup.BuildBatch
/// </summary>
public static class FinalBuildSceneSetup
{
    private const string ScenePath = "Assets/Scenes/FinalBuild.unity";
    private const string FontPath = "Assets/Fonts/Gameplay SDF.asset";
    private const string ModelFolder = "Assets/Models/3v3/1_Final_NoPress/";

    // Palette (Red / Blue are the HUD's own score colours)
    private static readonly Color Overlay = new Color(0.02f, 0.03f, 0.06f, 0.80f);
    private static readonly Color Panel = new Color(0.07f, 0.09f, 0.15f, 0.96f);
    private static readonly Color PanelLight = new Color(0.14f, 0.17f, 0.26f, 1f);
    private static readonly Color TextMain = new Color(0.96f, 0.97f, 1f, 1f);
    private static readonly Color TextMuted = new Color(0.62f, 0.67f, 0.78f, 1f);
    private static readonly Color Ink = new Color(0.05f, 0.07f, 0.12f, 1f);
    private static readonly Color Track = new Color(1f, 1f, 1f, 0.10f);
    private static readonly Color Red = new Color(0.87f, 0.15f, 0.08f, 1f);
    private static readonly Color Blue = new Color(0.32f, 0.28f, 1f, 1f);
    private static readonly Color RedSoft = new Color(1f, 0.45f, 0.38f, 1f);
    private static readonly Color BlueSoft = new Color(0.58f, 0.56f, 1f, 1f);

    private static TMP_FontAsset font;
    private static Sprite rounded, circle, arrow;

    [MenuItem("Football/Set Up Final Build Scene")]
    public static void BuildFromMenu()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            scene = EditorSceneManager.OpenScene(ScenePath);
        }
        Build();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        EditorUtility.DisplayDialog("Final Build scene", "Pre-game, goal banner and full-time screens are set up.", "OK");
    }

    public static void BuildBatch()
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            Build();
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            Debug.Log("FinalBuildSceneSetup: done, saved=" + saved);
            EditorApplication.Exit(saved ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    // =====================================================================================

    // ------------------------------------------------------------------ additive: pause + free camera

    /// <summary>
    /// Adds ONLY the pause screen, the controls hint and the fly camera to the scene as it is now
    /// (everything else, including your own edits, is left alone). Running it again replaces just these.
    /// </summary>
    [MenuItem("Football/Add Pause Screen and Free Camera")]
    public static void AddPauseFromMenu()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            scene = EditorSceneManager.OpenScene(ScenePath);
        }
        AddPauseAndFreeCamera();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    public static void AddPauseBatch()
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            AddPauseAndFreeCamera();
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            Debug.Log("FinalBuildSceneSetup: pause + free camera added, saved=" + saved);
            EditorApplication.Exit(saved ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    private static void AddPauseAndFreeCamera()
    {
        Init();
        var ex = Object.FindFirstObjectByType<ExhibitionMatch>(FindObjectsInactive.Include);
        if (ex == null || ex.preGameScreen == null) throw new Exception("Exhibition Match not set up yet: run Football > Set Up Final Build Scene first.");
        Transform menuCanvas = ex.preGameScreen.transform.root;   // found even if the canvas is switched off

        // Fly camera back on the Main Camera (ExhibitionMatch switches it on only during play)
        Camera cam = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
        if (cam != null)
        {
            var fly = cam.GetComponent<UnityEngine.Rendering.FreeCamera>();
            if (fly == null) fly = cam.gameObject.AddComponent<UnityEngine.Rendering.FreeCamera>();
            ex.freeCamera = fly;
            ex.cameraTransform = cam.transform;
            EditorUtility.SetDirty(cam.gameObject);
        }

        // Pause screen
        var oldPause = menuCanvas.Find("Pause Screen");
        if (oldPause != null) Object.DestroyImmediate(oldPause.gameObject);
        BuildPauseScreen(menuCanvas, ex);

        // Controls hint on the pre-game screen
        var oldHint = ex.preGameScreen.transform.Find("Controls Hint");
        if (oldHint != null) Object.DestroyImmediate(oldHint.gameObject);
        var hint = MakeText("Controls Hint", ex.preGameScreen.transform,
                            "IN GAME:   MOUSE + WASD  FREE CAMERA     Q  DEFAULT VIEW     ESC  PAUSE",
                            22, TextMuted, new Vector2(0, -492), new Vector2(1400, 34));
        hint.characterSpacing = 4f;

        EditorUtility.SetDirty(ex);
    }

    private static void BuildPauseScreen(Transform canvas, ExhibitionMatch ex)
    {
        var screen = Stretch(MakeRect("Pause Screen", canvas));
        Img(Stretch(MakeRect("Backdrop", screen)).gameObject, Overlay);

        var panel = MakeRect("Panel", screen, new Vector2(0, 10), new Vector2(640, 640));
        Img(panel.gameObject, Panel, rounded, 0.35f);
        var accentRed = MakeRect("Accent Red", panel);
        accentRed.anchorMin = new Vector2(0, 1); accentRed.anchorMax = new Vector2(0.5f, 1); accentRed.pivot = new Vector2(1, 1);
        accentRed.sizeDelta = new Vector2(-24, 8); accentRed.anchoredPosition = new Vector2(0, -22);
        Img(accentRed.gameObject, Red);
        var accentBlue = MakeRect("Accent Blue", panel);
        accentBlue.anchorMin = new Vector2(0.5f, 1); accentBlue.anchorMax = new Vector2(1, 1); accentBlue.pivot = new Vector2(0, 1);
        accentBlue.sizeDelta = new Vector2(-24, 8); accentBlue.anchoredPosition = new Vector2(0, -22);
        Img(accentBlue.gameObject, Blue);

        var title = MakeText("Title", panel, "PAUSED", 72, TextMain, new Vector2(0, 222), new Vector2(560, 100));
        title.characterSpacing = 14f;

        MakeButton("Resume Button", panel, "RESUME", new Vector2(0, 92), new Vector2(420, 88), TextMain, Ink, 40, ex.Resume)
            .GetComponentInChildren<TMP_Text>().characterSpacing = 6f;
        MakeButton("New Match Button", panel, "NEW MATCH", new Vector2(0, -16), new Vector2(420, 88), PanelLight, TextMain, 40, ex.NewMatch)
            .GetComponentInChildren<TMP_Text>().characterSpacing = 6f;
        MakeButton("Quit Button", panel, "QUIT", new Vector2(0, -124), new Vector2(420, 88), PanelLight, TextMain, 40, ex.Quit)
            .GetComponentInChildren<TMP_Text>().characterSpacing = 6f;

        Img(MakeRect("Divider", panel, new Vector2(0, -196), new Vector2(560, 2)).gameObject, Track);
        var c1 = MakeText("Controls 1", panel, "MOUSE  LOOK      W A S D  FLY      E  UP", 22, TextMuted, new Vector2(0, -232), new Vector2(600, 32));
        var c2 = MakeText("Controls 2", panel, "Q  DEFAULT VIEW      ESC  RESUME", 22, TextMuted, new Vector2(0, -268), new Vector2(600, 32));
        c1.characterSpacing = c2.characterSpacing = 4f;

        ex.pauseScreen = screen.gameObject;
        screen.gameObject.SetActive(false);
    }

    // =====================================================================================

    private static void Init()
    {
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        if (font == null) throw new Exception("Font not found: " + FontPath);
        rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        circle = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        arrow = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd");
    }

    private static void Build()
    {
        Init();

        var match = Object.FindFirstObjectByType<MatchController>();
        var gm = Object.FindFirstObjectByType<GameManager>();
        if (match == null || gm == null) throw new Exception("MatchController / GameManager not found in the scene.");
        GameObject hudCanvas = gm.redScoreText != null ? gm.redScoreText.canvas.rootCanvas.gameObject : GameObject.Find("Canvas");
        if (hudCanvas == null) throw new Exception("HUD Canvas not found.");

        // Remove what an earlier run created
        foreach (string n in new[] { "Menu Canvas", "Exhibition Match" })
        {
            var old = GameObject.Find(n);
            if (old != null) Object.DestroyImmediate(old);
        }
        foreach (string n in new[] { "Red Team Name", "Blue Team Name" })
        {
            var old = hudCanvas.transform.Find(n);
            if (old != null) Object.DestroyImmediate(old.gameObject);
        }

        SetUpMatchController(match);
        SetUpTestBrains(match);
        SetUpCamera();
        TMP_Text timer = SetUpHud(hudCanvas, gm, out TMP_Text hudRedName, out TMP_Text hudBlueName);

        // ---- Menu canvas ----
        var canvasGo = new GameObject("Menu Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.layer = LayerMask.NameToLayer("UI");
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        ConfigureScaler(canvasGo.GetComponent<CanvasScaler>());

        var exGo = new GameObject("Exhibition Match");
        var ex = exGo.AddComponent<ExhibitionMatch>();
        var stats = exGo.AddComponent<MatchStats>();
        ex.match = match;
        ex.stats = stats;
        ex.redColor = Red;
        ex.blueColor = Blue;
        ex.matchLengthSeconds = 300f;
        ex.goalPauseSeconds = 2f;
        ex.teams = TeamOptions();
        ex.redChoice = 2;   // AllPass
        ex.blueChoice = 0;  // NoPass
        ex.hud = hudCanvas;
        ex.timerText = timer;
        ex.hudRedName = hudRedName;
        ex.hudBlueName = hudBlueName;

        BuildPreGame(canvasGo.transform, ex);
        BuildGoalBanner(canvasGo.transform, ex);
        BuildFullTime(canvasGo.transform, ex);
        AddPauseAndFreeCamera();

        EditorUtility.SetDirty(ex);
        EditorUtility.SetDirty(stats);
    }

    // ------------------------------------------------------------------ existing objects

    private static void SetUpMatchController(MatchController match)
    {
        match.coachMode = CoachMode.Learned;
        match.coachAllowPress = false;
        match.moveGain = 2f;
        match.resetOnTimeout = false;
        match.redCoachModel = null;   // set per team by ExhibitionMatch
        match.blueCoachModel = null;
        EditorUtility.SetDirty(match);
    }

    private static void SetUpTestBrains(MatchController match)
    {
        void Add(List<FootballAgentML> team, Transform attackGoal)
        {
            foreach (var p in team)
            {
                if (p == null) continue;
                var brain = p.GetComponent<TestBrain>();
                if (brain == null) brain = p.gameObject.AddComponent<TestBrain>();
                brain.opponentGoal = attackGoal;
                brain.targetBall = match.ball;
                brain.enabled = false;   // switched on only when this team is played by TestBrain bots
                EditorUtility.SetDirty(brain);
                PrefabUtility.RecordPrefabInstancePropertyModifications(brain);
            }
        }
        Add(match.redTeam, match.blueGoal);
        Add(match.blueTeam, match.redGoal);
    }

    /// <summary>
    /// Fixed broadcast view: high on the +z touchline looking across the pitch, so Red (who start on +x)
    /// are on the left of the screen like their score and card. Position worked out so the whole pitch and
    /// both goals fit between the score at the top and the timer at the bottom (16:9). This is the view
    /// Q returns to; the FreeCamera fly camera stays on the camera (see AddPauseAndFreeCamera).
    /// </summary>
    private static void SetUpCamera()
    {
        Camera cam = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
        if (cam == null) { Debug.LogWarning("FinalBuildSceneSetup: no camera found"); return; }
        cam.transform.SetPositionAndRotation(new Vector3(0f, 86f, 94f), Quaternion.Euler(43f, 180f, 0f));
        cam.fieldOfView = 35f;
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, 500f);
        EditorUtility.SetDirty(cam.transform);
        EditorUtility.SetDirty(cam);
    }

    private static TMP_Text SetUpHud(GameObject hudCanvas, GameManager gm, out TMP_Text redName, out TMP_Text blueName)
    {
        ConfigureScaler(hudCanvas.GetComponent<CanvasScaler>());

        TMP_Text timer = null;
        foreach (var t in hudCanvas.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t.gameObject.name == "Score (1)" || t.gameObject.name == "Timer" || t.text.Contains("HH:MM"))
            {
                timer = t;
                break;
            }
        }
        if (timer != null)
        {
            timer.gameObject.name = "Timer";
            timer.text = "00:00";
            EditorUtility.SetDirty(timer);
        }

        // Each name goes directly under its score number: same box, same alignment as the score text, 58 px lower.
        redName = UnderScore("Red Team Name", hudCanvas.transform, gm.redScoreText, "TestBrain Bots", RedSoft);
        blueName = UnderScore("Blue Team Name", hudCanvas.transform, gm.blueScoreText, "NoPass", BlueSoft);
        return timer;
    }

    private static TMP_Text UnderScore(string name, Transform parent, TMP_Text score, string text, Color color)
    {
        Vector2 pos = score != null ? score.rectTransform.anchoredPosition : Vector2.zero;
        Vector2 size = score != null ? score.rectTransform.sizeDelta : new Vector2(600, 98);
        var t = MakeText(name, parent, text, 30, color, pos + new Vector2(0, -58), new Vector2(size.x, 48));
        if (score != null)
        {
            var r = t.rectTransform;
            r.anchorMin = score.rectTransform.anchorMin;
            r.anchorMax = score.rectTransform.anchorMax;
            r.pivot = score.rectTransform.pivot;
            r.anchoredPosition = pos + new Vector2(0, -58);
            t.alignment = score.alignment;
        }
        t.characterSpacing = 4f;
        return t;
    }

    private static void ConfigureScaler(CanvasScaler scaler)
    {
        if (scaler == null) return;
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        EditorUtility.SetDirty(scaler);
    }

    private static List<ExhibitionMatch.TeamOption> TeamOptions()
    {
        ModelAsset Model(string file)
        {
            var m = AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelFolder + file);
            if (m == null) Debug.LogWarning("FinalBuildSceneSetup: model not found: " + ModelFolder + file);
            return m;
        }
        ExhibitionMatch.TeamOption Trained(string name, string tagline, string description) => new ExhibitionMatch.TeamOption
        {
            displayName = name,
            tagline = tagline,
            description = description,
            scriptedBots = false,
            playersModel = Model($"{name}_coach3nopress_players.onnx"),
            coachModel = Model($"{name}_coach3nopress_coordinator.onnx"),
            coachAllowPress = false,
        };
        return new List<ExhibitionMatch.TeamOption>
        {
            Trained("NoPass", "INDIVIDUAL REWARD",
                    "Lambda 0. Only the player who scores is rewarded, so every player plays for itself."),
            Trained("HalfPass", "SHARED REWARD",
                    "Lambda 0.5. A teammate's goal is worth half as much as your own."),
            Trained("AllPass", "TEAM REWARD",
                    "Lambda 1. Every goal is worth the same to the whole team."),
            new ExhibitionMatch.TeamOption
            {
                displayName = "TestBrain Bots",
                tagline = "SCRIPTED",
                description = "Hand-coded bots. They dribble straight at goal and shoot. No learning involved.",
                scriptedBots = true,
            },
        };
    }

    // ------------------------------------------------------------------ pre-game screen

    private static void BuildPreGame(Transform canvas, ExhibitionMatch ex)
    {
        var screen = Stretch(MakeRect("Pre-Game Screen", canvas));
        Img(Stretch(MakeRect("Backdrop", screen)).gameObject, Overlay);

        var title = MakeText("Title", screen, "PASS OR DRIBBLE?", 104, TextMain, new Vector2(0, 360), new Vector2(1500, 130));
        title.characterSpacing = 6f;
        var sub = MakeText("Subtitle", screen, "3V3 EXHIBITION MATCH", 30, TextMuted, new Vector2(0, 280), new Vector2(1000, 44));
        sub.characterSpacing = 14f;

        TeamCard(screen, "Red Card", "RED", Red, RedSoft, -450f, ex.RedPrevious, ex.RedNext,
                 out ex.redName, out ex.redTagline, out ex.redDescription);
        TeamCard(screen, "Blue Card", "BLUE", Blue, BlueSoft, 450f, ex.BluePrevious, ex.BlueNext,
                 out ex.blueName, out ex.blueTagline, out ex.blueDescription);

        var vs = MakeText("VS", screen, "VS", 84, TextMain, new Vector2(0, 0), new Vector2(200, 110));
        vs.characterSpacing = 4f;

        var kick = MakeButton("Kick Off Button", screen, "KICK OFF", new Vector2(0, -330), new Vector2(460, 104), TextMain, Ink, 50, ex.KickOff);
        kick.GetComponentInChildren<TMP_Text>().characterSpacing = 8f;
        ex.matchLengthLabel = MakeText("Match Length", screen, "MATCH LENGTH  05:00", 26, TextMuted, new Vector2(0, -420), new Vector2(800, 40));
        ex.matchLengthLabel.characterSpacing = 8f;

        var quit = MakeButton("Quit Button", screen, "QUIT", Vector2.zero, new Vector2(200, 70), PanelLight, TextMain, 30, ex.Quit);
        var qr = (RectTransform)quit.transform;
        qr.anchorMin = qr.anchorMax = qr.pivot = new Vector2(1, 0);
        qr.anchoredPosition = new Vector2(-56, 48);

        ex.preGameScreen = screen.gameObject;
    }

    private static void TeamCard(RectTransform parent, string name, string side, Color color, Color soft, float x,
                                 UnityAction previous, UnityAction next,
                                 out TMP_Text teamName, out TMP_Text tagline, out TMP_Text description)
    {
        var card = MakeRect(name, parent, new Vector2(x, 0), new Vector2(720, 450));
        Img(card.gameObject, Panel, rounded, 0.35f);

        var accent = MakeRect("Accent", card);
        accent.anchorMin = new Vector2(0, 1);
        accent.anchorMax = new Vector2(1, 1);
        accent.pivot = new Vector2(0.5f, 1);
        accent.sizeDelta = new Vector2(-48, 8);
        accent.anchoredPosition = new Vector2(0, -22);
        Img(accent.gameObject, color);

        var sideLabel = MakeText("Side", card, side, 34, color, new Vector2(0, 160), new Vector2(400, 50));
        sideLabel.characterSpacing = 16f;

        teamName = MakeText("Team Name", card, "AllPass", 66, TextMain, new Vector2(0, 62), new Vector2(440, 96));
        FitToBox(teamName, 34f, 66f);   // long names (TestBrain Bots) shrink instead of running under the arrows
        ArrowButton(card, "Previous", new Vector2(-284, 62), -90f, previous);
        ArrowButton(card, "Next", new Vector2(284, 62), 90f, next);

        tagline = MakeText("Tagline", card, "TEAM REWARD", 28, soft, new Vector2(0, -16), new Vector2(620, 44));
        tagline.characterSpacing = 8f;
        description = MakeText("Description", card, "", 26, TextMuted, new Vector2(0, -118), new Vector2(600, 130));
        description.textWrappingMode = TextWrappingModes.Normal;
        description.alignment = TextAlignmentOptions.Top;
    }

    private static void ArrowButton(RectTransform parent, string name, Vector2 pos, float rotation, UnityAction onClick)
    {
        var rt = MakeRect(name + " Button", parent, pos, new Vector2(84, 84));
        var bg = Img(rt.gameObject, PanelLight, circle, 1f);
        bg.type = Image.Type.Simple;
        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        button.colors = Tint();
        var nav = button.navigation;
        nav.mode = Navigation.Mode.None;
        button.navigation = nav;
        UnityEventTools.AddPersistentListener(button.onClick, onClick);

        var icon = MakeRect("Arrow", rt, Vector2.zero, new Vector2(34, 34));
        icon.localEulerAngles = new Vector3(0, 0, rotation);
        var img = Img(icon.gameObject, TextMain, arrow, 1f);
        img.type = Image.Type.Simple;
        img.raycastTarget = false;
    }

    // ------------------------------------------------------------------ goal banner

    private static void BuildGoalBanner(Transform canvas, ExhibitionMatch ex)
    {
        var banner = MakeRect("Goal Banner", canvas);
        banner.anchorMin = new Vector2(0, 0.5f);
        banner.anchorMax = new Vector2(1, 0.5f);
        banner.sizeDelta = new Vector2(0, 240);
        banner.anchoredPosition = new Vector2(0, 120);
        Img(banner.gameObject, new Color(0.03f, 0.04f, 0.08f, 0.88f));

        var top = MakeRect("Accent Top", banner);
        top.anchorMin = new Vector2(0, 1); top.anchorMax = new Vector2(1, 1); top.pivot = new Vector2(0.5f, 1);
        top.sizeDelta = new Vector2(0, 6); top.anchoredPosition = Vector2.zero;
        var bottom = MakeRect("Accent Bottom", banner);
        bottom.anchorMin = new Vector2(0, 0); bottom.anchorMax = new Vector2(1, 0); bottom.pivot = new Vector2(0.5f, 0);
        bottom.sizeDelta = new Vector2(0, 6); bottom.anchoredPosition = Vector2.zero;

        var goal = MakeText("Goal Text", banner, "GOAL!", 150, TextMain, new Vector2(0, 24), new Vector2(1200, 170));
        goal.characterSpacing = 12f;
        ex.goalBannerTeam = MakeText("Scorer", banner, "RED  -  ALLPASS", 40, Red, new Vector2(0, -76), new Vector2(1200, 56));
        ex.goalBannerTeam.characterSpacing = 10f;
        ex.goalBannerAccents = new Graphic[] { Img(top.gameObject, Red), Img(bottom.gameObject, Red) };

        ex.goalBanner = banner.gameObject;
        banner.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ full-time screen

    private static void BuildFullTime(Transform canvas, ExhibitionMatch ex)
    {
        var screen = Stretch(MakeRect("Full-Time Screen", canvas));
        Img(Stretch(MakeRect("Backdrop", screen)).gameObject, Overlay);

        var panel = MakeRect("Panel", screen, new Vector2(0, 46), new Vector2(1180, 840));
        Img(panel.gameObject, Panel, rounded, 0.35f);

        // red | blue accent along the top
        var accentRed = MakeRect("Accent Red", panel);
        accentRed.anchorMin = new Vector2(0, 1); accentRed.anchorMax = new Vector2(0.5f, 1); accentRed.pivot = new Vector2(1, 1);
        accentRed.sizeDelta = new Vector2(-24, 8); accentRed.anchoredPosition = new Vector2(0, -22);
        Img(accentRed.gameObject, Red);
        var accentBlue = MakeRect("Accent Blue", panel);
        accentBlue.anchorMin = new Vector2(0.5f, 1); accentBlue.anchorMax = new Vector2(1, 1); accentBlue.pivot = new Vector2(0, 1);
        accentBlue.sizeDelta = new Vector2(-24, 8); accentBlue.anchoredPosition = new Vector2(0, -22);
        Img(accentBlue.gameObject, Blue);

        var title = MakeText("Title", panel, "FULL TIME", 40, TextMuted, new Vector2(0, 352), new Vector2(600, 56));
        title.characterSpacing = 18f;

        // Names sit outside the score with a clear gap: red right-aligned ending at x = -230, blue starting at +230.
        ex.finalRedName = MakeText("Red Name", panel, "AllPass", 50, RedSoft, new Vector2(-410, 266), new Vector2(360, 80));
        ex.finalRedName.alignment = TextAlignmentOptions.Right;
        ex.finalScore = MakeText("Score", panel, "0  -  0", 104, TextMain, new Vector2(0, 266), new Vector2(400, 130));
        ex.finalBlueName = MakeText("Blue Name", panel, "NoPass", 50, BlueSoft, new Vector2(410, 266), new Vector2(360, 80));
        ex.finalBlueName.alignment = TextAlignmentOptions.Left;
        FitToBox(ex.finalRedName, 26f, 50f);
        FitToBox(ex.finalBlueName, 26f, 50f);
        ex.resultText = MakeText("Result", panel, "DRAW", 34, TextMain, new Vector2(0, 186), new Vector2(600, 50));
        ex.resultText.characterSpacing = 12f;

        Img(MakeRect("Divider", panel, new Vector2(0, 140), new Vector2(1060, 2)).gameObject, Track);

        float y = 92f;
        const float step = 60f;
        ex.possessionRow = StatRow(panel, "POSSESSION", y); y -= step;
        ex.shotsRow = StatRow(panel, "SHOTS", y); y -= step;
        ex.passesRow = StatRow(panel, "PASSES", y); y -= step;
        ex.interceptionsRow = StatRow(panel, "INTERCEPTIONS", y); y -= step;
        ex.tacklesRow = StatRow(panel, "TACKLES", y); y -= step;
        ex.territoryRow = StatRow(panel, "BALL IN THEIR HALF", y); y -= step;
        ex.distanceRow = StatRow(panel, "DISTANCE COVERED", y); y -= step;

        Img(MakeRect("Divider 2", panel, new Vector2(0, -316), new Vector2(1060, 2)).gameObject, Track);
        var formationRow = MakeRect("Row - Coach Formation", panel, new Vector2(0, -362), new Vector2(1060, 56));
        var fl = MakeText("Label", formationRow, "COACH'S MAIN FORMATION", 24, TextMuted, new Vector2(0, 0), new Vector2(600, 40));
        fl.characterSpacing = 6f;
        ex.redFormation = MakeText("Red Value", formationRow, "-", 32, TextMain, new Vector2(-440, 0), new Vector2(240, 50));
        ex.blueFormation = MakeText("Blue Value", formationRow, "-", 32, TextMain, new Vector2(440, 0), new Vector2(240, 50));

        MakeButton("New Match Button", screen, "NEW MATCH", new Vector2(-180, -452), new Vector2(330, 92), TextMain, Ink, 40, ex.NewMatch)
            .GetComponentInChildren<TMP_Text>().characterSpacing = 6f;
        MakeButton("Quit Button", screen, "QUIT", new Vector2(180, -452), new Vector2(330, 92), PanelLight, TextMain, 40, ex.Quit)
            .GetComponentInChildren<TMP_Text>().characterSpacing = 6f;

        ex.fullTimeScreen = screen.gameObject;
        screen.gameObject.SetActive(false);
    }

    private static StatRowView StatRow(RectTransform parent, string label, float y)
    {
        var row = MakeRect("Row - " + label, parent, new Vector2(0, y), new Vector2(1060, 56));
        var view = row.gameObject.AddComponent<StatRowView>();
        view.redValue = MakeText("Red Value", row, "0", 38, TextMain, new Vector2(-440, 2), new Vector2(240, 52));
        view.blueValue = MakeText("Blue Value", row, "0", 38, TextMain, new Vector2(440, 2), new Vector2(240, 52));
        view.label = MakeText("Label", row, label, 24, TextMuted, new Vector2(0, 12), new Vector2(600, 36));
        view.label.characterSpacing = 6f;

        var track = MakeRect("Bar Track", row, new Vector2(0, -16), new Vector2(560, 8));
        Img(track.gameObject, Track);
        var redBar = MakeRect("Red Bar", track);
        Img(redBar.gameObject, Red);
        var blueBar = MakeRect("Blue Bar", track);
        Img(blueBar.gameObject, Blue);
        view.redBar = redBar;
        view.blueBar = blueBar;
        view.Show("0", "0", 1, 1);   // even split until a match has been played
        return view;
    }

    // ------------------------------------------------------------------ UI helpers

    private static RectTransform MakeRect(string name, Transform parent, Vector2? pos = null, Vector2? size = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos ?? Vector2.zero;
        rt.sizeDelta = size ?? new Vector2(100, 100);
        return rt;
    }

    private static RectTransform Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    private static Image Img(GameObject go, Color color, Sprite sprite = null, float cornerScale = 1f)
    {
        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.color = color;
        if (sprite != null)
        {
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = cornerScale;
        }
        return img;
    }

    private static TMP_Text MakeText(string name, Transform parent, string text, float size, Color color, Vector2 pos, Vector2 box)
    {
        var rt = MakeRect(name, parent, pos, box);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
        return t;
    }

    /// <summary>Text shrinks (down to min) to stay inside its box on one line.</summary>
    private static void FitToBox(TMP_Text t, float min, float max)
    {
        t.enableAutoSizing = true;
        t.fontSizeMin = min;
        t.fontSizeMax = max;
        t.overflowMode = TextOverflowModes.Truncate;
    }

    private static Button MakeButton(string name, Transform parent, string label, Vector2 pos, Vector2 size,
                                 Color background, Color textColor, float fontSize, UnityAction onClick)
    {
        var rt = MakeRect(name, parent, pos, size);
        var img = Img(rt.gameObject, background, rounded, 0.4f);
        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = img;
        button.colors = Tint();
        var nav = button.navigation;
        nav.mode = Navigation.Mode.None;
        button.navigation = nav;
        UnityEventTools.AddPersistentListener(button.onClick, onClick);
        var t = MakeText("Label", rt, label, fontSize, textColor, Vector2.zero, size);
        Stretch((RectTransform)t.transform);
        return button;
    }

    private static ColorBlock Tint()
    {
        var c = ColorBlock.defaultColorBlock;
        c.normalColor = Color.white;
        c.highlightedColor = new Color(0.86f, 0.89f, 0.96f, 1f);
        c.pressedColor = new Color(0.70f, 0.73f, 0.82f, 1f);
        c.selectedColor = Color.white;
        c.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);
        c.fadeDuration = 0.08f;
        return c;
    }
}
