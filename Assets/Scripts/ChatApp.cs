using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// syncUp: hela appen i ett skript. Skapar sitt eget gränssnitt när du trycker Play,
/// så du behöver inte bygga någon scen eller några prefabs. Själva ritandet sköts av Ui.cs.
///
/// Skärmar:  Start  ->  (Konto)  ->  Chatt   (+ en ruta för att välja rapportorsak)
/// </summary>
[RequireComponent(typeof(NetClient))]
public class ChatApp : MonoBehaviour
{
    [Tooltip("Servern när den körs på den här datorn (Starta syncUp.bat, eller Play i Unity)")]
    public string localServer = "ws://localhost:8080";

    [Tooltip("Servern online, t.ex. på Render. Byt till din egen adress när du lagt upp servern (se README).")]
    public string onlineServer = "wss://syncup.onrender.com";

    const string PrefServerMode = "syncUp.serverMode";   // "local" eller "online", sparas mellan gångerna
    const string PrefServerUrl = "syncUp.serverUrl";
    static readonly string[] Reactions = { "♥", "★", "☺" }; // samma som servern (server/content.js)

    /// <summary>En pratbubbla som kan få reaktioner.</summary>
    class BubbleInfo
    {
        public GameObject row;
        public LayoutElement layout;
        public float baseWidth;
        public Text reactionsText;
        public readonly Dictionary<string, string> bySender = new Dictionary<string, string>();
    }

    // Intressen man kan välja: id som skickas till servern, och etiketten som visas
    static readonly (string id, string label)[] Interests =
    {
        ("spel", "Spel"), ("musik", "Musik"), ("sport", "Sport"), ("film", "Film & serier"), ("bocker", "Böcker"),
        ("mat", "Mat"), ("djur", "Djur"), ("resor", "Resor"), ("teknik", "Teknik"), ("konst", "Konst"),
    };
    static readonly string[] ReportReasons = { "Spam", "Elakt språk", "Olämpligt innehåll", "Annat" };

    const int MaxInterests = 5;
    const int MaxRows = 200;
    const float MaxBubbleWidth = 560f;
    const float TypingResendSeconds = 2.5f; // skicka "skriver" igen så här ofta medan man skriver
    const float TypingIdleSeconds = 3f;     // sluta skicka "skriver" när man varit still så här länge
    const float TypingShowSeconds = 6f;     // dölj partnerns "skriver" om inget hörts på så här länge

    static ChatApp instance;

