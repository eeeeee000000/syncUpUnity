using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using Debug = UnityEngine.Debug;

/// <summary>
/// Startar syncUp-servern (Node.js) automatiskt när du trycker Play i Unity, och stänger den när du
/// slutar. Gör ingenting om en server redan körs på port 8080, t.ex. från "Starta syncUp.bat".
/// Stängs av och på i menyn syncUp > Starta servern vid Play.
/// </summary>
[InitializeOnLoad]
static class LocalServerLauncher
{
    const int Port = 8080;
    const string MenuPath = "syncUp/Starta servern vid Play";
    const string EnabledPref = "syncUp.autoStartServer";
    // Unity laddar om alla skript när Play startar, så processens id sparas här i stället för i en vanlig variabel
    const string PidKey = "syncUp.serverPid";

    static LocalServerLauncher()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.quitting += StopServer;
    }

    static bool Enabled
    {
        get => EditorPrefs.GetBool(EnabledPref, true);
        set => EditorPrefs.SetBool(EnabledPref, value);
    }

    [MenuItem(MenuPath)]
    static void ToggleEnabled()
    {
        Enabled = !Enabled;
        Debug.Log("syncUp: servern startas " + (Enabled ? "automatiskt vid Play." : "inte längre vid Play."));
    }

    [MenuItem(MenuPath, true)]
    static bool ToggleEnabledValidate()
    {
        Menu.SetChecked(MenuPath, Enabled);
        return true;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode && Enabled) StartServer();
        if (state == PlayModeStateChange.EnteredEditMode) StopServer();
    }

    static void StartServer()
    {
        if (PortInUse())
        {
            Debug.Log($"syncUp: en server körs redan på port {Port}, så ingen ny startas.");
            return;
        }

        string dir = FindServerFolder();
        if (dir == null)
        {
            Debug.LogWarning("syncUp: hittade inte server-mappen (server/server.js). Starta servern själv.");
            return;
        }

        // En egen nyckel varje gång, som skrivs ut här nedan så att du kan öppna adminpanelen
        string adminKey = Guid.NewGuid().ToString("N").Substring(0, 12);
        var info = new ProcessStartInfo("node", "server.js")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.EnvironmentVariables["ADMIN_KEY"] = adminKey;
        info.EnvironmentVariables["PORT"] = Port.ToString();

        try
        {
            Process process = Process.Start(info);
            SessionState.SetInt(PidKey, process.Id);
            Debug.Log($"syncUp: startade servern. Webb: http://localhost:{Port}   " +
                      $"Adminpanel: http://localhost:{Port}/admin?key={adminKey}");
        }
        catch (Win32Exception)
        {
            Debug.LogWarning("syncUp: kunde inte starta servern. Är Node.js installerat? Ladda ner från https://nodejs.org");
        }
    }

    static void StopServer()
    {
        int pid = SessionState.GetInt(PidKey, 0);
        if (pid == 0) return;
        SessionState.EraseInt(PidKey);
        try
        {
            Process process = Process.GetProcessById(pid);
            // Kontrollera att det fortfarande är vår node-process och inte ett annat program som fått samma id
            if (!process.HasExited && process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill();
                Debug.Log("syncUp: stängde servern.");
            }
        }
        catch (ArgumentException)
        {
            // Processen finns inte längre, inget att stänga.
        }
        catch (InvalidOperationException)
        {
            // Processen hann avslutas medan vi tittade på den.
        }
    }

    /// <summary>server/ ligger i Unity-projektet, eller bredvid det (äldre mappstruktur).</summary>
    static string FindServerFolder()
    {
        string project = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        foreach (string candidate in new[] { Path.Combine(project, "server"), Path.Combine(project, "..", "server") })
        {
            if (File.Exists(Path.Combine(candidate, "server.js"))) return Path.GetFullPath(candidate);
        }
        return null;
    }

    static bool PortInUse()
    {
        try
        {
            using (var client = new TcpClient())
            {
                return client.ConnectAsync("127.0.0.1", Port).Wait(300) && client.Connected;
            }
        }
        catch (Exception)
        {
            return false; // ingen svarade, porten är ledig
        }
    }
}
