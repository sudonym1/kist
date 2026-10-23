using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace SharedInventory;

internal sealed class Window : IDisposable
{
    private const string SearchControlName = "SharedInventorySearch";

    private sealed class Row
    {
        public string Id = "";
        public string Name = "";
        public string Count = "";
        public int Stack;
        public Sprite? Icon;
    }

    private readonly Client client;
    private readonly ConfigEntry<Vector2> savedPosition;
    private bool positioned;
    private readonly List<Row> rows = new List<Row>();
    private Rect rect;
    private Vector2 scroll;
    private string search = "";
    private bool focusSearch;
    private int version = -1;
    private string cachedSearch = "\0";
    private GameObject? blocker;
    private RectTransform? blockerRect;
    private GUISkin? skin;
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
            ClampToScreen();
            var pointer = ZInput.pointerPosition;
            // Screen input starts at the bottom left; IMGUI starts at the top left.
            return rect.Contains(new Vector2(pointer.x, Screen.height - pointer.y));
        }
    }

    public Window(Client client, ConfigEntry<Vector2> savedPosition)
    {
        this.client = client;
        this.savedPosition = savedPosition;
    }

    public void Toggle()
    {
        if (Visible) { Close(); return; }
        search = "";
        scroll = Vector2.zero;
        focusSearch = true;
        Visible = true;
        if (!positioned)
        {
            var width = Mathf.Min(450, Screen.width);
            var height = Mathf.Min(540, Screen.height);
            var position = savedPosition.Value;
            if (position.x < 0 || position.y < 0 || float.IsNaN(position.x) || float.IsNaN(position.y) ||
                float.IsInfinity(position.x) || float.IsInfinity(position.y))
                position = new Vector2((Screen.width - width) / 2, (Screen.height - height) / 2);
            rect = new Rect(position.x, position.y, width, height);
            positioned = true;
        }
        ClampToScreen();
        CreateBlocker();
        blocker!.SetActive(true);
        PositionBlocker();
        client.List();
    }

    public void Close()
    {
        SavePosition();
        search = "";
        scroll = Vector2.zero;
        focusSearch = false;
        Visible = false;
        blocker?.SetActive(false);
    }

    public void Dispose()
    {
        SavePosition();
        if (blocker != null) UnityEngine.Object.Destroy(blocker);
        blocker = null;
        blockerRect = null;
        if (skin != null) UnityEngine.Object.Destroy(skin);
        skin = null;
        foreach (var texture in themeTextures) UnityEngine.Object.Destroy(texture);
        themeTextures.Clear();
    }

    private void ClampToScreen()
    {
        rect.width = Mathf.Min(450, Screen.width);
        rect.height = Mathf.Min(540, Screen.height);
        rect.x = Mathf.Clamp(rect.x, 0, Screen.width - rect.width);
        rect.y = Mathf.Clamp(rect.y, 0, Screen.height - rect.height);
    }

    private void SavePosition()
    {
        if (!positioned) return;
        ClampToScreen();
        if (savedPosition.Value != rect.position) savedPosition.Value = rect.position;
    }

    private Texture2D Background(Color color)
    {
        var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        themeTextures.Add(texture);
        return texture;
    }

    private void CreateSkin()
    {
        if (skin != null) return;
        skin = UnityEngine.Object.Instantiate(GUI.skin);
        skin.hideFlags = HideFlags.HideAndDontSave;
        var panel = Background(new Color(0.24f, 0.18f, 0.12f, 0.98f));
        var control = Background(new Color(0.34f, 0.25f, 0.15f));
        var hover = Background(new Color(0.46f, 0.34f, 0.20f));
        var pressed = Background(new Color(0.18f, 0.13f, 0.08f));
        var ink = new Color(0.95f, 0.86f, 0.68f);
        foreach (var style in new[] { skin.window, skin.button, skin.textField,
                     skin.verticalScrollbar, skin.verticalScrollbarThumb })
        {
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                         style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                state.background = style == skin.window ? panel : control;
                state.textColor = ink;
            }
            style.border = new RectOffset();
        }
        skin.button.hover.background = hover;
        skin.button.active.background = pressed;
        skin.textField.normal.background = pressed;
        // The track and thumb need distinct fills in every interaction state.
        // Keep Unity's proportional thumb sizing and native drag/page behavior.
        var thumb = Background(new Color(0.68f, 0.51f, 0.29f));
        var thumbHighlight = Background(new Color(0.86f, 0.67f, 0.39f));
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
        skin.label.normal.textColor = ink;
        skin.window.fontStyle = FontStyle.Bold;
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
        ClampToScreen();
        CreateSkin();
        var previousSkin = GUI.skin;
        try
        {
            GUI.skin = skin;
            rect = GUI.Window(0x53494E56, rect, DrawContents, "Shared inventory");
        }
        finally { GUI.skin = previousSkin; }
        ClampToScreen();
        // Persist once dragging ends, rather than writing the config every frame.
        if (!Input.GetMouseButton(0)) SavePosition();
        PositionBlocker();
    }

    private void DrawContents(int id)
    {
        if (GUI.Button(new Rect(rect.width - 32, 3, 26, 23), "×")) Close();
#if SHAREDINVENTORY_TESTING
        if (TestingPanel != null)
        {
            if (GUI.Button(new Rect(rect.width - 135, 3, 98, 23), testingTab ? "Inventory" : "Testing")) testingTab = !testingTab;
            if (testingTab)
            {
                TestingPanel.DrawContents(rect.width, rect.height);
                GUI.DragWindow(new Rect(0, 0, rect.width - 140, 25));
                return;
            }
        }
#endif
        GUI.Label(new Rect(12, 28, 55, 24), "Search");
        GUI.SetNextControlName(SearchControlName);
        search = GUI.TextField(new Rect(68, 28, rect.width - 154, 25), search);
        // Wait until the control exists, and focus only once per opening.
        if (focusSearch && Event.current.type == EventType.Repaint)
        {
            GUI.FocusWindow(id);
            GUI.FocusControl(SearchControlName);
            focusSearch = false;
        }
        GUI.enabled = client.Ready && !client.Listing;
        if (GUI.Button(new Rect(rect.width - 78, 28, 66, 25), "Refresh")) client.List();
        GUI.enabled = true;
        GUI.Label(new Rect(46, 57, rect.width - 24, 22), "Item");
        GUI.Label(new Rect(rect.width - 223, 57, 95, 22), "Total");
        GUI.Label(new Rect(rect.width - 128, 57, 100, 22), "Withdraw");
        RebuildRows();
        var viewport = new Rect(10, 81, rect.width - 20, rect.height - 91);
        const int rowHeight = 32;
        var width = viewport.width - 18;
        scroll = GUI.BeginScrollView(viewport, scroll, new Rect(0, 0, width, rows.Count * rowHeight));
        var first = Math.Max(0, (int)(scroll.y / rowHeight));
        var last = Math.Min(rows.Count, first + (int)(viewport.height / rowHeight) + 2);
        for (var i = first; i < last; ++i)
        {
            var row = rows[i];
            var y = i * rowHeight;
            if (row.Icon != null) DrawIcon(new Rect(2, y + 1, 28, 28), row.Icon);
            GUI.Label(new Rect(36, y, width - 234, 28), new GUIContent(row.Name, row.Id));
            GUI.Label(new Rect(width - 195, y, 95, 28), row.Count);
            GUI.enabled = client.Ready && !client.Busy && row.Stack > 0;
            if (GUI.Button(new Rect(width - 100, y, 35, 27), "1")) client.Withdrawal(row.Id, false);
            if (GUI.Button(new Rect(width - 60, y, 60, 27), "Stack")) client.Withdrawal(row.Id, true);
            GUI.enabled = true;
        }
        GUI.EndScrollView();
        if (rows.Count == 0)
            GUI.Label(new Rect(20, 93, width, 26), client.Listing ? "Loading…" : "No matching items.");
        GUI.DragWindow(new Rect(0, 0, rect.width - 35, 25));
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
