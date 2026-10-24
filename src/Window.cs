using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace SharedInventory;

internal sealed class Window : IDisposable
{
    private const string SearchControlName = "SharedInventorySearch";
    private const float Width = 450;
    private const float PreferredHeight = 540;
    private const float PreferredScale = 1.5f;
    private static readonly Color Gold = new Color(0.68f, 0.49f, 0.25f);
    private static readonly Color PaleGold = new Color(0.94f, 0.79f, 0.49f);
    private static readonly Color Muted = new Color(0.63f, 0.61f, 0.54f);

    private sealed class Row
    {
        public string Id = "";
        public string Name = "";
        public string Count = "";
        public int Stack;
        public Sprite? Icon;
    }

    private readonly Client client;
    private readonly List<Row> rows = new List<Row>();
    private Rect rect;
    private float scale;
    private float height;
    private Vector2 scroll;
    private string search = "";
    private bool focusSearch;
    private int version = -1;
    private string cachedSearch = "\0";
    private GameObject? blocker;
    private RectTransform? blockerRect;
    private GUISkin? skin;
    private Font? titleFont;
    private GUIStyle titleStyle = null!;
    private GUIStyle headingStyle = null!;
    private GUIStyle countStyle = null!;
    private GUIStyle hintStyle = null!;
    private GUIStyle statusStyle = null!;
    private GUIStyle emptyStyle = null!;
    private GUIStyle recoveryStyle = null!;
    private Texture2D? crystal;
    private Texture2D? risingLine;
    private Texture2D? fallingLine;
    private readonly List<Texture2D> themeTextures = new List<Texture2D>();
    public bool Visible { get; private set; }
#if SHAREDINVENTORY_TESTING
    public TestPanel? TestingPanel;
    private bool testingTab;
#endif
    public bool IsPointerOver
    {
        get
        {
            if (!Visible) return false;
            CenterOnScreen();
            var pointer = ZInput.pointerPosition;
            // Screen input starts at the bottom left; IMGUI starts at the top left.
            return rect.Contains(new Vector2(pointer.x, Screen.height - pointer.y));
        }
    }

    public Window(Client client) => this.client = client;

    public void Toggle()
    {
        if (Visible) { Close(); return; }
        search = "";
        scroll = Vector2.zero;
        focusSearch = true;
        Visible = true;
        CenterOnScreen();
        CreateBlocker();
        blocker!.SetActive(true);
        PositionBlocker();
        client.List(false);
    }

    public void Close()
    {
        search = "";
        scroll = Vector2.zero;
        focusSearch = false;
        Visible = false;
        blocker?.SetActive(false);
    }

    public void Dispose()
    {
        if (blocker != null) UnityEngine.Object.Destroy(blocker);
        blocker = null;
        blockerRect = null;
        if (skin != null) UnityEngine.Object.Destroy(skin);
        skin = null;
        if (titleFont != null) UnityEngine.Object.Destroy(titleFont);
        titleFont = null;
        foreach (var texture in themeTextures) UnityEngine.Object.Destroy(texture);
        themeTextures.Clear();
        crystal = null;
        risingLine = fallingLine = null;
    }

    private void CenterOnScreen()
    {
        // Shorten the scrolling list on small screens before shrinking type or
        // icons. Both remain at 150% unless even the minimum panel cannot fit.
        var availableWidth = Mathf.Max(1, Screen.width - 32);
        var availableHeight = Mathf.Max(1, Screen.height - 32);
        scale = Mathf.Min(PreferredScale, Mathf.Min(availableWidth / Width, availableHeight / 360f));
        height = Mathf.Min(PreferredHeight, availableHeight / scale);
        var size = new Vector2(Width * scale, height * scale);
        rect = new Rect((Screen.width - size.x) / 2, (Screen.height - size.y) / 2, size.x, size.y);
    }

    private Texture2D Background(Color color, Color edge)
    {
        var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                texture.SetPixel(x, y, x == 0 || y == 0 || x == 7 || y == 7 ? edge : color);
        texture.Apply(false, true);
        themeTextures.Add(texture);
        return texture;
    }

