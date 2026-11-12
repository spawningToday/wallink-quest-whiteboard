using System.IO;
using UnityEditor;

namespace WallInk.EditorTools
{
    /// <summary>
    /// On the first editor session after opening the project, offers to run the setup command
    /// if the generated scene does not exist yet. Asked once per editor session at most.
    /// </summary>
    [InitializeOnLoad]
    static class WallInkFirstRun
    {
        const string AskedKey = "WallInk.FirstRunAsked";

        static WallInkFirstRun()
        {
            if (UnityEngine.Application.isBatchMode) return;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            // The Meta project config is unavailable until the Meta SDK has finished loading.
            if (OVRProjectConfig.CachedProjectConfig == null) return;
            EditorApplication.update -= Tick;
            if (File.Exists(WallInkSetup.ScenePath) || SessionState.GetBool(AskedKey, false)) return;
            SessionState.SetBool(AskedKey, true);
            if (EditorUtility.DisplayDialog("WallInk",
                    "This project has not been set up yet.\n\nRun 'WallInk > Setup Project and Scene' now? It configures the project for " +
                    "Quest 3 (OpenXR, Meta XR features, URP, Android settings) and generates the app scene.",
                    "Run setup", "Later"))
                WallInkSetup.SetupFromMenu();
        }
    }
}
