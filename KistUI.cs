using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ThorsKist
{
    /// <summary>
    /// The Kist window.
    ///
    /// Built from bare uGUI at runtime and borrowing Valheim's own font, rather
    /// than shipping an asset bundle: one DLL, nothing to keep in step with the
    /// game's Unity version. Contents are read straight off KistClient's mirror,
    /// so the window can never show a Kist the server has not confirmed.
    /// </summary>
    public static class KistUI
    {
        public static bool IsOpen;

        private enum Tab { Kist, Hird }

        private static GameObject _root;
        private static RectTransform _content;      // scrolling item list
        private static RectTransform _hirdContent;
        private static GameObject _kistPage;
        private static GameObject _hirdPage;
        private static TMP_Text _title;
        private static TMP_Text _status;
        private static TMP_InputField _search;
        private static TMP_InputField _hirdName;
        private static Button _tabKist;
        private static Button _tabHird;
        private static TMP_FontAsset _font;
        private static Tab _tab = Tab.Kist;
        private static bool _dirty = true;
        private static readonly List<GameObject> _rows = new List<GameObject>();

        // Valheim's palette, close enough to sit beside the vanilla panels.
        private static readonly Color Parchment = new Color(0.87f, 0.82f, 0.70f, 1f);
        private static readonly Color Dim = new Color(0.62f, 0.58f, 0.50f, 1f);
        private static readonly Color PanelBg = new Color(0.07f, 0.065f, 0.06f, 0.96f);
        private static readonly Color RowBg = new Color(0.13f, 0.12f, 0.10f, 0.85f);
        private static readonly Color RowAlt = new Color(0.17f, 0.15f, 0.12f, 0.85f);
        private static readonly Color Accent = new Color(0.79f, 0.63f, 0.31f, 1f);
        private static readonly Color BtnBg = new Color(0.24f, 0.21f, 0.16f, 1f);
        private static readonly Color BtnBgHot = new Color(0.36f, 0.30f, 0.20f, 1f);
        private static readonly Color Bad = new Color(0.82f, 0.36f, 0.30f, 1f);

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public static void Tick()
        {
            if (Player.m_localPlayer == null)
            {
                if (IsOpen) Close();
                return;
            }

            if (Main.OpenKistKey.Value.IsDown() && !TypingSomewhere())
            {
                if (IsOpen) Close(); else Open();
            }

            if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();

            if (IsOpen)
            {
                // Vanilla frees the cursor for its own panels; this window has to
                // ask for the same treatment every frame or something else in the
                // frame will take it back.
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;

                // The distance readout changes as the player walks, and crossing
                // the radius has to switch the Take buttons on or off -- but only
                // the crossing is worth a full rebuild.
                if (_status != null) _status.text = StatusLine();
                bool canTake = KistClient.CanTake;
                if (canTake != _lastCanTake) { _lastCanTake = canTake; _dirty = true; }

                if (_dirty) Rebuild();
            }
        }

        private static bool _lastCanTake;

        private static bool TypingSomewhere()
        {
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;
            if (Console.IsVisible()) return true;
            if (_search != null && _search.isFocused) return true;
            if (_hirdName != null && _hirdName.isFocused) return true;
            return false;
        }

        public static void Open()
        {
            if (InventoryGui.instance == null) return;
            Build();
            if (_root == null) return;
            IsOpen = true;
            _root.SetActive(true);
            // Other panels are added to the same canvas at runtime; drawing last
            // is the only thing that keeps this window on top of them.
            _root.transform.SetAsLastSibling();
            _dirty = true;
            KistClient.RequestState();
        }

        public static void Close()
        {
            IsOpen = false;
            if (_root != null) _root.SetActive(false);
        }

        public static void MarkDirty() { _dirty = true; }

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        private static void Build()
        {
            if (_root != null) return;

            Canvas canvas = InventoryGui.instance.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            Transform parent = (canvas.rootCanvas != null ? canvas.rootCanvas : canvas).transform;

            _font = FindFont();
            KistClient.OnStateChanged = MarkDirty;

            _root = NewPanel("ThorsKist_Window", parent, PanelBg);
            RectTransform rt = _root.transform as RectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1000f, 660f);
            rt.anchoredPosition = Vector2.zero;
            AddOutline(_root, Accent);

            // Header ---------------------------------------------------------
            _title = NewText(_root.transform, "THOR'S KIST", 30, Accent, TextAlignmentOptions.Left);
            Place(_title.rectTransform, new Vector2(0f, 1f), new Vector2(24f, -20f), new Vector2(640f, 40f));

            Button close = NewButton(_root.transform, "X", 20, Close);
            Place((close.transform as RectTransform), new Vector2(1f, 1f), new Vector2(-46f, -20f), new Vector2(36f, 36f));

            _status = NewText(_root.transform, "", 16, Dim, TextAlignmentOptions.Right);
            Place(_status.rectTransform, new Vector2(1f, 1f), new Vector2(-90f, -26f), new Vector2(560f, 28f));

            _tabKist = NewButton(_root.transform, "KIST", 18, () => { _tab = Tab.Kist; _dirty = true; });
            Place((_tabKist.transform as RectTransform), new Vector2(0f, 1f), new Vector2(24f, -66f), new Vector2(140f, 34f));

            _tabHird = NewButton(_root.transform, "HIRD", 18, () => { _tab = Tab.Hird; _dirty = true; });
            Place((_tabHird.transform as RectTransform), new Vector2(0f, 1f), new Vector2(172f, -66f), new Vector2(140f, 34f));

            // Kist page ------------------------------------------------------
            _kistPage = NewPanel("page_kist", _root.transform, Color.clear);
            Stretch(_kistPage.transform as RectTransform, 24f, 24f, 110f, 56f);

            _search = NewInput(_kistPage.transform, "Search...");
            Place((_search.transform as RectTransform), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(360f, 32f), new Vector2(0f, 1f));
            _search.onValueChanged.AddListener(delegate { _dirty = true; });

            _content = NewScrollList(_kistPage.transform, 42f);

            // Hird page ------------------------------------------------------
            _hirdPage = NewPanel("page_hird", _root.transform, Color.clear);
            Stretch(_hirdPage.transform as RectTransform, 24f, 24f, 110f, 56f);
            _hirdContent = NewScrollList(_hirdPage.transform, 0f);

            _root.SetActive(false);
        }

        private static TMP_FontAsset FindFont()
        {
            // Valheim's norse-flavoured font, taken from a label the game has
            // already built. Falling back to the TMP default would look wrong
            // but still readable, so a miss is not fatal.
            if (InventoryGui.instance != null)
            {
                TMP_Text sample = InventoryGui.instance.GetComponentInChildren<TMP_Text>(true);
                if (sample != null && sample.font != null) return sample.font;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Redraw
        // ------------------------------------------------------------------

        private static void Rebuild()
        {
            _dirty = false;
            if (_root == null) return;

            bool kistTab = _tab == Tab.Kist;
            _kistPage.SetActive(kistTab);
            _hirdPage.SetActive(!kistTab);
            Tint(_tabKist, kistTab ? BtnBgHot : BtnBg);
            Tint(_tabHird, kistTab ? BtnBg : BtnBgHot);

            _title.text = KistClient.InHird
                ? "THOR'S KIST  —  " + KistClient.Hird.Name.ToUpperInvariant()
                : "THOR'S KIST";

            _status.text = StatusLine();

            ClearRows();
            if (kistTab) BuildKistRows(); else BuildHirdRows();
        }

        private static string StatusLine()
        {
            if (!KistClient.InHird)
                return KistClient.HasPendingInvite ? "<color=#C9A227>You have an invitation</color>" : "No hird";

            if (!KistClient.KistUnlocked)
                return "<color=#D15C4C>Locked — needs " + KistClient.MinHirdSize + " members (" +
                       KistClient.Hird.Members.Count + " so far)</color>";

            if (Main.UnlimitedRange.Value) return "In range (unlimited)";

            int d = Mathf.RoundToInt(KistClient.DistanceToAnchor);
            string colour = KistClient.CanTake ? "#9BBF7A" : "#D15C4C";
            return "<color=" + colour + ">" + d + "m from the Kist</color>   " +
                   "deposit " + Mathf.RoundToInt(Main.DepositRadius.Value) + "m / " +
                   "take " + Mathf.RoundToInt(Main.TakeRadius.Value) + "m / " +
                   "craft " + Mathf.RoundToInt(Main.CraftRadius.Value) + "m";
        }

        private static void ClearRows()
        {
            foreach (GameObject go in _rows) if (go != null) Object.Destroy(go);
            _rows.Clear();
        }

        private static void BuildKistRows()
        {
            if (!KistClient.InHird)
            {
                AddNotice(_content, "You are not in a hird. Open the HIRD tab to found one.");
                return;
            }
            if (KistClient.Kist == null || KistClient.Kist.NrOfItems() == 0)
            {
                AddNotice(_content, "The Kist is empty. Hold " + Main.DepositModifier.Value.MainKey +
                                    " and left-click an item in your inventory to store it.");
                return;
            }

            string filter = _search != null ? (_search.text ?? "").Trim().ToLowerInvariant() : "";
            List<ItemDrop.ItemData> items = KistClient.Kist.GetAllItemsSortedByName();
            bool canTake = KistClient.CanTake;
            int shown = 0;

            foreach (ItemDrop.ItemData item in items)
            {
                string label = Localization.instance != null
                    ? Localization.instance.Localize(item.m_shared.m_name)
                    : item.m_shared.m_name;
                if (filter.Length > 0 && label.ToLowerInvariant().IndexOf(filter) < 0) continue;
                AddItemRow(item, label, shown, canTake);
                shown++;
            }

            if (shown == 0) AddNotice(_content, "Nothing here matches \"" + filter + "\".");
        }

        private static void AddItemRow(ItemDrop.ItemData item, string label, int index, bool canTake)
        {
            GameObject row = NewPanel("row", _content, index % 2 == 0 ? RowBg : RowAlt);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = 46f;
            le.preferredHeight = 46f;
            _rows.Add(row);

            GameObject iconGo = new GameObject("icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            iconGo.transform.SetParent(row.transform, false);
            Image icon = iconGo.GetComponent<Image>();
            icon.sprite = item.GetIcon();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            Place(iconGo.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(30f, 0f), new Vector2(38f, 38f));

            string quality = item.m_shared.m_maxQuality > 1 ? "  <color=#8C8677>✦" + item.m_quality + "</color>" : "";
            TMP_Text name = NewText(row.transform, label + quality, 18, Parchment, TextAlignmentOptions.Left);
            Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(62f, 0f), new Vector2(430f, 34f), new Vector2(0f, 0.5f));

            TMP_Text count = NewText(row.transform, "x" + item.m_stack, 18, Accent, TextAlignmentOptions.Right);
            Place(count.rectTransform, new Vector2(1f, 0.5f), new Vector2(-380f, 0f), new Vector2(120f, 34f));

            Vector2i slot = item.m_gridPos;
            string itemName = item.m_shared.m_name;
            int stack = item.m_stack;

            AddTakeButton(row, "1", -286f, canTake, slot, itemName, 1);
            AddTakeButton(row, "10", -206f, canTake && stack > 1, slot, itemName, stack < 10 ? stack : 10);
            AddTakeButton(row, "All", -120f, canTake, slot, itemName, stack);

            if (!canTake)
            {
                TMP_Text why = NewText(row.transform, KistClient.KistUnlocked ? "out of range" : "locked",
                                       14, Bad, TextAlignmentOptions.Right);
                Place(why.rectTransform, new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(100f, 30f));
            }
        }

        private static void AddTakeButton(GameObject row, string label, float x, bool enabled,
                                          Vector2i slot, string itemName, int amount)
        {
            Button b = NewButton(row.transform, label, 16, delegate
            {
                KistClient.Take(slot, itemName, amount);
            });
            Place(b.transform as RectTransform, new Vector2(1f, 0.5f), new Vector2(x, 0f), new Vector2(72f, 30f));
            b.interactable = enabled;
            if (!enabled) Tint(b, new Color(0.16f, 0.15f, 0.13f, 1f));
        }

        // ------------------------------------------------------------------
        // Hird tab
        // ------------------------------------------------------------------

        private static void BuildHirdRows()
        {
            if (KistClient.HasPendingInvite)
            {
                AddSection("INVITATION");
                AddNotice(_hirdContent, KistClient.PendingInviteFrom + " invites you to join \"" +
                                        KistClient.PendingInviteHird + "\".");
                GameObject row = AddRow(46f);
                Button accept = NewButton(row.transform, "Accept", 16, KistClient.AcceptInvite);
                Place(accept.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(90f, 0f), new Vector2(120f, 32f));
                Button decline = NewButton(row.transform, "Decline", 16, KistClient.DeclineInvite);
                Place(decline.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(222f, 0f), new Vector2(120f, 32f));
            }

            if (!KistClient.InHird)
            {
                AddSection("FOUND A HIRD");
                AddNotice(_hirdContent,
                    "A Kist belongs to a hird, never to one person. Found one, then invite at least " +
                    (KistClient.MinHirdSize - 1) + " more — the Kist stays shut until then.");

                GameObject row = AddRow(52f);
                _hirdName = NewInput(row.transform, "Name your hird...");
                Place(_hirdName.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(240f, 0f), new Vector2(420f, 34f));
                Button create = NewButton(row.transform, "Found", 16, delegate
                {
                    KistClient.CreateHird(_hirdName != null ? _hirdName.text : "");
                });
                Place(create.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(672f, 0f), new Vector2(120f, 34f));
                return;
            }

            Hird h = KistClient.Hird;
            bool owner = KistClient.IsOwner;

            AddSection("MEMBERS (" + h.Members.Count + ")");
            for (int i = 0; i < h.Members.Count; i++)
            {
                HirdMember m = h.Members[i];
                GameObject row = AddRow(40f, i % 2 == 0 ? RowBg : RowAlt);
                string tag = m.PlayerId == h.OwnerId ? "  <color=#C9A227>(founder)</color>" : "";
                TMP_Text t = NewText(row.transform, m.Name + tag, 18, Parchment, TextAlignmentOptions.Left);
                Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(520f, 30f), new Vector2(0f, 0.5f));

                if (owner && m.PlayerId != h.OwnerId)
                {
                    long id = m.PlayerId;
                    Button kick = NewButton(row.transform, "Remove", 15, delegate { KistClient.Kick(id); });
                    Place(kick.transform as RectTransform, new Vector2(1f, 0.5f), new Vector2(-24f, 0f), new Vector2(110f, 28f));
                }
            }

            if (h.Invites.Count > 0)
            {
                AddSection("INVITED");
                foreach (HirdInvite inv in h.Invites)
                {
                    GameObject row = AddRow(36f);
                    TMP_Text t = NewText(row.transform, inv.Name + "  <color=#8C8677>awaiting reply</color>",
                                         16, Dim, TextAlignmentOptions.Left);
                    Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(520f, 28f), new Vector2(0f, 0.5f));
                }
            }

            if (owner)
            {
                AddSection("INVITE");
                if (KistClient.Roster.Count == 0)
                {
                    AddNotice(_hirdContent, "Nobody else is online who could join a hird.");
                }
                else
                {
                    for (int i = 0; i < KistClient.Roster.Count; i++)
                    {
                        KistClient.RosterEntry e = KistClient.Roster[i];
                        if (h.IsInvited(e.PlayerId)) continue;
                        GameObject row = AddRow(38f, i % 2 == 0 ? RowBg : RowAlt);
                        TMP_Text t = NewText(row.transform, e.Name, 17, Parchment, TextAlignmentOptions.Left);
                        Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(520f, 28f), new Vector2(0f, 0.5f));
                        long id = e.PlayerId;
                        string nm = e.Name;
                        Button b = NewButton(row.transform, "Invite", 15, delegate { KistClient.Invite(id, nm); });
                        Place(b.transform as RectTransform, new Vector2(1f, 0.5f), new Vector2(-24f, 0f), new Vector2(110f, 28f));
                    }
                }
            }

            AddSection("THE KIST");
            AddNotice(_hirdContent, "The Kist sits at " + Mathf.RoundToInt(h.Anchor.x) + ", " +
                                    Mathf.RoundToInt(h.Anchor.z) + ". Everything reaches it from within the " +
                                    "configured radius.");
            GameObject actions = AddRow(52f);
            float x = 24f;
            if (owner)
            {
                Button move = NewButton(actions.transform, "Move the Kist here", 15, KistClient.SetAnchorHere);
                Place(move.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(200f, 32f));
                x += 212f;
            }
            Button leave = NewButton(actions.transform, "Leave hird", 15, KistClient.Leave);
            Place(leave.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(150f, 32f));
            x += 162f;
            if (owner)
            {
                Button disband = NewButton(actions.transform, "Disband", 15, KistClient.Disband);
                Place(disband.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(130f, 32f));
                Tint(disband, new Color(0.34f, 0.16f, 0.14f, 1f));
            }
        }

        private static GameObject AddSection(string label)
        {
            GameObject row = AddRow(38f, Color.clear);
            TMP_Text t = NewText(row.transform, label, 17, Accent, TextAlignmentOptions.Left);
            Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f), new Vector2(600f, 30f), new Vector2(0f, 0.5f));
            return row;
        }

        private static GameObject AddRow(float height) { return AddRow(height, Color.clear); }

        private static GameObject AddRow(float height, Color bg)
        {
            GameObject row = NewPanel("row", _hirdContent, bg);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            _rows.Add(row);
            return row;
        }

        private static void AddNotice(RectTransform parent, string text)
        {
            GameObject row = NewPanel("notice", parent, Color.clear);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = 48f;
            le.preferredHeight = 48f;
            _rows.Add(row);
            TMP_Text t = NewText(row.transform, text, 17, Dim, TextAlignmentOptions.TopLeft);
            Stretch(t.rectTransform, 16f, 16f, 4f, 4f);
        }

        // ------------------------------------------------------------------
        // Small uGUI builders
        // ------------------------------------------------------------------

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
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private static Button NewButton(Transform parent, string label, float size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = NewPanel("button", parent, BtnBg);
            Button b = go.AddComponent<Button>();
            b.targetGraphic = go.GetComponent<Image>();
            ColorBlock cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            b.colors = cb;
            b.onClick.AddListener(onClick);

            TMP_Text t = NewText(go.transform, label, size, Parchment, TextAlignmentOptions.Center);
            Stretch(t.rectTransform, 4f, 4f, 2f, 2f);
            return b;
        }

        private static void Tint(Button b, Color c)
        {
            if (b == null) return;
            Image img = b.GetComponent<Image>();
            if (img != null) img.color = c;
        }

        private static TMP_InputField NewInput(Transform parent, string placeholder)
        {
            GameObject go = NewPanel("input", parent, new Color(0.05f, 0.05f, 0.045f, 1f));
            AddOutline(go, new Color(0.4f, 0.36f, 0.28f, 1f));

            GameObject area = new GameObject("area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            Stretch(area.transform as RectTransform, 10f, 10f, 4f, 4f);

            TMP_Text text = NewText(area.transform, "", 17, Parchment, TextAlignmentOptions.Left);
            Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
            text.raycastTarget = true;

            TMP_Text hint = NewText(area.transform, placeholder, 17, new Color(0.45f, 0.42f, 0.36f, 1f),
                                    TextAlignmentOptions.Left);
            Stretch(hint.rectTransform, 0f, 0f, 0f, 0f);

            TMP_InputField field = go.AddComponent<TMP_InputField>();
            field.textViewport = area.transform as RectTransform;
            field.textComponent = text;
            field.placeholder = hint;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.characterLimit = 40;
            return field;
        }

        /// <summary>
        /// A vertical scrolling list. Returns the content transform rows should
        /// be parented to.
        /// </summary>
        private static RectTransform NewScrollList(Transform parent, float topInset)
        {
            GameObject viewport = NewPanel("viewport", parent, new Color(0f, 0f, 0f, 0.25f));
            viewport.AddComponent<RectMask2D>();
            RectTransform vrt = viewport.transform as RectTransform;
            Stretch(vrt, 0f, 0f, topInset, 0f);

            GameObject content = new GameObject("content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            RectTransform crt = content.transform as RectTransform;
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.offsetMin = new Vector2(0f, 0f);
            crt.offsetMax = new Vector2(0f, 0f);

            VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 3f;
            layout.padding = new RectOffset(4, 4, 4, 4);

            ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = viewport.AddComponent<ScrollRect>();
            scroll.content = crt;
            scroll.viewport = vrt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            return crt;
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            Place(rt, anchor, offset, size, anchor);
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 offset, Vector2 size, Vector2 pivot)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;
        }

        private static void Stretch(RectTransform rt, float left, float right, float top, float bottom)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }
    }
}
