using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds a Windows IL2CPP player containing ONLY the TLS probe scene.
/// </summary>
/// <remarks>
/// <para>
/// The scene is created programmatically rather than committed: a hand-written .unity is
/// GUIDs and YAML that can drift from the component it is supposed to instantiate, and the
/// whole point of this build is that what runs is what was reviewed.
/// </para>
/// <para>
/// Scripting backend and stripping level are SET and then RESTORED, and the restore is
/// FLUSHED with <c>AssetDatabase.SaveAssets</c> -- without that it never reaches disk and
/// the project silently keeps IL2CPP and whatever stripping level was last used, changing
/// every later build without anyone choosing it.
/// </para>
/// </remarks>
public static class TlsProbeBuild
{
    private const string SceneDir = "Assets/_TlsProbe";
    private const string ScenePath = SceneDir + "/TlsProbe.unity";

    public static void Build()
    {
        string outRoot = Arg("-probeOutput") ?? "build/tlsprobe";
        string strippingArg = Arg("-probeStripping") ?? "Minimal";

        if (!Enum.TryParse(strippingArg, true, out ManagedStrippingLevel stripping))
        {
            throw new Exception("[tls-probe-build] -probeStripping must be Minimal/Low/Medium/High, got " + strippingArg);
        }

        var group = NamedBuildTarget.Standalone;
        ScriptingImplementation prevBackend = PlayerSettings.GetScriptingBackend(group);
        ManagedStrippingLevel prevStripping = PlayerSettings.GetManagedStrippingLevel(group);
        Debug.Log($"[tls-probe-build] current backend={prevBackend} stripping={prevStripping}");

        try
        {
            MakeScene();

            PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(group, stripping);
            Debug.Log($"[tls-probe-build] building IL2CPP, stripping={stripping}");

            string dir = Path.Combine(outRoot, stripping.ToString());
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "TlsProbe.exe");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(opts);
            BuildSummary s = report.summary;
            Debug.Log($"[tls-probe-build] result={s.result} errors={s.totalErrors} " +
                      $"size={s.totalSize} out={s.outputPath}");

            // The exit code is not the evidence, and NEITHER IS THE .EXE. A build that
            // reported result=Failed still exited 0 and left a plausible 652 KB TlsProbe.exe
            // behind, because IL2CPP had died at "fatal error C1085: ... No space left on
            // device" AFTER the launcher was written. That .exe is a shell: it pops a modal
            // "Failed to load il2cpp" and writes a zero-byte log, which from a script looks
            // exactly like a player that has not finished starting.
            //
            // GameAssembly.dll is the file that actually holds the compiled game, so it is
            // the artefact worth asserting.
            string gameAssembly = Path.Combine(dir, "GameAssembly.dll");
            if (s.result != BuildResult.Succeeded || !File.Exists(exe) || !File.Exists(gameAssembly))
            {
                throw new Exception($"[tls-probe-build] FAILED result={s.result} " +
                                    $"exeExists={File.Exists(exe)} gameAssemblyExists={File.Exists(gameAssembly)}");
            }

            Debug.Log($"[tls-probe-build] OK {exe} (GameAssembly.dll {new FileInfo(gameAssembly).Length} bytes)");
        }
        finally
        {
            PlayerSettings.SetScriptingBackend(group, prevBackend);
            PlayerSettings.SetManagedStrippingLevel(group, prevStripping);

            // Without this the restore is IN MEMORY ONLY. The build itself persists
            // ProjectSettings.asset, so the IL2CPP/High values reach disk and the restore
            // after it does not -- batchmode `-quit` exits without flushing. Observed: a run
            // logged "restored backend=Mono2x" and still left `Standalone: 1` (IL2CPP) and
            // `managedStrippingLevel: {Standalone: 4}` committed to ProjectSettings.asset,
            // which the NEXT run then read as the value to preserve.
            AssetDatabase.SaveAssets();

            Debug.Log($"[tls-probe-build] restored backend={prevBackend} stripping={prevStripping}");
        }
    }

    private static void MakeScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("TlsProbe");
        go.AddComponent<Il2cppTlsProbe>();
        go.AddComponent<TlsProbeQuit>();
        Directory.CreateDirectory(SceneDir);
        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            throw new Exception("[tls-probe-build] could not save " + ScenePath);
        }
        Debug.Log("[tls-probe-build] scene written: " + ScenePath);
    }

    private static string Arg(string name)
    {
        string[] a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++)
        {
            if (a[i] == name) return a[i + 1];
        }
        return null;
    }
}