    // Startar appen automatiskt i vilken scen som helst
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance == null) new GameObject("syncUp").AddComponent<ChatApp>();
    }

    // ---------------------------------------------------------------- Tillstånd

    NetClient net;
    Canvas canvas;
    AudioSource audioSource;
    AudioClip pling;

    GameObject homeScreen, authScreen, chatScreen, reportDialog;
    GameObject entryScreen;            // skärmen vi kom från (start eller konto), dit går vi tillbaka vid fel

    InputField serverInput, userInput, passInput, chatInput;
    Text homeStatus, authStatus, chatName, chatMeta, avatarLetter, typingText, reportTitle;
    Button botBtn, iceBtn, reportBtn, sendBtn;
    ScrollRect scroll;
    RectTransform content;

    readonly HashSet<string> chosenInterests = new HashSet<string>();
    readonly Dictionary<string, Button> interestButtons = new Dictionary<string, Button>();
    readonly Dictionary<int, BubbleInfo> bubbles = new Dictionary<int, BubbleInfo>();
    Button localBtn, onlineBtn;
    GameObject picker;                 // raden med reaktioner som visas under en bubbla man klickat på

    int scrollFrames;
    int online;
    bool canReport;
    string myName;
    string partner;
    string lastPartner;                // den senaste man chattat med, går att rapportera även efter att den lämnat
    bool partnerIsBot;
    string lastBubbleSender;           // för att gruppera bubblor från samma person
    string kickedReason;               // visas när servern kopplar bort oss på grund av admin
    Msg pendingHello;                  // första meddelandet som skickas när anslutningen är uppe

    float typingSentAt = -1f;          // när vi senast skickade "skriver", -1 = vi skriver inte
    float lastKeystrokeAt;
    float partnerTypingUntil;

    // ---------------------------------------------------------------- Unity

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        Application.runInBackground = true; // så att två fönster kan chatta med varandra vid test

        net = GetComponent<NetClient>();
        net.OnConnected += HandleConnected;
        net.OnDisconnected += HandleDisconnected;
        net.OnMessage += HandleMessage;

        EnsureCamera();
        SetUpSound();
        BuildUI();
        ShowScreen(homeScreen);
    }

    void Start()
    {
        EnsureEventSystem();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (net != null)
        {
            net.OnConnected -= HandleConnected;
            net.OnDisconnected -= HandleDisconnected;
            net.OnMessage -= HandleMessage;
        }
    }

    void Update()
    {
        if (typingText == null) return; // en dubblett som håller på att förstöras
        if (typingSentAt >= 0f && Time.unscaledTime - lastKeystrokeAt > TypingIdleSeconds) StopTyping();
        if (typingText.text.Length > 0 && Time.unscaledTime > partnerTypingUntil) SetPartnerTyping(false);
    }

    void LateUpdate()
    {
        // Scrolla ner till senaste meddelandet. Görs över två frames så att layouten hunnit räknas om.
        if (scrollFrames > 0 && scroll != null)
        {
            scrollFrames--;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 0f;
        }
    }

    void EnsureCamera()
    {
        if (Camera.allCamerasCount > 0) return;
        var cam = new GameObject("Camera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Ui.Bg;
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        go.AddComponent<StandaloneInputModule>();
#endif
    }

    static bool EnterPressed()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var kb = UnityEngine.InputSystem.Keyboard.current;
        return kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
#endif
    }

    // ---------------------------------------------------------------- Ljud

    /// <summary>Skapar ett kort "pling" i kod, så att inga ljudfiler behövs.</summary>
    void SetUpSound()
    {
        if (FindAnyObjectByType<AudioListener>() == null) gameObject.AddComponent<AudioListener>();
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;

        const int rate = 44100;
        const float seconds = 0.25f;
        var samples = new float[(int)(rate * seconds)];
        for (int i = 0; i < samples.Length; i++)
        {
            float t = (float)i / rate;
            samples[i] = Mathf.Sin(2f * Mathf.PI * 880f * t) * Mathf.Exp(-t * 18f) * 0.3f;
        }
        pling = AudioClip.Create("Pling", samples.Length, 1, rate, false);
        pling.SetData(samples, 0);
    }

    /// <summary>Spelar plinget, men bara när fönstret inte är i fokus.</summary>
    void Notify()
    {
        if (!Application.isFocused) audioSource.PlayOneShot(pling);
    }

    // ---------------------------------------------------------------- Gränssnitt: skärmar

    void BuildUI()
    {
        var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        BuildHome();
        BuildAuth();
        BuildChat();
        BuildReportDialog();
    }

    void BuildHome()
    {
        homeScreen = Ui.GradientScreen(canvas.transform, "Start", Ui.WarmTop, Ui.WarmBottom);
        Transform card = Ui.Card(homeScreen.transform, 520f, color: Ui.WarmCard);

        Ui.Label(card, "syncUp", 42, TextAnchor.MiddleCenter, Ui.TextColor, bold: true);
        Ui.Label(card, "Chatta med någon ny.", 18, TextAnchor.MiddleCenter, Ui.Muted);
        Ui.Spacer(card, 6f);

        Ui.Label(card, "Server", 15, TextAnchor.MiddleLeft, Ui.Muted);
        Transform modes = Ui.Row(card);
        localBtn = Ui.Chip(modes, "Den här datorn", () => SetServerMode("local"));
        onlineBtn = Ui.Chip(modes, "Online", () => SetServerMode("online"));
        serverInput = Ui.Input(card, "ws://localhost:8080", false, 100, 46f);
        serverInput.onEndEdit.AddListener(url => PlayerPrefs.SetString(PrefServerUrl, url.Trim()));
        LoadServerChoice();

        Ui.Label(card, "Intressen", 15, TextAnchor.MiddleLeft, Ui.Muted);
        Transform grid = Ui.Grid(card, 4, new Vector2(109f, 34f), 8f);
        foreach (var (id, label) in Interests)
        {
            string interest = id; // egen kopia till lambdan
            interestButtons[id] = Ui.Chip(grid, label, () => ToggleInterest(interest));
        }
        Ui.Spacer(card, 8f);

        Ui.Button(card, "Chatta anonymt", Ui.Coral, StartAnonymous, 19, 50f);
        Ui.Button(card, "Logga in", Ui.Glass, OpenAuth, 19, 50f);

        homeStatus = Ui.Label(card, "", 16, TextAnchor.MiddleCenter, Ui.Warning);
    }

    void BuildAuth()
    {
        authScreen = Ui.GradientScreen(canvas.transform, "Konto", Ui.WarmTop, Ui.WarmBottom);
        Transform card = Ui.Card(authScreen.transform, 460f, color: Ui.WarmCard);

        Ui.Label(card, "Logga in", 34, TextAnchor.MiddleCenter, Ui.TextColor, bold: true);
        Ui.Label(card, "Konton matchas bara med andra konton.", 16, TextAnchor.MiddleCenter, Ui.Muted);
        Ui.Spacer(card, 6f);

        userInput = Ui.Input(card, "Användarnamn", false, 16, 48f);
        passInput = Ui.Input(card, "Lösenord", true, 64, 48f);
        Ui.Spacer(card, 6f);

        Ui.Button(card, "Logga in", Ui.Coral, () => SubmitAuth("login"), 19, 50f);
        Ui.Button(card, "Skapa konto", Ui.Green, () => SubmitAuth("register"), 19, 50f);
        Ui.Button(card, "Tillbaka", Color.clear, () => ShowScreen(homeScreen), 17, 40f)
            .GetComponentInChildren<Text>().color = Ui.Muted;

        authStatus = Ui.Label(card, "", 16, TextAnchor.MiddleCenter, Ui.Warning);
    }

    void BuildChat()
    {
        const float topHeight = 64f, bottomHeight = 68f, typingHeight = 24f;
        chatScreen = Ui.Screen(canvas.transform, "Chatt", Ui.Bg);

        // Övre fältet: avatar, namn och antal online, sedan knappar
        RectTransform top = Ui.Bar(chatScreen.transform, true, topHeight);
        avatarLetter = Ui.Avatar(top, 42f);
        Transform who = Ui.Stack(top);
        Ui.Size(who, 60f).flexibleWidth = 1f;
        chatName = Ui.Label(who, "", 19, TextAnchor.MiddleLeft, Ui.TextColor, bold: true);
        chatMeta = Ui.Label(who, "", 14, TextAnchor.MiddleLeft, Ui.Muted);
        botBtn = TopButton(top, "Bot", Ui.Purple, OnBot, 64f);
        iceBtn = TopButton(top, "Fråga", Ui.Amber, OnIcebreaker, 76f);
        TopButton(top, "Nästa", Ui.Accent, OnNext, 82f);
        reportBtn = TopButton(top, "Rapportera", Ui.Red, OnReport, 112f);
        TopButton(top, "Lämna", Ui.Neutral, OnLeave, 78f);

        // Undre fältet: rund textruta + skicka
        RectTransform bottom = Ui.Bar(chatScreen.transform, false, bottomHeight);
        chatInput = Ui.Input(bottom, "Skriv ett meddelande...", false, 300, 48f, 24f);
        chatInput.onEndEdit.AddListener(OnChatEndEdit);
        chatInput.onValueChanged.AddListener(OnChatTyping);
        chatInput.GetComponent<LayoutElement>().flexibleWidth = 1f;
        sendBtn = Ui.Button(bottom, "Skicka", Ui.Accent, SendChat, 17, 48f, 24f);
        Ui.Size(sendBtn, 100f);

        // Precis ovanför textrutan: "X skriver..."
        typingText = Ui.Label(chatScreen.transform, "", 14, TextAnchor.MiddleLeft, Ui.Muted);
        var typingRect = typingText.rectTransform;
        typingRect.anchorMin = new Vector2(0f, 0f);
        typingRect.anchorMax = new Vector2(1f, 0f);
        typingRect.pivot = new Vector2(0.5f, 0f);
        typingRect.offsetMin = new Vector2(24f, bottomHeight);
        typingRect.offsetMax = new Vector2(-24f, bottomHeight + typingHeight);

        BuildMessageList(chatScreen.transform, bottomHeight + typingHeight, topHeight);
    }

    Button TopButton(Transform bar, string label, Color color, UnityEngine.Events.UnityAction onClick, float width)
    {
        Button button = Ui.Button(bar, label, color, onClick, 15, 38f);
        Ui.Size(button, width);
        return button;
    }

    /// <summary>Den scrollbara listan med meddelanden mellan övre och undre fältet.</summary>
    void BuildMessageList(Transform parent, float bottomOffset, float topOffset)
    {
        var scrollObject = new GameObject("Meddelanden", typeof(RectTransform), typeof(ScrollRect));
        scrollObject.transform.SetParent(parent, false);
        var scrollRect = (RectTransform)scrollObject.transform;
        scrollRect.anchorMin = Vector2.zero;
        scrollRect.anchorMax = Vector2.one;
        scrollRect.offsetMin = new Vector2(0f, bottomOffset);
        scrollRect.offsetMax = new Vector2(0f, -topOffset);

        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
        viewport.transform.SetParent(scrollObject.transform, false);
        Ui.Stretch((RectTransform)viewport.transform);
        viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f); // behövs för att kunna dra/scrolla

        var contentObject = new GameObject("Innehåll", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentObject.transform.SetParent(viewport.transform, false);
        content = (RectTransform)contentObject.transform;
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;

        var layout = contentObject.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(20, 20, 14, 10);
        layout.spacing = 3f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll = scrollObject.GetComponent<ScrollRect>();
        scroll.viewport = (RectTransform)viewport.transform;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;
    }

    /// <summary>En mörk ruta ovanpå chatten där man väljer varför man rapporterar.</summary>
    void BuildReportDialog()
    {
        reportDialog = Ui.Screen(canvas.transform, "Rapportera", new Color(0f, 0f, 0f, 0.6f));
        Transform card = Ui.Card(reportDialog.transform, 380f, 24, 8f);
        reportTitle = Ui.Label(card, "Rapportera", 22, TextAnchor.MiddleCenter, Ui.TextColor, bold: true);
        Ui.Label(card, "Välj orsak:", 16, TextAnchor.MiddleCenter, Ui.Muted);
        foreach (string reason in ReportReasons)
        {
            string chosen = reason; // egen kopia till lambdan
            Ui.Button(card, reason, Ui.Red, () => SendReport(chosen), 17, 44f);
        }
        Ui.Button(card, "Avbryt", Color.clear, () => reportDialog.SetActive(false), 16, 38f)
            .GetComponentInChildren<Text>().color = Ui.Muted;
        reportDialog.SetActive(false);
    }

    void ShowScreen(GameObject screen)
    {
        homeScreen.SetActive(screen == homeScreen);
        authScreen.SetActive(screen == authScreen);
        chatScreen.SetActive(screen == chatScreen);
        reportDialog.SetActive(false);
    }

    // ---------------------------------------------------------------- Knappar

    string serverMode = "local";

    /// <summary>Läser in förra valet (lokal eller online), annars lokal.</summary>
    void LoadServerChoice()
    {
        SetServerMode(PlayerPrefs.GetString(PrefServerMode, "local"), keepSavedUrl: true);
    }

    /// <summary>
    /// Väljer var servern finns. Adressfältet fylls i, men går att ändra, t.ex. till en klasskompis
    /// dator i nätverket (ws://192.168.1.23:8080).
    /// </summary>
    void SetServerMode(string mode, bool keepSavedUrl = false)
    {
        serverMode = mode == "online" ? "online" : "local";
        Ui.SetChip(localBtn, serverMode == "local");
        Ui.SetChip(onlineBtn, serverMode == "online");
        PlayerPrefs.SetString(PrefServerMode, serverMode);

        string saved = PlayerPrefs.GetString(PrefServerUrl, "");
        serverInput.text = keepSavedUrl && saved.Length > 0 ? saved : DefaultServerUrl();
        PlayerPrefs.SetString(PrefServerUrl, serverInput.text);
    }

    string DefaultServerUrl() => serverMode == "online" ? onlineServer : localServer;

    void ToggleInterest(string id)
    {
        if (chosenInterests.Contains(id)) chosenInterests.Remove(id);
        else if (chosenInterests.Count < MaxInterests) chosenInterests.Add(id);
        Ui.SetChip(interestButtons[id], chosenInterests.Contains(id));
    }

    void StartAnonymous()
    {
        BeginConnect(new Msg { type = "anon" }, homeScreen);
    }

    void OpenAuth()
    {
        authStatus.text = "";
        ShowScreen(authScreen);
    }

    void SubmitAuth(string type)
    {
        string name = userInput.text.Trim();
        string password = passInput.text;

        if (name.Length < 3)
        {
            authStatus.text = "Skriv ett användarnamn med minst 3 tecken.";
            return;
        }
        if (password.Length == 0 || (type == "register" && password.Length < 6))
        {
            authStatus.text = type == "register" ? "Lösenordet måste vara minst 6 tecken." : "Skriv ditt lösenord.";
            return;
        }

        BeginConnect(new Msg { type = type, name = name, password = password }, authScreen);
    }

    void OnNext()
    {
        ClearChat();
        StopTyping();
        SetPartnerTyping(false);
        canReport = false;
        net.Send(new Msg { type = "next" });
    }

    void OnBot()
    {
        net.Send(new Msg { type = "bot" });
    }

    void OnIcebreaker()
    {
        net.Send(new Msg { type = "icebreaker" });
    }

    void OnReport()
    {
        reportTitle.text = "Rapportera " + lastPartner;
        reportDialog.SetActive(true);
        reportDialog.transform.SetAsLastSibling();
    }

    void SendReport(string reason)
    {
        net.Send(new Msg { type = "report", reason = reason });
        reportDialog.SetActive(false);
    }

    void OnLeave()
    {
        net.Close();
        ResetSession();
        homeStatus.text = "";
        authStatus.text = "";
        ShowScreen(homeScreen);
    }

    void OnChatEndEdit(string _)
    {
        // onEndEdit körs både vid Enter och när fältet tappar fokus, så vi kollar att det var Enter
        if (EnterPressed()) SendChat();
    }

    void SendChat()
    {
        string text = chatInput.text.Trim();
        if (text.Length == 0 || partner == null) return;
        net.Send(new Msg { type = "chat", text = text });
        StopTyping();
        chatInput.text = "";
        chatInput.ActivateInputField();
    }

    // ---------------------------------------------------------------- "Skriver..."

    void OnChatTyping(string value)
    {
        if (partner == null || value.Length == 0) return;
        lastKeystrokeAt = Time.unscaledTime;
        if (typingSentAt < 0f || Time.unscaledTime - typingSentAt > TypingResendSeconds)
        {
            typingSentAt = Time.unscaledTime;
            net.Send(new Msg { type = "typing", on = true });
        }
    }

    void StopTyping()
    {
        if (typingSentAt < 0f) return;
        typingSentAt = -1f;
        net.Send(new Msg { type = "typing", on = false });
    }

    void SetPartnerTyping(bool on)
    {
        typingText.text = on && partner != null ? partner + " skriver..." : "";
        if (on) partnerTypingUntil = Time.unscaledTime + TypingShowSeconds;
    }

    // ---------------------------------------------------------------- Nätverk

    void BeginConnect(Msg hello, GameObject entry)
    {
        entryScreen = entry;
        pendingHello = hello;
        kickedReason = null;

        string url = serverInput.text.Trim();
        if (url.Length == 0) url = DefaultServerUrl();
        if (url.StartsWith("http://")) url = "ws://" + url.Substring("http://".Length);
        else if (url.StartsWith("https://")) url = "wss://" + url.Substring("https://".Length);
        else if (!url.StartsWith("ws://") && !url.StartsWith("wss://")) url = "ws://" + url;

        SetEntryStatus("Ansluter...");
        net.Connect(url);
    }

    void SetEntryStatus(string text)
    {
        homeStatus.text = entryScreen == homeScreen ? text : "";
        authStatus.text = entryScreen == authScreen ? text : "";
    }

    void HandleConnected()
    {
        if (pendingHello == null) return;
        net.Send(pendingHello);
        pendingHello = null;
    }

    void HandleDisconnected(string reason)
    {
        ResetSession();
        ShowScreen(entryScreen != null ? entryScreen : homeScreen);
        if (kickedReason != null) reason = kickedReason;
        SetEntryStatus(string.IsNullOrEmpty(reason) ? "Anslutningen avbröts." : reason);
    }

    void HandleMessage(Msg m)
    {
        switch (m.type)
        {
            case "welcome":
            case "auth_ok":
                myName = m.name;
                ClearChat();
                ShowScreen(chatScreen);
                AddSystem("Du är " + myName + ".");
                net.Send(new Msg { type = "find", interests = chosenInterests.ToArray() });
                RefreshChatUi();
                break;

            case "auth_error":
                net.Close();
                ShowScreen(authScreen);
                authStatus.text = m.error;
                break;

            case "searching":
                partner = null;
                canReport = false;
                RefreshChatUi();
                AddSystem("Söker...");
                break;

            case "matched":
                partner = m.partner;
                lastPartner = m.partner;
                partnerIsBot = m.bot;
                canReport = !m.bot;
                ClearChat();
                AddSystem("Ansluten till " + partner + ".");
                if (m.common != null && m.common.Length > 0)
                    AddSystem("Gemensamt: " + string.Join(", ", m.common.Select(InterestLabel)).ToLower() + ".");
                RefreshChatUi();
                chatInput.ActivateInputField();
                break;

            case "chat":
                if (m.sender != myName)
                {
                    SetPartnerTyping(false);
                    Notify();
                }
                AddChat(m.sender, m.text, m.id);
                break;

            case "reaction":
                SetReaction(m.id, m.sender, m.text);
                break;

            case "typing":
                SetPartnerTyping(m.on);
                break;

            case "icebreaker":
                AddIcebreaker(m.sender, m.text);
                break;

            case "partner_left":
                AddSystem(partner + " lämnade.");
                partner = null;
                SetPartnerTyping(false);
                RefreshChatUi();
                break;

            case "info":
                AddSystem(m.text);
                break;

            case "announce":
                AddNotice("Meddelande från admin", m.text);
                Notify();
                break;

            case "kicked":
                kickedReason = m.text;
                break;

            case "online":
                online = m.online;
                RefreshChatUi();
                break;
        }
    }

    static string InterestLabel(string id)
    {
        foreach (var (key, label) in Interests)
            if (key == id) return label;
        return id;
    }

    void ResetSession()
    {
        partner = null;
        lastPartner = null;
        partnerIsBot = false;
        myName = null;
        pendingHello = null;
        online = 0;
        canReport = false;
        typingSentAt = -1f;
        SetPartnerTyping(false);
        ClearChat();
        RefreshChatUi();
    }

    void RefreshChatUi()
    {
        bool hasPartner = partner != null;
        chatName.text = hasPartner ? partner : "Söker...";
        var meta = new List<string>();
        if (hasPartner && partnerIsBot) meta.Add("testbot");
        if (online > 0) meta.Add(online + " online");
        chatMeta.text = string.Join("  ·  ", meta);
        Ui.SetAvatar(avatarLetter, hasPartner ? partner : null);
        reportBtn.interactable = canReport;
        iceBtn.interactable = hasPartner;
        chatInput.interactable = hasPartner;
        sendBtn.interactable = hasPartner;
        botBtn.gameObject.SetActive(!hasPartner);
    }

    // ---------------------------------------------------------------- Meddelandelistan

    void ClearChat()
    {
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            GameObject row = content.GetChild(i).gameObject;
            row.SetActive(false); // så att layouten slutar räkna den direkt
            Destroy(row);
        }
        lastBubbleSender = null;
        bubbles.Clear();
        picker = null;
    }

    /// <summary>
    /// En pratbubbla: mina till höger i blått, partnerns till vänster i grått, med tid bredvid.
    /// Klicka på en bubbla för att reagera på den.
    /// </summary>
    void AddChat(string sender, string text, int id)
    {
        bool mine = sender == myName;
        if (lastBubbleSender != sender) AddGap(8f); // lite luft när en ny person börjar skriva
        lastBubbleSender = sender;

        Transform row = NewRow(mine ? TextAnchor.LowerRight : TextAnchor.LowerLeft);
        row.GetComponent<HorizontalLayoutGroup>().reverseArrangement = mine;
        GameObject bubble = Box(row, mine ? Ui.Accent : Ui.Bubble, 18f, MaxBubbleWidth,
            Ui.Label(null, text, 19, TextAnchor.UpperLeft, Ui.TextColor));
        Ui.Label(row, DateTime.Now.ToString("HH:mm"), 12, TextAnchor.LowerLeft, Ui.Muted);

        if (id > 0)
        {
            var layout = bubble.GetComponent<LayoutElement>();
            bubbles[id] = new BubbleInfo { row = row.gameObject, layout = layout, baseWidth = layout.preferredWidth };
            bubble.GetComponent<Image>().raycastTarget = true; // så att bubblan går att klicka på
            var button = bubble.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => TogglePicker(id, mine));
        }
        Trim();
    }

    // ---------------------------------------------------------------- Reaktioner

    /// <summary>Visar (eller döljer) en liten rad med ♥ ★ ☺ precis under bubblan.</summary>
    void TogglePicker(int id, bool mine)
    {
        if (!bubbles.TryGetValue(id, out BubbleInfo info) || info.row == null) return;
        bool sameBubble = picker != null && picker.transform.GetSiblingIndex() == info.row.transform.GetSiblingIndex() + 1;
        ClosePicker();
        if (sameBubble) return;

        Transform row = NewRow(mine ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft);
        row.GetComponent<HorizontalLayoutGroup>().spacing = 4f;
        foreach (string emoji in Reactions)
        {
            string chosen = emoji; // egen kopia till lambdan
            Button b = Ui.Button(row, emoji, Ui.Panel, () =>
            {
                net.Send(new Msg { type = "react", id = id, text = chosen });
                ClosePicker();
            }, 18, 36f, 18f);
            Ui.Size(b, 36f);
        }
        row.SetSiblingIndex(info.row.transform.GetSiblingIndex() + 1);
        picker = row.gameObject;
    }

    void ClosePicker()
    {
        if (picker == null) return;
        picker.SetActive(false);
        Destroy(picker);
        picker = null;
    }

    /// <summary>Uppdaterar en reaktion och visar dem längst ner i bubblan, t.ex. "♥ 2  ★".</summary>
    void SetReaction(int id, string sender, string emoji)
    {
        if (!bubbles.TryGetValue(id, out BubbleInfo info) || info.row == null) return;
        if (string.IsNullOrEmpty(emoji)) info.bySender.Remove(sender);
        else info.bySender[sender] = emoji;

        var parts = new List<string>();
        foreach (string e in Reactions)
        {
            int count = info.bySender.Values.Count(v => v == e);
            if (count > 0) parts.Add(count > 1 ? e + " " + count : e);
        }
        string label = string.Join("   ", parts);

        if (info.reactionsText == null)
        {
            info.reactionsText = Ui.Label(info.layout.transform, "", 14, TextAnchor.MiddleRight, Ui.TextColor);
        }
        info.reactionsText.text = label;
        info.reactionsText.gameObject.SetActive(label.Length > 0);

        // Gör bubblan bredare om reaktionerna inte får plats
        float needed = info.reactionsText.preferredWidth + 30f;
        info.layout.preferredWidth = Mathf.Min(MaxBubbleWidth, Mathf.Max(info.baseWidth, label.Length > 0 ? needed : 0f));
        scrollFrames = Mathf.Max(scrollFrames, 1);
    }

    /// <summary>En liten grå rad i mitten, t.ex. "Söker..." eller "Ansluten till X."</summary>
    void AddSystem(string text)
    {
        lastBubbleSender = null;
        AddGap(6f);
        Transform row = NewRow(TextAnchor.MiddleCenter);
        Ui.Label(row, text, 14, TextAnchor.MiddleCenter, Ui.Muted);
        AddGap(6f);
        Trim();
    }

    void AddIcebreaker(string sender, string question)
    {
        AddNotice("Fråga från " + (sender == myName ? "dig" : sender), question);
    }

    /// <summary>En ruta i mitten med en liten rubrik, för isbrytare och meddelanden från admin.</summary>
    void AddNotice(string title, string body)
    {
        lastBubbleSender = null;
        AddGap(8f);
        Transform row = NewRow(TextAnchor.MiddleCenter);
        Text heading = Ui.Label(null, title, 12, TextAnchor.UpperCenter, Ui.Muted, bold: true);
        Text text = Ui.Label(null, body, 17, TextAnchor.UpperCenter, Ui.TextColor);
        Box(row, Ui.Panel, 12f, MaxBubbleWidth, heading, text);
        AddGap(8f);
        Trim();
    }

    /// <summary>En rad i listan där barnen ligger bredvid varandra.</summary>
    Transform NewRow(TextAnchor alignment)
    {
        var go = new GameObject("Rad", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(content, false);
        var layout = go.GetComponent<HorizontalLayoutGroup>();
        layout.childAlignment = alignment;
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        return go.transform;
    }

    /// <summary>
    /// En rundad ruta runt en eller flera texter. Bredden följer texten, men aldrig bredare än maxWidth
    /// (då radbryts texten i stället). Texterna skapas utan förälder och flyttas in här.
    /// </summary>
    static GameObject Box(Transform row, Color color, float radius, float maxWidth, params Text[] texts)
    {
        var go = new GameObject("Bubbla", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(row, false);
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        Ui.Round(image, radius);

        var layout = go.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 8, 9);
        layout.spacing = 2f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        float textWidth = 0f;
        foreach (Text t in texts)
        {
            t.transform.SetParent(go.transform, false);
            textWidth = Mathf.Max(textWidth, t.preferredWidth);
        }
        go.GetComponent<LayoutElement>().preferredWidth = Mathf.Min(textWidth + layout.padding.horizontal + 2f, maxWidth);
        return go;
    }

    void AddGap(float height)
    {
        Ui.Spacer(content, height);
    }

    void Trim()
    {
        // Ta bort den äldsta raden när listan blir för lång. Destroy sker först i slutet av framen,
        // så raden stängs av direkt för att inte räknas (eller tas bort) två gånger.
        int active = 0;
        for (int i = 0; i < content.childCount; i++)
            if (content.GetChild(i).gameObject.activeSelf) active++;
        for (int i = 0; i < content.childCount && active > MaxRows; i++)
        {
            GameObject child = content.GetChild(i).gameObject;
            if (!child.activeSelf) continue;
            child.SetActive(false);
            Destroy(child);
            active--;
        }

        scrollFrames = 2;
    }
}
