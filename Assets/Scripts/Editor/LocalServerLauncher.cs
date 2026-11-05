using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Startar servern när man trycker Play och stänger den när man slutar.
// Gör inget om en server redan körs. Kan stängas av i menyn syncUp.
[InitializeOnLoad]
static class LocalServerLauncher
{
    const int Port = 8080;
    const string MenuPath = "syncUp/Starta servern vid Play";
    const string EnabledPref = "syncUp.autoStartServer";
    // Skripten laddas om när Play startar, så process-id:t måste sparas här
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
        Debug.Log("syncUp: servern startas " + (Enabled ? "vid Play." : "inte längre vid Play."));
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
            Debug.Log($"syncUp: en server körs redan på port {Port}.");
            return;
        }

        string dir = FindServerFolder();
        if (dir == null)
        {
            Debug.LogWarning("syncUp: hittade inte server/server.js. Starta servern själv.");
            return;
        }

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
            Debug.Log($"syncUp: servern startad. Webb: http://localhost:{Port}   " +
                      $"Admin: http://localhost:{Port}/admin?key={adminKey}");
        }
        catch (Win32Exception)
        {
            Debug.LogWarning("syncUp: kunde inte starta servern. Är Node.js installerat? https://nodejs.org");
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
            // Kolla att det är node, id:t kan ha återanvänts av ett annat program
            if (!process.HasExited && process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill();
                Debug.Log("syncUp: servern stängd.");
            }
        }
        catch (ArgumentException)
        {
            // processen finns inte längre
        }
        catch (InvalidOperationException)
        {
            // hann avslutas under tiden
        }
    }

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
            return false; // ingen svarade
        }
    }
}