    private Texture2D PanelTexture()
    {
        var texture = new Texture2D(128, 256, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var pixels = new Color[128 * 256];
        for (var y = 0; y < 256; y++)
            for (var x = 0; x < 128; x++)
            {
                // Subtle dark timber grain, generated only once per session.
                var grain = Mathf.Sin(x * 0.7f + Mathf.Sin(y * 0.055f) * 0.9f) * 0.004f;
                var noise = ((x * 73 + y * 37) % 19) * 0.0003f;
                pixels[y * 128 + x] = new Color(0.075f + grain + noise, 0.079f + grain, 0.074f + grain, 0.995f);
            }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        themeTextures.Add(texture);
        return texture;
    }

    private Texture2D CrystalTexture()
    {
        var texture = new Texture2D(48, 64, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var pixels = new Color[48 * 64];
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 48; x++)
            {
                var px = (x - 23.5f) / 19;
                var py = (y - 31.5f) / 28;
                var distance = Mathf.Abs(px) + Mathf.Abs(py);
                if (distance > 1) continue;
                var facet = px < 0 ? (py < 0 ? 0.65f : 1f) : (py < 0 ? 0.42f : 0.77f);
                pixels[y * 48 + x] = distance > 0.92f ? PaleGold :
                    new Color(0.94f * facet, 0.59f * facet, 0.19f * facet, 1);
            }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        themeTextures.Add(texture);
        return texture;
    }

    private Texture2D LineTexture(bool rising)
    {
        var texture = new Texture2D(32, 32, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var pixels = new Color[32 * 32];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var distance = Mathf.Abs(x - (rising ? y : 31 - y));
                pixels[y * 32 + x] = new Color(1, 1, 1, Mathf.Clamp01(2.5f - distance));
            }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        themeTextures.Add(texture);
        return texture;
    }

    private void CreateSkin()
    {
        if (skin != null) return;
        skin = UnityEngine.Object.Instantiate(GUI.skin);
        skin.hideFlags = HideFlags.HideAndDontSave;
        var panel = PanelTexture();
        var control = Background(new Color(0.15f, 0.145f, 0.12f), new Color(0.38f, 0.31f, 0.20f));
        var hover = Background(new Color(0.24f, 0.20f, 0.13f), Gold);
        var pressed = Background(new Color(0.045f, 0.052f, 0.049f), new Color(0.30f, 0.26f, 0.18f));
        var ink = new Color(0.91f, 0.88f, 0.78f);
        foreach (var style in new[] { skin.window, skin.button, skin.textField,
                     skin.verticalScrollbar, skin.verticalScrollbarThumb })
        {
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                         style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                state.background = style == skin.window ? panel : control;
                state.textColor = ink;
            }
            style.border = style == skin.window ? new RectOffset() : new RectOffset(1, 1, 1, 1);
            style.fontSize = 14;
        }
        skin.button.alignment = TextAnchor.MiddleCenter;
        skin.button.padding = new RectOffset(3, 3, 2, 2);
        skin.textField.padding = new RectOffset(9, 6, 5, 3);
        skin.window.padding = new RectOffset();
        skin.button.hover.background = hover;
        skin.button.hover.textColor = PaleGold;
        skin.button.active.background = pressed;
        foreach (var state in new[] { skin.textField.normal, skin.textField.hover, skin.textField.active, skin.textField.focused })
            state.background = pressed;
        skin.textField.focused.background = Background(new Color(0.065f, 0.070f, 0.061f), Gold);
        // The track and thumb need distinct fills in every interaction state.
        // Keep Unity's proportional thumb sizing and native drag/page behavior.
        var thumb = Background(new Color(0.49f, 0.37f, 0.21f), Gold);
        var thumbHighlight = Background(Gold, PaleGold);
        foreach (var state in new[] { skin.verticalScrollbar.normal, skin.verticalScrollbar.hover,
                     skin.verticalScrollbar.active, skin.verticalScrollbar.focused,
                     skin.verticalScrollbar.onNormal, skin.verticalScrollbar.onHover,
                     skin.verticalScrollbar.onActive, skin.verticalScrollbar.onFocused })
            state.background = pressed;
        foreach (var state in new[] { skin.verticalScrollbarThumb.normal, skin.verticalScrollbarThumb.hover,
                     skin.verticalScrollbarThumb.active, skin.verticalScrollbarThumb.focused,
                     skin.verticalScrollbarThumb.onNormal, skin.verticalScrollbarThumb.onHover,
                     skin.verticalScrollbarThumb.onActive, skin.verticalScrollbarThumb.onFocused })
            state.background = thumb;
        skin.verticalScrollbarThumb.hover.background = thumbHighlight;
        skin.verticalScrollbarThumb.active.background = thumbHighlight;
        skin.verticalScrollbar.fixedWidth = 12;
        skin.verticalScrollbarThumb.fixedWidth = 12;
        skin.label.normal.textColor = ink;
        skin.label.fontSize = 14;
        skin.label.alignment = TextAnchor.MiddleLeft;
        skin.label.padding = new RectOffset();
        skin.label.clipping = TextClipping.Clip;
        skin.label.wordWrap = false;
        titleFont = Font.CreateDynamicFontFromOSFont(new[] { "Georgia", "Liberation Serif", "DejaVu Serif" }, 24);
        titleStyle = new GUIStyle(skin.label) { fontSize = 23, fontStyle = FontStyle.Bold, font = titleFont != null ? titleFont : skin.font };
        titleStyle.normal.textColor = PaleGold;
        headingStyle = new GUIStyle(skin.label) { fontSize = 10, fontStyle = FontStyle.Bold };
        headingStyle.normal.textColor = Gold;
        countStyle = new GUIStyle(skin.label) { fontSize = 13, alignment = TextAnchor.MiddleRight };
        countStyle.normal.textColor = PaleGold;
        hintStyle = new GUIStyle(skin.label) { fontSize = 10 };
        hintStyle.normal.textColor = Muted;
        statusStyle = new GUIStyle(hintStyle) { fontSize = 11 };
        emptyStyle = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
        emptyStyle.normal.textColor = Muted;
        recoveryStyle = new GUIStyle(statusStyle) { wordWrap = true };
        crystal = CrystalTexture();
        risingLine = LineTexture(true);
        fallingLine = LineTexture(false);
    }

