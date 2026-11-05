using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Färger och små hjälpfunktioner som bygger gränssnittet i kod.
/// Allt ritas med Unitys inbyggda UI (uGUI), så inga prefabs eller bildfiler behövs.
/// </summary>
public static class Ui
{
    // ---------------------------------------------------------------- Färger (samma som webbversionen)

    public static readonly Color Bg = Hex(0x18191C);
    public static readonly Color Panel = Hex(0x222327);
    public static readonly Color Bubble = Hex(0x303136);
    public static readonly Color Neutral = Hex(0x3A3B40);
    public static readonly Color Accent = Hex(0x3D78D9);
    public static readonly Color Field = new Color(1f, 1f, 1f, 0.07f);
    public static readonly Color TextColor = Hex(0xF2F2F3);
    public static readonly Color Muted = Hex(0x8E8F94);
    public static readonly Color Warning = Hex(0xFFB38A);

    // Knappfärger med betydelse: rött = rapportera, grönt = skapa, osv. Dämpade så att de inte skriker.
    public static readonly Color Red = Hex(0xC9483E);
    public static readonly Color Green = Hex(0x3F9E6A);
    public static readonly Color Amber = Hex(0xC98A2B);
    public static readonly Color Purple = Hex(0x7A5BC9);

    // Den varma startskärmen
    public static readonly Color Coral = Hex(0xE0704A);
    public static readonly Color WarmTop = Hex(0x2D1B2E);
    public static readonly Color WarmBottom = Hex(0x7A3F26);
    public static readonly Color WarmCard = new Color(0.10f, 0.06f, 0.06f, 0.55f);
    public static readonly Color Glass = new Color(1f, 1f, 1f, 0.12f);

    // Dämpade färger för avatarerna, samma namn får alltid samma färg (samma lista som webbversionen)
    static readonly Color[] AvatarColors =
    {
        Hex(0x5B6EE1), Hex(0x3F9E8F), Hex(0xC0703F), Hex(0x9A5BC4), Hex(0xC2577A), Hex(0x5F8F3E),
    };

    public const float Radius = 10f;   // standardrundning för knappar och fält

    static Color Hex(int rgb) =>
        new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    // ---------------------------------------------------------------- Typsnitt och rundade hörn

    static Font font;
    static Sprite roundSprite;

    static Font DefaultFont
    {
        get
        {
            if (font == null)
                // Första typsnittet som finns används, de andra fyller i tecken som saknas (t.ex. ★ i Segoe UI Symbol)
                font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Segoe UI", "Segoe UI Symbol", "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" }, 16);
            return font;
        }
    }

    const int RoundSize = 64;          // bildens storlek i pixlar, hörnen har radien RoundSize / 2

