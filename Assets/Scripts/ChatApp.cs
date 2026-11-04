using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Hela appen i ett skript. Skapar sitt eget gränssnitt när du trycker Play,
/// så du behöver inte bygga någon scen eller några prefabs.
///
/// Skärmar:  Start  ->  (Konto)  ->  Chatt
/// </summary>
[RequireComponent(typeof(NetClient))]
public class ChatApp : MonoBehaviour
{
    [Tooltip("Serveradressen som fylls i från början på startskärmen")]
    public string defaultServer = "ws://localhost:8080";

    static ChatApp instance;

    // Startar appen automatiskt i vilken scen som helst
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance == null) new GameObject("StrangerChat").AddComponent<ChatApp>();
    }

    // ---------------------------------------------------------------- Färger

    static readonly Color Bg = new Color(0.07f, 0.09f, 0.13f);
    static readonly Color PanelColor = new Color(0.12f, 0.15f, 0.21f);
    static readonly Color Accent = new Color(0.31f, 0.51f, 1f);
    static readonly Color Green = new Color(0.20f, 0.65f, 0.42f);
    static readonly Color Red = new Color(0.78f, 0.28f, 0.28f);
    static readonly Color Grey = new Color(0.30f, 0.35f, 0.45f);
    static readonly Color Muted = new Color(1f, 1f, 1f, 0.55f);
    static readonly Color Warning = new Color(1f, 0.78f, 0.35f);

    const string ColorMe = "#7EE0A1";
    const string ColorThem = "#8AB4FF";
    const string ColorSystem = "#9AA4B5";

    // ---------------------------------------------------------------- Tillstånd

    NetClient net;
    Font font;
    Canvas canvas;

    GameObject homeScreen, authScreen, chatScreen;
    GameObject entryScreen;            // skärmen vi kom från (start eller konto), dit går vi tillbaka vid fel

    InputField serverInput, userInput, passInput, chatInput;
    Text homeStatus, authStatus, chatStatus;
    Button nextBtn, reportBtn, sendBtn;
    ScrollRect scroll;
    RectTransform content;

    int rowCount;
    int scrollFrames;
    int online;
    bool canReport;
    string myName;
    string partner;
    Msg pendingHello;                  // första meddelandet som skickas när anslutningen är uppe

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

        font = Font.CreateDynamicFontFromOSFont(
            new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" }, 16);

        EnsureCamera();
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
        cam.backgroundColor = Bg;
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
    }

    void BuildHome()
    {
        homeScreen = MakeScreen("Start");
        Transform col = MakeColumn(homeScreen.transform, 560f);

        var title = MakeText(col, "StrangerChat", 56, TextAnchor.MiddleCenter, Color.white);
        title.fontStyle = FontStyle.Bold;
        MakeText(col, "Möt nya människor genom text.", 24, TextAnchor.MiddleCenter, Muted);
        MakeSpacer(col, 12f);

        MakeText(col, "Serveradress", 18, TextAnchor.MiddleLeft, Muted);
        serverInput = MakeInput(col, "ws://localhost:8080", false);
        serverInput.text = defaultServer;
        MakeSpacer(col, 6f);

        MakeButton(col, "Chatta anonymt", Accent, StartAnonymous, 24, 70f);
        MakeButton(col, "Logga in / Skapa konto", Grey, OpenAuth, 24, 70f);

        homeStatus = MakeText(col, "", 20, TextAnchor.MiddleCenter, Warning);
        MakeText(col, "Skolprojekt. Dela aldrig personlig information med främlingar.", 16,
            TextAnchor.MiddleCenter, Muted);
    }

    void BuildAuth()
    {
        authScreen = MakeScreen("Konto");
        Transform col = MakeColumn(authScreen.transform, 560f);

        var title = MakeText(col, "Konto", 44, TextAnchor.MiddleCenter, Color.white);
        title.fontStyle = FontStyle.Bold;
        MakeText(col, "Med konto chattar du bara med andra som också har konto.", 20,
            TextAnchor.MiddleCenter, Muted);
        MakeSpacer(col, 8f);

        userInput = MakeInput(col, "Användarnamn (3–16 tecken)", false);
        userInput.characterLimit = 16;
        passInput = MakeInput(col, "Lösenord (minst 6 tecken)", true);
        passInput.characterLimit = 64;
        MakeSpacer(col, 6f);

        MakeButton(col, "Logga in", Accent, () => SubmitAuth("login"), 24, 66f);
        MakeButton(col, "Skapa konto", Green, () => SubmitAuth("register"), 24, 66f);
        MakeButton(col, "Tillbaka", Grey, () => ShowScreen(homeScreen), 22, 56f);

        authStatus = MakeText(col, "", 20, TextAnchor.MiddleCenter, Warning);
    }

    void BuildChat()
    {
        chatScreen = MakeScreen("Chatt");

        // Övre fältet: status + knappar
        RectTransform top = MakeBar(chatScreen.transform, true, 70f);
        chatStatus = MakeText(top, "", 22, TextAnchor.MiddleLeft, Color.white);
        var statusLayout = chatStatus.gameObject.AddComponent<LayoutElement>();
        statusLayout.flexibleWidth = 1f;
        statusLayout.minWidth = 60f;
        nextBtn = MakeButton(top, "Nästa", Accent, OnNext, 20, 54f);
        SetWidth(nextBtn, 110f);
        reportBtn = MakeButton(top, "Rapportera", Red, OnReport, 20, 54f);
        SetWidth(reportBtn, 150f);
        var leaveBtn = MakeButton(top, "Lämna", Grey, OnLeave, 20, 54f);
        SetWidth(leaveBtn, 100f);

        // Undre fältet: textruta + skicka
        RectTransform bottom = MakeBar(chatScreen.transform, false, 76f);
        chatInput = MakeInput(bottom, "Skriv ett meddelande...", false, 60f);
        chatInput.characterLimit = 300;
        chatInput.onEndEdit.AddListener(OnChatEndEdit);
        chatInput.GetComponent<LayoutElement>().flexibleWidth = 1f;
        sendBtn = MakeButton(bottom, "Skicka", Green, SendChat, 22, 60f);
        SetWidth(sendBtn, 120f);

        // Mitten: scrollbar lista med meddelanden
        var scrollObject = new GameObject("Meddelanden", typeof(RectTransform), typeof(ScrollRect));
        scrollObject.transform.SetParent(chatScreen.transform, false);
        var scrollRect = (RectTransform)scrollObject.transform;
        scrollRect.anchorMin = Vector2.zero;
        scrollRect.anchorMax = Vector2.one;
        scrollRect.offsetMin = new Vector2(0f, 76f);
        scrollRect.offsetMax = new Vector2(0f, -70f);

        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
        viewport.transform.SetParent(scrollObject.transform, false);
        Stretch((RectTransform)viewport.transform, 0f, 0f);
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
        layout.padding = new RectOffset(20, 20, 16, 16);
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.UpperCenter;
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

    void ShowScreen(GameObject screen)
    {
        homeScreen.SetActive(screen == homeScreen);
        authScreen.SetActive(screen == authScreen);
        chatScreen.SetActive(screen == chatScreen);
    }

    // ---------------------------------------------------------------- Knappar

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
            authStatus.text = type == "register"
                ? "Lösenordet måste vara minst 6 tecken."
                : "Skriv ditt lösenord.";
            return;
        }

        BeginConnect(new Msg { type = type, name = name, password = password }, authScreen);
    }

    void OnNext()
    {
        ClearChat();
        canReport = false;
        net.Send(new Msg { type = "next" });
    }

    void OnReport()
    {
        net.Send(new Msg { type = "report", reason = "Rapporterad med knappen i appen" });
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
        chatInput.text = "";
        chatInput.ActivateInputField();
    }

    // ---------------------------------------------------------------- Nätverk

    void BeginConnect(Msg hello, GameObject entry)
    {
        entryScreen = entry;
        pendingHello = hello;

        string url = serverInput.text.Trim();
        if (url.Length == 0) url = defaultServer;
        if (!url.StartsWith("ws://") && !url.StartsWith("wss://")) url = "ws://" + url;

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
                net.Send(new Msg { type = "find" });
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
                AddSystem("Söker efter någon att prata med...");
                break;

            case "matched":
                partner = m.partner;
                canReport = true;
                ClearChat();
                AddSystem("Du chattar nu med " + partner + ". Säg hej!");
                RefreshChatUi();
                chatInput.ActivateInputField();
                break;

            case "chat":
                AddChat(m.sender, m.text);
                break;

            case "partner_left":
                AddSystem(partner + " lämnade chatten. Tryck på Nästa för att hitta någon ny.");
                partner = null;
                RefreshChatUi();
                break;

            case "info":
                AddSystem(m.text);
                break;

            case "online":
                online = m.online;
                RefreshChatUi();
                break;
        }
    }

    void ResetSession()
    {
        partner = null;
        myName = null;
        pendingHello = null;
        online = 0;
        canReport = false;
        ClearChat();
        RefreshChatUi();
    }

    void RefreshChatUi()
    {
        bool hasPartner = partner != null;
        string who = hasPartner ? "Chattar med " + partner : "Söker...";
        chatStatus.text = who + (online > 0 ? "   •   " + online + " online" : "");
        reportBtn.interactable = canReport;
        chatInput.interactable = hasPartner;
        sendBtn.interactable = hasPartner;
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
        rowCount = 0;
    }

    void AddChat(string sender, string text)
    {
        bool mine = sender == myName;
        string color = mine ? ColorMe : ColorThem;
        string label = mine ? "Du" : sender;
        AddRow("<b><color=" + color + ">" + Esc(label) + "</color></b>  " + Esc(text),
            mine ? TextAnchor.UpperRight : TextAnchor.UpperLeft, 22);
    }

    void AddSystem(string text)
    {
        AddRow("<i><color=" + ColorSystem + ">" + Esc(text) + "</color></i>", TextAnchor.UpperCenter, 19);
    }

    void AddRow(string richText, TextAnchor anchor, int size)
    {
        Text row = MakeText(content, richText, size, anchor, Color.white);
        row.supportRichText = true;
        row.raycastTarget = false;

        rowCount++;
        if (rowCount > 200)
        {
            Destroy(content.GetChild(0).gameObject);
            rowCount--;
        }
        scrollFrames = 2;
    }

    /// <summary>
    /// Texten visas med "rich text". Byt ut '&lt;' så att ingen kan skicka in egna färg-/storlekstaggar.
    /// </summary>
    static string Esc(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Replace('<', '‹');
    }

    // ---------------------------------------------------------------- UI-hjälpare

    static void Stretch(RectTransform rt, float padX, float padY)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(padX, padY);
        rt.offsetMax = new Vector2(-padX, -padY);
    }

    GameObject MakeScreen(string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvas.transform, false);
        Stretch((RectTransform)go.transform, 0f, 0f);
        go.GetComponent<Image>().color = Bg;
        return go;
    }

    /// <summary>En centrerad kolumn där barnen staplas lodrätt.</summary>
    static Transform MakeColumn(Transform parent, float width)
    {
        var go = new GameObject("Kolumn", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        go.transform.SetParent(parent, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, 0f);

        var layout = go.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 14f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go.transform;
    }

    /// <summary>Ett fält högst upp eller längst ner där barnen ligger bredvid varandra.</summary>
    static RectTransform MakeBar(Transform parent, bool top, float height)
    {
        var go = new GameObject(top ? "Övre fält" : "Undre fält",
            typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = PanelColor;

        var rt = (RectTransform)go.transform;
        float y = top ? 1f : 0f;
        rt.anchorMin = new Vector2(0f, y);
        rt.anchorMax = new Vector2(1f, y);
        rt.pivot = new Vector2(0.5f, y);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(0f, height);

        var layout = go.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 8, 8);
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;
        return rt;
    }

    Text MakeText(Transform parent, string value, int size, TextAnchor anchor, Color color)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = color;
        t.text = value;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.supportRichText = false;
        return t;
    }

    static void MakeSpacer(Transform parent, float height)
    {
        var go = new GameObject("Mellanrum", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
    }

    Button MakeButton(Transform parent, string label, Color color, UnityAction onClick, int fontSize, float height)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        var image = go.GetComponent<Image>();
        image.color = color;
        go.GetComponent<LayoutElement>().preferredHeight = height;

        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        Text text = MakeText(go.transform, label, fontSize, TextAnchor.MiddleCenter, Color.white);
        text.raycastTarget = false;
        Stretch(text.rectTransform, 6f, 0f);
        return button;
    }

    static void SetWidth(Component component, float width)
    {
        var layout = component.GetComponent<LayoutElement>();
        if (layout == null) layout = component.gameObject.AddComponent<LayoutElement>();
        layout.preferredWidth = width;
    }

    InputField MakeInput(Transform parent, string placeholder, bool isPassword, float height = 60f)
    {
        var go = new GameObject("Textfält", typeof(RectTransform), typeof(Image), typeof(InputField), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        var background = go.GetComponent<Image>();
        background.color = new Color(1f, 1f, 1f, 0.12f);
        go.GetComponent<LayoutElement>().preferredHeight = height;

        Text text = MakeText(go.transform, "", 22, TextAnchor.MiddleLeft, Color.white);
        text.raycastTarget = false;
        Stretch(text.rectTransform, 14f, 6f);

        Text hint = MakeText(go.transform, placeholder, 22, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.35f));
        hint.fontStyle = FontStyle.Italic;
        hint.raycastTarget = false;
        Stretch(hint.rectTransform, 14f, 6f);

        var input = go.GetComponent<InputField>();
        input.targetGraphic = background;
        input.textComponent = text;
        input.placeholder = hint;
        if (isPassword) input.contentType = InputField.ContentType.Password;
        return input;
    }
}