    // IMGUI does not participate in Unity UI raycasts. A transparent image over
    // just this window prevents clicks reaching the crafting/inventory UI below.
    private void CreateBlocker()
    {
        if (blocker != null) return;
        blocker = new GameObject("SharedInventoryInput", typeof(Canvas), typeof(GraphicRaycaster));
        blocker.hideFlags = HideFlags.HideAndDontSave;
        var canvas = blocker.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32767;
        var surface = new GameObject("Window", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        surface.transform.SetParent(blocker.transform, false);
        var image = surface.GetComponent<Image>();
        image.color = new Color(0, 0, 0, 0);
        image.raycastTarget = true;
        blockerRect = surface.GetComponent<RectTransform>();
        blockerRect.anchorMin = blockerRect.anchorMax = blockerRect.pivot = Vector2.zero;
    }

    private void PositionBlocker()
    {
        if (blockerRect == null) return;
        blockerRect.anchoredPosition = new Vector2(rect.x, Screen.height - rect.y - rect.height);
        blockerRect.sizeDelta = new Vector2(rect.width, rect.height);
    }

    public void Draw()
    {
        if (!Visible || Player.m_localPlayer == null) return;
        CenterOnScreen();
        CreateSkin();
        var previousSkin = GUI.skin;
        var previousMatrix = GUI.matrix;
        var previousColor = GUI.color;
        var previousContentColor = GUI.contentColor;
        var previousBackgroundColor = GUI.backgroundColor;
        var previousEnabled = GUI.enabled;
        try
        {
            GUI.skin = skin;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            GUI.color = Color.white;
            GUI.contentColor = GUI.backgroundColor = Color.white;
            GUI.enabled = true;
            // Keep all controls in logical coordinates. GUI.matrix scales text,
            // sprites, scrolling and hit testing together; the UI blocker below
            // uses the actual screen-space rectangle instead.
            GUI.Window(0x53494E56, new Rect(rect.x / scale, rect.y / scale, Width, height), DrawContents, "");
        }
        finally
        {
            GUI.skin = previousSkin;
            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
            GUI.contentColor = previousContentColor;
            GUI.backgroundColor = previousBackgroundColor;
            GUI.enabled = previousEnabled;
        }
        PositionBlocker();
    }

    private static void Fill(Rect area, Color color)
    {
        if (Event.current.type != EventType.Repaint) return;
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(area, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private void Line(Vector2 from, Vector2 to, Color color)
    {
        if (Event.current.type != EventType.Repaint) return;
        var direction = to - from;
        var x = Mathf.Min(from.x, to.x);
        var y = Mathf.Min(from.y, to.y);
        if (direction.x == 0 || direction.y == 0)
        {
            Fill(new Rect(x, y, Mathf.Max(1, Mathf.Abs(direction.x)), Mathf.Max(1, Mathf.Abs(direction.y))), color);
            return;
        }
        // Rasterized diagonals keep ornament coordinates local to GUI.Window;
        // rotating GUI.matrix inside a scaled window also rotates its origin.
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(x, y, Mathf.Abs(direction.x), Mathf.Abs(direction.y)),
            direction.x * direction.y > 0 ? fallingLine : risingLine);
        GUI.color = previous;
    }

    private void DrawFrame()
    {
        Fill(new Rect(0, 0, Width, 3), Gold);
        Fill(new Rect(0, height - 3, Width, 3), Gold);
        Fill(new Rect(0, 0, 3, height), Gold);
        Fill(new Rect(Width - 3, 0, 3, height), Gold);
        var innerEdge = new Color(0.27f, 0.24f, 0.17f);
        Fill(new Rect(7, 7, Width - 14, 1), innerEdge);
        Fill(new Rect(7, height - 8, Width - 14, 1), innerEdge);
        Fill(new Rect(7, 7, 1, height - 14), innerEdge);
        Fill(new Rect(Width - 8, 7, 1, height - 14), innerEdge);
        Fill(new Rect(11, 11, Width - 22, 75), new Color(0.045f, 0.048f, 0.044f, 0.75f));
        // Angular gold corner bindings echo the planned rune-bound reliquary.
        for (var column = 0; column < 2; column++)
            for (var row = 0; row < 2; row++)
            {
                var x = column == 0 ? 10 : Width - 27;
                var y = row == 0 ? 10 : height - 27;
                Line(new Vector2(x, y + 16), new Vector2(x + 16, y), Gold);
                Line(new Vector2(x, y + 10), new Vector2(x + 10, y), Gold);
            }
        Line(new Vector2(20, 88), new Vector2(Width / 2 - 9, 88), innerEdge);
        Line(new Vector2(Width / 2 + 9, 88), new Vector2(Width - 20, 88), innerEdge);
        Line(new Vector2(Width / 2 - 5, 88), new Vector2(Width / 2, 83), Gold);
        Line(new Vector2(Width / 2, 83), new Vector2(Width / 2 + 5, 88), Gold);
        Line(new Vector2(Width / 2 + 5, 88), new Vector2(Width / 2, 93), Gold);
        Line(new Vector2(Width / 2, 93), new Vector2(Width / 2 - 5, 88), Gold);
        if (crystal != null) GUI.DrawTexture(new Rect(19, 15, 44, 60), crystal);
        Line(new Vector2(41, 28), new Vector2(41, 62), PaleGold);
        Line(new Vector2(34, 37), new Vector2(41, 44), PaleGold);
        Line(new Vector2(41, 44), new Vector2(48, 37), PaleGold);
        GUI.Label(new Rect(74, 29, Width - 115, 32), "Einherjar Chest", titleStyle);
    }

    private void DrawFooter()
    {
        Fill(new Rect(18, height - 51, Width - 36, 1), new Color(0.27f, 0.24f, 0.17f));
        GUI.Label(new Rect(32, height - 26, Width - 140, 16), "ALT-CLICK TO DEPOSIT    /    F3 OR ESC TO CLOSE", hintStyle);
#if SHAREDINVENTORY_TESTING
        if (TestingPanel != null && GUI.Button(new Rect(Width - 98, height - 39, 66, 23), testingTab ? "Inventory" : "Testing"))
            testingTab = !testingTab;
#endif
    }

    private void DrawContents(int id)
    {
        DrawFrame();
        if (GUI.Button(new Rect(Width - 52, 21, 25, 25), "x")) Close();
#if SHAREDINVENTORY_TESTING
        if (TestingPanel != null && testingTab)
        {
            GUI.BeginGroup(new Rect(18, 102, Width - 36, height - 162));
            TestingPanel.DrawContents(Width - 36, height - 162);
            GUI.EndGroup();
            DrawFooter();
            return;
        }
#endif
        GUI.SetNextControlName(SearchControlName);
        search = GUI.TextField(new Rect(18, 104, Width - 36, 30), search);
        if (search.Length == 0)
            GUI.Label(new Rect(29, 105, Width - 58, 28), "Search stored items...", statusStyle);
        // Wait until the control exists, and focus only once per opening.
        if (focusSearch && Event.current.type == EventType.Repaint)
        {
            GUI.FocusWindow(id);
            GUI.FocusControl(SearchControlName);
            focusSearch = false;
        }
        RebuildRows();
        const float contentWidth = Width - 54;
        const float actionsX = contentWidth - 88;
        const float countX = actionsX - 79;
        GUI.Label(new Rect(66, 143, countX - 40, 19), "ITEM", headingStyle);
        GUI.Label(new Rect(18 + countX, 143, 74, 19), "STORED", headingStyle);
        GUI.Label(new Rect(18 + actionsX, 143, 90, 19), "WITHDRAW", headingStyle);
        var recovery = client.RefreshStatus.Length != 0;
        if (recovery)
            GUI.Label(new Rect(18, 164, Width - 36, 32), client.RefreshStatus, recoveryStyle);
        var top = recovery ? 200 : 166;
        var viewport = new Rect(18, top, Width - 36, height - top - 61);
        const int rowHeight = 40;
        Fill(viewport, new Color(0.035f, 0.041f, 0.037f, 0.6f));
        scroll = GUI.BeginScrollView(viewport, scroll, new Rect(0, 0, contentWidth, rows.Count * rowHeight));
        var first = Math.Max(0, (int)(scroll.y / rowHeight));
        var last = Math.Min(rows.Count, first + (int)(viewport.height / rowHeight) + 2);
        for (var i = first; i < last; ++i)
        {
            var row = rows[i];
            var y = i * rowHeight;
            var rowRect = new Rect(0, y, contentWidth, rowHeight - 1);
            Fill(rowRect, i % 2 == 0 ? new Color(0.12f, 0.13f, 0.115f, 0.65f) : new Color(0.08f, 0.09f, 0.08f, 0.6f));
            Fill(new Rect(5, y + 4, 33, 32), new Color(0.04f, 0.045f, 0.039f, 0.8f));
            if (row.Icon != null) DrawIcon(new Rect(6, y + 4, 32, 32), row.Icon);
            GUI.Label(new Rect(46, y + 4, countX - 51, 32), row.Name);
            GUI.Label(new Rect(countX, y + 4, 72, 32), row.Count, countStyle);
            GUI.enabled = client.CanAccessSharedInventory && !client.Busy && row.Stack > 0;
            if (GUI.Button(new Rect(actionsX, y + 6, 30, 28), "1")) client.Withdrawal(row.Id, false);
            if (GUI.Button(new Rect(actionsX + 35, y + 6, 53, 28), "Stack")) client.Withdrawal(row.Id, true);
            GUI.enabled = true;
        }
        GUI.EndScrollView();
        if (rows.Count == 0 && !recovery)
            GUI.Label(new Rect(28, top + 12, Width - 56, 60), client.Listing ? "Opening the chest..." : "No matching items.", emptyStyle);
        DrawFooter();
    }

    private static void DrawIcon(Rect target, Sprite icon)
    {
        // Item sprites can share an atlas; draw only this sprite's UV region.
        var source = icon.textureRect;
        var texture = icon.texture;
        var scale = Mathf.Min(target.width / source.width, target.height / source.height);
        var width = source.width * scale;
        var height = source.height * scale;
        target = new Rect(target.x + (target.width - width) / 2,
            target.y + (target.height - height) / 2, width, height);
        GUI.DrawTextureWithTexCoords(target, texture, new Rect(source.x / texture.width,
            source.y / texture.height, source.width / texture.width, source.height / texture.height));
    }

    private void RebuildRows()
    {
        if (version == client.Version && cachedSearch == search) return;
        var searchChanged = cachedSearch != search;
        version = client.Version;
        cachedSearch = search;
        rows.Clear();
        foreach (var pair in client.Counts)
        {
            if (!ContentIds.CanShare(pair.Key)) continue;
            var item = Items.Template(pair.Key);
            var name = item == null ? pair.Key : Localization.instance.Localize(item.m_shared.m_name);
            if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                pair.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(new Row
            {
                Id = pair.Key, Name = name + (client.Quality(pair.Key) > 1 ? " (Quality " + client.Quality(pair.Key) + ")" : ""), Count = pair.Value.ToString("N0", CultureInfo.CurrentCulture),
                Stack = item == null ? 0 : Math.Max(1, item.m_shared.m_maxStackSize),
                Icon = item?.m_shared.m_icons != null && item.m_shared.m_icons.Length > 0
                    ? item.m_shared.m_icons[0] : null
            });
        }
        rows.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        if (searchChanged) scroll = Vector2.zero;
    }
}