    /// <summary>
    /// En vit cirkel som "9-slice"-sprite: mitten sträcks ut och hörnen behåller sin form,
    /// så samma bild ger rundade hörn på knappar, bubblor och rutor i alla storlekar.
    /// </summary>
    static Sprite RoundSprite
    {
        get
        {
            if (roundSprite != null) return roundSprite;
            var tex = new Texture2D(RoundSize, RoundSize, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float r = RoundSize / 2f;
            var pixels = new Color32[RoundSize * RoundSize];
            for (int y = 0; y < RoundSize; y++)
            {
                for (int x = 0; x < RoundSize; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    byte alpha = (byte)(Mathf.Clamp01(r - dist) * 255f); // mjuk kant, ingen trappstegseffekt
                    pixels[y * RoundSize + x] = new Color32(255, 255, 255, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            float b = r - 1f;
            roundSprite = Sprite.Create(tex, new Rect(0, 0, RoundSize, RoundSize), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            return roundSprite;
        }
    }

    /// <summary>Ger en bild rundade hörn med den radie som anges (i UI-enheter).</summary>
    public static void Round(Image image, float radius)
    {
        image.sprite = RoundSprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = (RoundSize / 2f) / radius;
    }

    // ---------------------------------------------------------------- Layout

    public static void Stretch(RectTransform rt, float padX = 0f, float padY = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(padX, padY);
        rt.offsetMax = new Vector2(-padX, -padY);
    }

    /// <summary>En helskärmsyta med bakgrundsfärg.</summary>
    public static GameObject Screen(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        Stretch((RectTransform)go.transform);
        go.GetComponent<Image>().color = color;
        return go;
    }

    /// <summary>En helskärmsyta med en lodrät färgövergång från top (överst) till bottom (nederst).</summary>
    public static GameObject GradientScreen(Transform parent, string name, Color top, Color bottom)
    {
        const int steps = 128;
        var tex = new Texture2D(1, steps, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < steps; y++) tex.SetPixel(0, y, Color.Lerp(bottom, top, y / (steps - 1f)));
        tex.Apply();

        GameObject go = Screen(parent, name, Color.white);
        go.GetComponent<Image>().sprite = Sprite.Create(tex, new Rect(0, 0, 1, steps), new Vector2(0.5f, 0.5f));
        return go;
    }

    /// <summary>Ett centrerat kort med rundade hörn, där barnen staplas lodrätt.</summary>
    public static Transform Card(Transform parent, float width, int padding = 30, float spacing = 10f, Color? color = null)
    {
        var go = new GameObject("Kort", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        go.transform.SetParent(parent, false);

        var image = go.GetComponent<Image>();
        image.color = color ?? Panel;
        Round(image, 18f);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, 0f);

        var layout = go.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(padding, padding, padding, padding - 6);
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go.transform;
    }

    /// <summary>Ett fält högst upp eller längst ner där barnen ligger bredvid varandra.</summary>
    public static RectTransform Bar(Transform parent, bool top, float height)
    {
        var go = new GameObject(top ? "Övre fält" : "Undre fält", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = Panel;

        var rt = (RectTransform)go.transform;
        float y = top ? 1f : 0f;
        rt.anchorMin = new Vector2(0f, y);
        rt.anchorMax = new Vector2(1f, y);
        rt.pivot = new Vector2(0.5f, y);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(0f, height);

        var layout = go.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 10, 10);
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        return rt;
    }

    /// <summary>En lodrät grupp utan bakgrund, t.ex. namn + "12 online" i övre fältet.</summary>
    public static Transform Stack(Transform parent, float spacing = 0f)
    {
        var go = new GameObject("Grupp", typeof(RectTransform), typeof(VerticalLayoutGroup));
        go.transform.SetParent(parent, false);
        var layout = go.GetComponent<VerticalLayoutGroup>();
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return go.transform;
    }

    /// <summary>En vågrät rad där barnen delar på bredden, t.ex. två knappar bredvid varandra.</summary>
    public static Transform Row(Transform parent, float spacing = 8f)
    {
        var go = new GameObject("Rad", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        var layout = go.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return go.transform;
    }

    /// <summary>Ett rutnät med lika stora celler, t.ex. för intresseknapparna.</summary>
    public static Transform Grid(Transform parent, int columns, Vector2 cellSize, float spacing)
    {
        var go = new GameObject("Rutnät", typeof(RectTransform), typeof(GridLayoutGroup));
        go.transform.SetParent(parent, false);
        var grid = go.GetComponent<GridLayoutGroup>();
        grid.cellSize = cellSize;
        grid.spacing = new Vector2(spacing, spacing);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;
        grid.childAlignment = TextAnchor.UpperCenter;
        return go.transform;
    }

    public static void Spacer(Transform parent, float height)
    {
        var go = new GameObject("Mellanrum", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
    }

    /// <summary>Sätter önskad bredd och/eller höjd (negativt värde = ändra inte).</summary>
    public static LayoutElement Size(Component component, float width = -1f, float height = -1f)
    {
        var layout = component.GetComponent<LayoutElement>();
        if (layout == null) layout = component.gameObject.AddComponent<LayoutElement>();
        if (width >= 0f) layout.preferredWidth = width;
        if (height >= 0f) layout.preferredHeight = height;
        return layout;
    }

    // ---------------------------------------------------------------- Komponenter

    public static Text Label(Transform parent, string value, int size, TextAnchor anchor, Color color, bool bold = false)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.font = DefaultFont;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = color;
        t.text = value;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.supportRichText = false;
        t.raycastTarget = false;
        return t;
    }

    public static Button Button(Transform parent, string label, Color color, UnityAction onClick, int fontSize, float height,
        float radius = Radius)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        var image = go.GetComponent<Image>();
        image.color = color;
        Round(image, radius);
        go.GetComponent<LayoutElement>().preferredHeight = height;

        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        Text text = Label(go.transform, label, fontSize, TextAnchor.MiddleCenter, TextColor);
        Stretch(text.rectTransform, 8f, 0f);
        return button;
    }

    public static InputField Input(Transform parent, string placeholder, bool isPassword, int characterLimit,
        float height = 52f, float radius = Radius)
    {
        var go = new GameObject("Textfält", typeof(RectTransform), typeof(Image), typeof(InputField), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        var background = go.GetComponent<Image>();
        background.color = Field;
        Round(background, radius);
        go.GetComponent<LayoutElement>().preferredHeight = height;

        float padX = Mathf.Max(14f, radius * 0.7f);
        Text text = Label(go.transform, "", 20, TextAnchor.MiddleLeft, TextColor);
        Stretch(text.rectTransform, padX, 6f);

        Text hint = Label(go.transform, placeholder, 20, TextAnchor.MiddleLeft, Muted);
        Stretch(hint.rectTransform, padX, 6f);

        var input = go.GetComponent<InputField>();
        input.targetGraphic = background;
        input.textComponent = text;
        input.placeholder = hint;
        input.characterLimit = characterLimit;
        input.selectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.5f);
        if (isPassword) input.contentType = InputField.ContentType.Password;
        return input;
    }

    /// <summary>Ett runt "chip" som kan slås av och på, t.ex. för intressen.</summary>
    public static Button Chip(Transform parent, string label, UnityAction onClick)
    {
        Button chip = Button(parent, label, Glass, onClick, 16, 34f, 17f);
        SetChip(chip, false);
        return chip;
    }

    public static void SetChip(Button chip, bool on)
    {
        ((Image)chip.targetGraphic).color = on ? Coral : Glass;
    }

    /// <summary>En rund avatar med en bokstav. Namn och färg sätts med SetAvatar.</summary>
    public static Text Avatar(Transform parent, float size)
    {
        var go = new GameObject("Avatar", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        Round(go.GetComponent<Image>(), size / 2f);
        var layout = go.GetComponent<LayoutElement>();
        layout.preferredWidth = layout.minWidth = size;
        layout.preferredHeight = layout.minHeight = size;

        Text letter = Label(go.transform, "?", Mathf.RoundToInt(size * 0.42f), TextAnchor.MiddleCenter, TextColor, bold: true);
        Stretch(letter.rectTransform);
        SetAvatar(letter, null);
        return letter;
    }

    public static void SetAvatar(Text letter, string name)
    {
        var image = letter.transform.parent.GetComponent<Image>();
        if (string.IsNullOrEmpty(name))
        {
            letter.text = "?";
            image.color = Neutral;
            return;
        }
        uint hash = 0;
        foreach (char c in name) hash = hash * 31 + c; // samma uträkning som webbversionen
        letter.text = char.ToUpper(name[0]).ToString();
        image.color = AvatarColors[hash % AvatarColors.Length];
    }
}
