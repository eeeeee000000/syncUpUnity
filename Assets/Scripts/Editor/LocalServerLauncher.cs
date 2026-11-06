using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using Debug = UnityEngine.Debug;

// Kör "Starta syncUp.bat" när man trycker Play, samma fönster och webbläsarflikar
// som när man dubbelklickar på den. Stängs när man slutar köra.
// Gör inget om en server redan körs. Kan stängas av i menyn syncUp.
[InitializeOnLoad]
static class LocalServerLauncher
{
    const int Port = 8080;
    const string MenuPath = "syncUp/Starta servern vid Play";
    const string EnabledPref = "syncUp.autoStartServer";
    // Skripten laddas om när Play startar, så process-id och läsposition måste sparas här
    const string PidKey = "syncUp.serverPid";
    const string LogOffsetKey = "syncUp.serverLogOffset";
    const double LogPollSeconds = 0.5;
    const string StartScript = "Starta syncUp.bat";

    static double nextLogPoll;

    static LocalServerLauncher()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.quitting += StopServer;
        EditorApplication.update += PollLog;
    }

    static string LogPath => Path.Combine(ProjectFolder, "Temp", "syncUp-server.log");

    // Visar nya rader från serverns logg i Console
    static void PollLog()
    {
        if (SessionState.GetInt(PidKey, 0) == 0) return;
        if (EditorApplication.timeSinceStartup < nextLogPoll) return;
        nextLogPoll = EditorApplication.timeSinceStartup + LogPollSeconds;
        ReadNewLogLines();
    }

    static void ReadNewLogLines()
    {
        if (!File.Exists(LogPath)) return;
        try
        {
            // FileShare.ReadWrite eftersom servern skriver i filen samtidigt
            using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long offset = SessionState.GetInt(LogOffsetKey, 0);
                if (stream.Length <= offset) return;
                stream.Seek(offset, SeekOrigin.Begin);
                var bytes = new byte[stream.Length - offset];
                int read = stream.Read(bytes, 0, bytes.Length);
                // bara hela rader, resten tas nästa gång
                int end = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                if (end < 0) return;
                string text = System.Text.Encoding.UTF8.GetString(bytes, 0, end + 1);
                SessionState.SetInt(LogOffsetKey, (int)(offset + end + 1));
                foreach (string line in text.Split('\n'))
                {
                    if (line.Trim().Length > 0) Debug.Log("[server] " + line.TrimEnd('\r'));
                }
            }
        }
        catch (IOException)
        {
            // filen är upptagen just nu, försöker igen om en halv sekund
        }
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

        string bat = Path.Combine(ProjectFolder, StartScript);
        if (!File.Exists(bat))
        {
            Debug.LogWarning($"syncUp: hittade inte {StartScript}. Starta servern själv.");
            return;
        }

        // Inget CreateNoWindow, så cmd får ett eget fönster precis som vid dubbelklick
        var info = new ProcessStartInfo("cmd.exe", $"/c \"\"{bat}\"\"")
        {
            WorkingDirectory = ProjectFolder,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        info.EnvironmentVariables["PORT"] = Port.ToString();
        // Loggen kopieras även till Unitys Console
        info.EnvironmentVariables["LOG_FILE"] = LogPath;
        SessionState.SetInt(LogOffsetKey, 0);

        try
        {
            Process process = Process.Start(info);
            SessionState.SetInt(PidKey, process.Id);
            Debug.Log("syncUp: servern startad i ett eget fönster. Adminlänken står där och i Console.");
        }
        catch (Win32Exception e)
        {
            Debug.LogWarning("syncUp: kunde inte starta servern: " + e.Message);
        }
    }

    static void StopServer()
    {
        int pid = SessionState.GetInt(PidKey, 0);
        if (pid == 0) return;
        ReadNewLogLines(); // det sista innan servern stängs
        SessionState.EraseInt(PidKey);
        try
        {
            Process process = Process.GetProcessById(pid);
            // Kolla att det är vårt cmd-fönster, id:t kan ha återanvänts av ett annat program
            if (!process.HasExited && process.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                // /T stänger även node som körs inuti fönstret
                var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                kill.WaitForExit(3000);
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

    static string ProjectFolder => Directory.GetParent(UnityEngine.Application.dataPath).FullName;

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
