using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

// Ställer in bygget: fönster 1280x720, ikon och att appen körs i bakgrunden.
// Körs en gång automatiskt, och finns i menyn syncUp.
[InitializeOnLoad]
static class SyncUpBuildSetup
{
    const string IconPath = "Assets/Icon/syncUp.png";
    const string DoneMarker = "Library/syncUpBuildSetupDone";

    static SyncUpBuildSetup()
    {
        if (File.Exists(DoneMarker)) return;
        // Vänta tills Unity laddat klart, annars hittas inte ikonen
        EditorApplication.delayCall += () =>
        {
            if (Apply()) File.WriteAllText(DoneMarker, "klar");
        };
    }

    [MenuItem("syncUp/Ställ in bygget (fönster och ikon)")]
    static void ApplyFromMenu()
    {
        if (Apply()) Debug.Log("syncUp: bygget är inställt.");
    }

    static bool Apply()
    {
        PlayerSettings.productName = "syncUp";
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;

        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
        if (icon == null)
        {
            Debug.LogWarning("syncUp: hittade inte " + IconPath);
            return false;
        }
        PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
        AssetDatabase.SaveAssets();
        return true;
    }
}
