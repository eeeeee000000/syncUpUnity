using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Ställer in bygget en gång: fönster i 1280x720 som går att ändra storlek på, ikonen och att appen
/// fortsätter köra i bakgrunden (så att två fönster kan chatta med varandra).
/// Körs automatiskt första gången projektet öppnas, och kan köras igen från menyn syncUp.
/// </summary>
[InitializeOnLoad]
static class SyncUpBuildSetup
{
    const string IconPath = "Assets/Icon/syncUp.png";
    // Ligger i Library/, som inte sparas i git, så en ny kopia av projektet ställs in en gång
    const string DoneMarker = "Library/syncUpBuildSetupDone";

    static SyncUpBuildSetup()
    {
        if (File.Exists(DoneMarker)) return;
        // Vänta tills Unity laddat klart, annars hittas inte ikonen än
        EditorApplication.delayCall += () =>
        {
            if (Apply()) File.WriteAllText(DoneMarker, "Inställningarna för bygget är gjorda.");
        };
    }

    [MenuItem("syncUp/Ställ in bygget (fönster och ikon)")]
    static void ApplyFromMenu()
    {
        if (Apply()) Debug.Log("syncUp: bygget är inställt (1280x720-fönster och ikon).");
    }

    /// <summary>Returnerar false om ikonen inte kunde laddas, så att det försöks igen nästa gång.</summary>
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
            Debug.LogWarning("syncUp: hittade inte ikonen " + IconPath + ", försöker igen nästa gång.");
            return false;
        }
        PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
        AssetDatabase.SaveAssets();
        return true;
    }
}
