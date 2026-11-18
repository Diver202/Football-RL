using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Exports the playable game: Assets/Scenes/FinalBuild.unity -> Builds/FinalGame/PassOrDribble.exe
/// (pre-game team selection, 5-minute match, full-time breakdown; see ExhibitionMatch).
///
/// Editor: menu  Football > Build Final Game.
/// Command line (Unity must be closed):
///   Unity.exe -batchmode -quit -projectPath . -executeMethod FinalGameBuild.BuildBatch [-buildRoot <folder>]
///
/// Only builds: it does not change scenes, Build Profiles or Player Settings.
/// </summary>
public static class FinalGameBuild
{
    private const string Scene = "Assets/Scenes/FinalBuild.unity";
    private const string DefaultBuildRoot = "Builds";

    [MenuItem("Football/Build Final Game")]
    public static void BuildFromMenu()
    {
        bool ok = Build(DefaultBuildRoot, false, out string message);
        EditorUtility.DisplayDialog(ok ? "Game built" : "Build failed", message, "OK");
        if (ok) EditorUtility.RevealInFinder(Path.Combine(DefaultBuildRoot, "FinalGame", "PassOrDribble.exe"));
    }

    [MenuItem("Football/Build Final Game (Full Screen)")]
    public static void BuildFullScreenFromMenu()
    {
        bool ok = Build(DefaultBuildRoot, true, out string message);
        EditorUtility.DisplayDialog(ok ? "Game built" : "Build failed", message, "OK");
        if (ok) EditorUtility.RevealInFinder(Path.Combine(DefaultBuildRoot, "FinalGameFullScreen", "PassOrDribble.exe"));
    }

    /// <summary>Entry point for -executeMethod. Exits with 0 on success, 1 on failure. Add -fullscreen for the
    /// full-screen build.</summary>
    public static void BuildBatch()
    {
        string root = DefaultBuildRoot;
        bool fullScreen = false;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-buildRoot" && i + 1 < args.Length) root = args[i + 1];
            if (args[i] == "-fullscreen") fullScreen = true;
        }
        bool ok = Build(root, fullScreen, out string message);
        Debug.Log("FinalGameBuild: " + message);
        EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>
    /// fullScreen: Builds/FinalGameFullScreen, borderless full screen at the monitor's native resolution, under
    /// its own product name "Pass or Dribble". Windows remembers the last window mode per product name, and the
    /// training players share the project's name (Football_RL), so a separate name guarantees it opens full
    /// screen. Player Settings are changed only for the duration of the build and then restored.
    /// </summary>
    private static bool Build(string root, bool fullScreen, out string message)
    {
        string productName = PlayerSettings.productName;
        FullScreenMode mode = PlayerSettings.fullScreenMode;
        bool native = PlayerSettings.defaultIsNativeResolution;
        try
        {
            if (fullScreen)
            {
                PlayerSettings.productName = "Pass or Dribble";
                PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
                PlayerSettings.defaultIsNativeResolution = true;
            }
            return BuildPlayer(root, fullScreen ? "FinalGameFullScreen" : "FinalGame", out message);
        }
        finally
        {
            PlayerSettings.productName = productName;
            PlayerSettings.fullScreenMode = mode;
            PlayerSettings.defaultIsNativeResolution = native;
        }
    }

    private static bool BuildPlayer(string root, string folder, out string message)
    {
        if (!File.Exists(Scene))
        {
            message = $"Scene not found: {Scene}";
            return false;
        }
        var options = new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = Path.Combine(root, folder, "PassOrDribble.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            message = $"Build {report.summary.result} ({report.summary.totalErrors} errors). See the Console / log.";
            return false;
        }
        message = "Built: " + options.locationPathName;
        return true;
    }
}
