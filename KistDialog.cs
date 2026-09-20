using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ThorsKist
{
    /// <summary>
    /// A one-button modal for things the player has to be told rather than
    /// discover. Kept self-contained -- its own panel, its own palette -- so it
    /// can appear before the Kist window has ever been built, and so nothing
    /// here is coupled to that window's layout.
    /// </summary>
    public static class KistDialog
    {
        public static bool IsOpen { get { return _root != null && _root.activeInHierarchy; } }

        private static GameObject _root;
        private static TMP_FontAsset _font;
        private static string _pendingTitle;
        private static string _pendingBody;

        private static readonly Color PanelBg = new Color(0.07f, 0.065f, 0.06f, 0.98f);
        private static readonly Color Parchment = new Color(0.87f, 0.82f, 0.70f, 1f);
        private static readonly Color Warn = new Color(0.86f, 0.62f, 0.26f, 1f);
        private static readonly Color BtnBg = new Color(0.24f, 0.21f, 0.16f, 1f);

        private static float S { get { return Mathf.Clamp(Main.UiScale.Value, 0.6f, 1.6f); } }

        /// <summary>
        /// Queues a message. Showing it waits for a canvas to exist, so this is
        /// safe to call from a load path or before the player has spawned.
        /// </summary>
        public static void Show(string title, string body)
        {
            _pendingTitle = title;
            _pendingBody = body;
        }

        public static void Tick()
        {
            if (Player.m_localPlayer == null)
            {
                if (_root != null) Close();
                return;
            }

            if (_root == null && _pendingBody != null) Build();
            if (!IsOpen) return;

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter))
                Close();
        }

        public static void Close()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _pendingTitle = null;
            _pendingBody = null;
        }

        private static void Build()
        {
            if (InventoryGui.instance == null) return;
            Canvas canvas = InventoryGui.instance.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            Transform parent = (canvas.rootCanvas != null ? canvas.rootCanvas : canvas).transform;

            _font = FindFont();

            RectTransform canvasRect = parent as RectTransform;
            float availW = canvasRect != null ? canvasRect.rect.width : Screen.width;

            float pad = Mathf.Round(16f * S);
            float w = Mathf.Min(700f * S, availW * 0.7f);
            float titleSize = 18f * S;
            float bodySize = 13f * S;
            float btnH = Mathf.Round(30f * S);
            float titleH = Mathf.Round(26f * S);

            _root = NewPanel("ThorsKist_Dialog", parent, PanelBg);
            // Above whatever else the game has on screen at the time.
            _root.transform.SetAsLastSibling();

            TMP_Text title = NewText(_root.transform, _pendingTitle ?? "THOR'S KIST", titleSize, Warn,
                                     TextAlignmentOptions.Left);
            TMP_Text body = NewText(_root.transform, _pendingBody ?? "", bodySize, Parchment,
                                    TextAlignmentOptions.TopLeft);
            body.textWrappingMode = TextWrappingModes.Normal;
            body.overflowMode = TextOverflowModes.Overflow;

            // Measure the wrapped body so the panel is exactly as tall as the
            // text needs. A fixed height would either clip a long conflict list
            // or leave a short one floating in empty space.
            float textW = w - pad * 2f;
            float bodyH = body.GetPreferredValues(body.text, textW, 0f).y;
            float h = pad + titleH + pad * 0.5f + bodyH + pad + btnH + pad;

            RectTransform rt = _root.transform as RectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = Vector2.zero;
            AddOutline(_root, Warn);

            Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(pad, -pad),
                  new Vector2(textW, titleH));
            Place(body.rectTransform, new Vector2(0f, 1f),
                  new Vector2(pad, -(pad + titleH + pad * 0.5f)), new Vector2(textW, bodyH));

            Button ok = NewButton(_root.transform, "OK", 13f * S, Close);
            Place(ok.transform as RectTransform, new Vector2(0.5f, 0f), new Vector2(0f, pad),
                  new Vector2(Mathf.Round(90f * S), btnH), new Vector2(0.5f, 0f));
        }

        // ------------------------------------------------------------------
        // Builders -- small copies rather than reaching into KistUI, so this
        // dialog stays usable if that window is never opened.
        // ------------------------------------------------------------------

        private static TMP_FontAsset FindFont()
        {
            if (InventoryGui.instance != null)
            {
                TMP_Text sample = InventoryGui.instance.GetComponentInChildren<TMP_Text>(true);
                if (sample != null && sample.font != null) return sample.font;
            }
            return null;
        }

        private static GameObject NewPanel(string name, Transform parent, Color bg)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            Image img = go.GetComponent<Image>();
            img.color = bg;
            img.raycastTarget = bg.a > 0.01f;
            return go;
        }

        private static void AddOutline(GameObject go, Color colour)
        {
            Outline o = go.AddComponent<Outline>();
            o.effectColor = new Color(colour.r, colour.g, colour.b, 0.55f);
            o.effectDistance = new Vector2(2f, -2f);
        }

        private static TMP_Text NewText(Transform parent, string text, float size, Color colour,
                                        TextAlignmentOptions align)
        {
            GameObject go = new GameObject("text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.text = text;
            t.fontSize = size;
            t.color = colour;
            t.alignment = align;
            t.richText = true;
            t.raycastTarget = false;
            return t;
        }

        private static Button NewButton(Transform parent, string label, float size,
                                        UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = NewPanel("button", parent, BtnBg);
            Button b = go.AddComponent<Button>();
            b.targetGraphic = go.GetComponent<Image>();
            ColorBlock cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            b.colors = cb;
            b.onClick.AddListener(onClick);

            TMP_Text t = NewText(go.transform, label, size, Parchment, TextAlignmentOptions.Center);
            RectTransform trt = t.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(2f, 1f);
            trt.offsetMax = new Vector2(-2f, -1f);
            return b;
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            Place(rt, anchor, offset, size, new Vector2(anchor.x, anchor.y));
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 offset, Vector2 size, Vector2 pivot)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;
        }
    }
}
