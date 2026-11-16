using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds one standalone Windows player per team scene, so training can run without the Editor
/// (train_3v3.ps1 runs the three trainings back to back, unattended).
///
///   Builds/TeamNoPass/Football.exe     <- Assets/Scenes/TeamNoPass.unity
///   Builds/TeamHalfPass/Football.exe   <- Assets/Scenes/TeamHalfPass.unity
///   Builds/TeamAllPass/Football.exe    <- Assets/Scenes/TeamAllPass.unity
///
/// Editor: menu  Football > Build Training Players.
/// Command line (Unity must be closed):
///   Unity.exe -batchmode -quit -projectPath . -executeMethod TrainingBuilds.BuildAllBatch [-buildRoot <folder>]
///
/// Only builds: it does not change scenes, Build Profiles or Player Settings.
/// Rebuild after changing any script or one of these scenes.
/// </summary>
public static class TrainingBuilds
{
    public static readonly string[] Teams = { "TeamNoPass", "TeamHalfPass", "TeamAllPass" };
    private const string DefaultBuildRoot = "Builds";

    [MenuItem("Football/Build Training Players")]
    public static void BuildAllFromMenu()
    {
        bool ok = BuildAll(DefaultBuildRoot, out string message);
        EditorUtility.DisplayDialog(ok ? "Training players built" : "Build failed", message, "OK");
    }

    /// <summary>Entry point for -executeMethod. Exits with 0 on success, 1 on failure.</summary>
    public static void BuildAllBatch()
    {
        string root = DefaultBuildRoot;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-buildRoot") root = args[i + 1];

        bool ok = BuildAll(root, out string message);
        Debug.Log("TrainingBuilds: " + message);
        EditorApplication.Exit(ok ? 0 : 1);
    }

    private static bool BuildAll(string root, out string message)
    {
        string summary = "";
        foreach (string team in Teams)
        {
            string scene = $"Assets/Scenes/{team}.unity";
            if (!File.Exists(scene))
            {
                message = $"Scene not found: {scene}";
                return false;
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = Path.Combine(root, team, "Football.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                message = $"{team}: build {report.summary.result} ({report.summary.totalErrors} errors). See the Console / log.";
                return false;
            }
            summary += $"{team}: {options.locationPathName}\n";
        }
        message = "Built:\n" + summary;
        return true;
    }
}
