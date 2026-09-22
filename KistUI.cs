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
    ///
    /// The list is deliberately dense. A shared vault holds hundreds of stacks,
    /// and a row you have to scroll to is a row you will not use.
    /// </summary>
    public static class KistUI
    {
        public static bool IsOpen;

        /// <summary>
        /// True while this window should be the only thing receiving input.
        ///
        /// Checks the actual GameObject, not just the flag: if IsOpen ever went
        /// stale while the panel was destroyed or hidden, the game would be left
        /// permanently deaf with nothing on screen to explain why.
        /// </summary>
        public static bool BlocksGameInput
        {
            get { return IsOpen && _root != null && _root.activeInHierarchy; }
        }

        /// <summary>True while the anchor button is actually on screen, so the
        /// client only pesters the server about it when someone can see it.</summary>
        public static bool WantsAnchorStatus
        {
            get { return IsOpen && _tab == Tab.Hird && KistClient.IsOwner; }
        }

        private enum Tab { Kist, Hird }

        private static GameObject _root;
        private static RectTransform _content;      // scrolling item list
        private static RectTransform _hirdContent;
        private static ScrollRect _kistScroll;
        private static ScrollRect _hirdScroll;
        private static GameObject _kistPage;
        private static GameObject _hirdPage;
        private static TMP_Text _title;
        private static TMP_Text _status;
        private static TMP_Text _countLabel;
        private static TMP_InputField _search;
        private static TMP_InputField _hirdName;
        // The hird-name field lives inside the rebuilt row list, so it is
        // destroyed and recreated on every redraw. Keeping the draft here means
        // a redraw mid-sentence does not wipe what was typed.
        private static string _hirdNameDraft = "";
        private static TMP_InputField _renameField;
        private static string _renameDraft = "";
        // Which hird name the rename box was last filled from, so a rename by
        // somebody else refreshes the box but a half-typed one is left alone.
        private static string _renameSeededFrom;
        private static Button _tabKist;
        private static Button _tabHird;
        private static GameObject _sortBar;
        private static readonly List<Button> _sortButtons = new List<Button>();
        private static readonly SortMode[] SortOrder =
        {
            SortMode.Name, SortMode.Quantity, SortMode.Progression, SortMode.Category
        };
        private static TMP_FontAsset _font;
        private static Tab _tab = Tab.Kist;
        private static bool _dirty = true;
        private static bool _lastCanTake;
        private static Vector2 _builtForScreen;
        private static readonly List<GameObject> _rows = new List<GameObject>();

        // ---- Metrics -------------------------------------------------------
        // One scale knob drives every size, so uiScale actually means something
        // rather than shrinking the text inside fixed-height rows.
        private static float S { get { return Mathf.Clamp(Main.UiScale.Value, 0.6f, 1.6f); } }
        private static float RowH { get { return Mathf.Round(18f * S); } }
        private static float FontRow { get { return 12f * S; } }
        private static float FontSmall { get { return 10.5f * S; } }
        private static float FontHead { get { return 20f * S; } }
        private static float IconSize { get { return Mathf.Round(16f * S); } }
        private const float RowGap = 0f;

        // ---- Row geometry --------------------------------------------------
        // One place decides where the take buttons sit and where the strip ends,
        // because computing those separately is what put the stack count
        // underneath the buttons in 0.2.0.
        private static float BtnW { get { return Mathf.Round(30f * S); } }
        private static float BtnGap { get { return 2f; } }
        private static float BtnEdge { get { return 3f; } }
        /// <summary>Distance from the row's right edge to the LEFT edge of the button strip.</summary>
        private static float BtnStripW { get { return BtnEdge + BtnW * 3f + BtnGap * 2f; } }

        // Valheim's palette, close enough to sit beside the vanilla panels.
        private static readonly Color Parchment = new Color(0.87f, 0.82f, 0.70f, 1f);
        private static readonly Color Dim = new Color(0.62f, 0.58f, 0.50f, 1f);
        private static readonly Color PanelBg = new Color(0.07f, 0.065f, 0.06f, 0.97f);
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

            if (!IsOpen) return;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // Esc clears a search first and only closes on the second press,
                // so a stray filter never strands you on an empty-looking Kist.
                if (_search != null && _search.text.Length > 0) { _search.text = ""; _dirty = true; }
                else Close();
                return;
            }

            // Vanilla frees the cursor for its own panels; this window has to
            // ask for the same treatment every frame or something else in the
            // frame will take it back.
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            HandleScroll();

            // The distance readout changes as the player walks, and crossing
            // the radius has to switch the Take buttons on or off -- but only
            // the crossing is worth a full rebuild.
            if (_status != null) _status.text = StatusLine();
            bool canTake = KistClient.CanTake;
            if (canTake != _lastCanTake) { _lastCanTake = canTake; _dirty = true; }

            if (_dirty) Rebuild();
        }

        /// <summary>
        /// Scrolling by hand rather than through ScrollRect.scrollSensitivity.
        /// Unity multiplies the raw wheel delta, which differs per platform and
        /// per device, so the same setting gives wildly different speeds. Moving
        /// a fixed number of ROWS per notch is what a person actually means.
        /// </summary>
        private static void HandleScroll()
        {
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(wheel, 0f)) return;

            ScrollRect target = _tab == Tab.Kist ? _kistScroll : _hirdScroll;
            if (target == null || target.content == null) return;

            float pixels = Mathf.Clamp(wheel, -3f, 3f) * Main.ScrollRowsPerNotch.Value * (RowH + RowGap);
            float hidden = target.content.rect.height - target.viewport.rect.height;
            if (hidden <= 0f) return;

            Vector2 pos = target.content.anchoredPosition;
            pos.y = Mathf.Clamp(pos.y - pixels, 0f, hidden);
            target.content.anchoredPosition = pos;
        }

        private static bool TypingSomewhere()
        {
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;
            if (Console.IsVisible()) return true;
            if (_search != null && _search.isFocused) return true;
            if (_hirdName != null && _hirdName.isFocused) return true;
            if (_renameField != null && _renameField.isFocused) return true;
            return false;
        }

        public static void Open()
        {
            if (InventoryGui.instance == null) return;

            // A resolution change between openings would leave a window sized
            // for the old screen, so rebuild rather than stretch.
            if (_root != null && _builtForScreen != new Vector2(Screen.width, Screen.height))
            {
                Object.Destroy(_root);
                _root = null;
            }

            Build();
            if (_root == null) return;
            IsOpen = true;
            _root.SetActive(true);
            // Other panels are added to the same canvas at runtime; drawing last
            // is the only thing that keeps this window on top of them.
            _root.transform.SetAsLastSibling();
            _dirty = true;
            KistClient.RequestState();

            // Land in the search box: the common reason to open a vault holding
            // hundreds of stacks is to find one thing.
            if (_tab == Tab.Kist && _search != null)
            {
                _search.text = "";
                _search.ActivateInputField();
            }
        }

        public static void Close()
        {
            IsOpen = false;
            if (_search != null) _search.DeactivateInputField();
            if (_root != null) _root.SetActive(false);
        }

        public static void MarkDirty() { _dirty = true; }

        /// <summary>
        /// Clicking the sort mode that is already active reverses it, which is
        /// what every list in every other program does.
        /// </summary>
        private static void SetSort(SortMode mode)
        {
            if (Main.KistSortMode.Value == mode) _sortReversed = !_sortReversed;
            else { Main.KistSortMode.Value = mode; _sortReversed = false; }
            _dirty = true;
        }

        private static bool _sortReversed;

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
            _builtForScreen = new Vector2(Screen.width, Screen.height);

            RectTransform canvasRect = parent as RectTransform;
            float availW = canvasRect != null ? canvasRect.rect.width : Screen.width;
            float availH = canvasRect != null ? canvasRect.rect.height : Screen.height;
            // Take most of the screen: the whole complaint about the old window
            // was that it showed nine rows.
            float w = Mathf.Min(1180f, availW * 0.82f);
            float h = availH * 0.86f;

            float headH = Mathf.Round(34f * S);
            float barH = Mathf.Round(26f * S);
            float pad = Mathf.Round(10f * S);

            _root = NewPanel("ThorsKist_Window", parent, PanelBg);
            RectTransform rt = _root.transform as RectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = Vector2.zero;
            AddOutline(_root, Accent);

            // Header ---------------------------------------------------------
            _title = NewText(_root.transform, "THOR'S KIST", FontHead, Accent, TextAlignmentOptions.Left);
            Place(_title.rectTransform, new Vector2(0f, 1f), new Vector2(pad, -pad), new Vector2(w * 0.45f, headH));

            Button close = NewButton(_root.transform, "X", FontRow, Close);
            Place(close.transform as RectTransform, new Vector2(1f, 1f), new Vector2(-pad, -pad),
                  new Vector2(headH, headH));

            _status = NewText(_root.transform, "", FontSmall, Dim, TextAlignmentOptions.Right);
            Place(_status.rectTransform, new Vector2(1f, 1f), new Vector2(-(pad + headH + 6f), -pad),
                  new Vector2(w * 0.5f, headH));

            // Tab + search bar -------------------------------------------------
            float barY = -(pad + headH + 4f);
            float tabW = Mathf.Round(92f * S);

            _tabKist = NewButton(_root.transform, "KIST", FontRow, () => { _tab = Tab.Kist; _dirty = true; });
            Place(_tabKist.transform as RectTransform, new Vector2(0f, 1f), new Vector2(pad, barY),
                  new Vector2(tabW, barH));

            _tabHird = NewButton(_root.transform, "HIRD", FontRow, () => { _tab = Tab.Hird; _dirty = true; });
            Place(_tabHird.transform as RectTransform, new Vector2(0f, 1f), new Vector2(pad + tabW + 4f, barY),
                  new Vector2(tabW, barH));

            _countLabel = NewText(_root.transform, "", FontSmall, Dim, TextAlignmentOptions.Left);
            Place(_countLabel.rectTransform, new Vector2(0f, 1f), new Vector2(pad + tabW * 2f + 14f, barY),
                  new Vector2(w * 0.25f, barH), new Vector2(0f, 1f));

            // Search sits in the top bar, always visible, never scrolled away.
            _search = NewInput(_root.transform, "Search the Kist...");
            float searchW = Mathf.Min(340f, w * 0.34f);
            Place(_search.transform as RectTransform, new Vector2(1f, 1f), new Vector2(-pad, barY),
                  new Vector2(searchW, barH), new Vector2(1f, 1f));
            _search.onValueChanged.AddListener(delegate { _dirty = true; });

            // Sort bar ---------------------------------------------------------
            // Its own row rather than squeezed in beside the tabs: four buttons
            // plus the search box does not fit the bar above at any sane width,
            // and a button that lands underneath another one is a button nobody
            // can press.
            float sortY = barY - (barH + 4f);
            _sortBar = NewPanel("sortbar", _root.transform, Color.clear);
            Place(_sortBar.transform as RectTransform, new Vector2(0f, 1f), new Vector2(pad, sortY),
                  new Vector2(w - pad * 2f, barH), new Vector2(0f, 1f));

            TMP_Text sortLabel = NewText(_sortBar.transform, "SORT", FontSmall, Dim,
                                         TextAlignmentOptions.Left);
            Place(sortLabel.rectTransform, new Vector2(0f, 0.5f), new Vector2(2f, 0f),
                  new Vector2(44f * S, barH), new Vector2(0f, 0.5f));

            _sortButtons.Clear();
            float sortBtnW = Mathf.Round(84f * S);
            float sortX = 44f * S + 6f;
            foreach (SortMode mode in SortOrder)
            {
                SortMode captured = mode;
                Button b = NewButton(_sortBar.transform, KistSort.ModeLabel(mode), FontSmall,
                                     delegate { SetSort(captured); });
                Place(b.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(sortX, 0f),
                      new Vector2(sortBtnW, barH - 4f), new Vector2(0f, 0.5f));
                _sortButtons.Add(b);
                sortX += sortBtnW + 3f;
            }

            float pageTop = pad + headH + 4f + barH + 4f + barH + 6f;

            // Pages ------------------------------------------------------------
            _kistPage = NewPanel("page_kist", _root.transform, Color.clear);
            Stretch(_kistPage.transform as RectTransform, pad, pad, pageTop, pad);
            _kistScroll = NewScrollList(_kistPage.transform, out _content);

            _hirdPage = NewPanel("page_hird", _root.transform, Color.clear);
            Stretch(_hirdPage.transform as RectTransform, pad, pad, pageTop, pad);
            _hirdScroll = NewScrollList(_hirdPage.transform, out _hirdContent);

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
            _search.gameObject.SetActive(kistTab);
            if (_sortBar != null) _sortBar.SetActive(kistTab);
            Tint(_tabKist, kistTab ? BtnBgHot : BtnBg);
            Tint(_tabHird, kistTab ? BtnBg : BtnBgHot);

            for (int i = 0; i < _sortButtons.Count && i < SortOrder.Length; i++)
            {
                bool active = SortOrder[i] == Main.KistSortMode.Value;
                Tint(_sortButtons[i], active ? BtnBgHot : BtnBg);
                TMP_Text label = _sortButtons[i].GetComponentInChildren<TMP_Text>();
                if (label == null) continue;
                // The arrow is the only thing that says which way round the list
                // is, and reversing is a click on an already-active button --
                // without it that click looks like it did nothing.
                label.text = KistSort.ModeLabel(SortOrder[i]) + (active ? (_sortReversed ? " ^" : " v") : "");
                label.color = active ? Accent : Parchment;
            }

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
            if (_countLabel != null) _countLabel.text = "";

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
            SortMode mode = Main.KistSortMode.Value;
            List<ItemDrop.ItemData> items = KistSort.Arrange(KistClient.Kist.GetAllItems(), mode);
            if (_sortReversed) items.Reverse();
            bool canTake = KistClient.CanTake;
            int shown = 0;

            foreach (ItemDrop.ItemData item in items)
            {
                string label = KistSort.DisplayName(item);
                if (filter.Length > 0 && label.ToLowerInvariant().IndexOf(filter) < 0) continue;
                AddItemRow(item, label, KistSort.RowTag(item, mode), shown, canTake);
                shown++;
            }

            if (_countLabel != null)
            {
                _countLabel.text = filter.Length > 0
                    ? shown + " of " + items.Count + " stacks"
                    : items.Count + " stacks";
            }

            if (shown == 0) AddNotice(_content, "Nothing here matches \"" + filter + "\".");
        }

        private static void AddItemRow(ItemDrop.ItemData item, string label, string tag, int index, bool canTake)
        {
            GameObject row = NewPanel("row", _content, index % 2 == 0 ? RowBg : RowAlt);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = RowH;
            le.preferredHeight = RowH;
            _rows.Add(row);

            GameObject iconGo = new GameObject("icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            iconGo.transform.SetParent(row.transform, false);
            Image icon = iconGo.GetComponent<Image>();
            icon.sprite = item.GetIcon();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            // The pivot is (0, 0.5), so this offset is the icon's LEFT edge and
            // its right edge lands IconSize further on.
            float iconLeft = 4f + IconSize * 0.5f;
            Place(iconGo.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(iconLeft, 0f),
                  new Vector2(IconSize, IconSize));

            // Plain ASCII: a glyph the game's font lacks renders as a blank or a
            // box, and there is no way to find that out from here.
            string quality = item.m_shared.m_maxQuality > 1
                ? "  <color=#8C8677>q" + item.m_quality + "</color>" : "";
            TMP_Text name = NewText(row.transform, label + quality, FontRow, Parchment, TextAlignmentOptions.Left);
            // Start clear of the icon's right edge, then leave a gap. The old
            // offset was IconSize + 8, which at any icon larger than 8px landed
            // INSIDE the icon -- the name was printed over it, not beside it.
            float labelX = iconLeft + IconSize + 10f;
            Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(labelX, 0f),
                  // Keep the label's right edge exactly where it was, so the
                  // count and sort-key columns are untouched.
                  new Vector2(380f * S - (labelX - (IconSize + 8f)), RowH), new Vector2(0f, 0.5f));

            TMP_Text count = NewText(row.transform, "x" + item.m_stack, FontRow, Accent, TextAlignmentOptions.Right);
            Place(count.rectTransform, new Vector2(1f, 0.5f), new Vector2(-(BtnStripW + 8f), 0f),
                  new Vector2(90f * S, RowH));

            // Whatever the current sort is keyed on, spelled out. It is there to
            // be checked: a biome or a category that reads wrong is a table entry
            // worth reporting, and without it a wrong order just looks arbitrary.
            if (!string.IsNullOrEmpty(tag))
            {
                TMP_Text tagText = NewText(row.transform, tag, FontSmall, Dim, TextAlignmentOptions.Right);
                Place(tagText.rectTransform, new Vector2(1f, 0.5f),
                      new Vector2(-(BtnStripW + 8f + 90f * S + 10f), 0f), new Vector2(130f * S, RowH));
            }

            Vector2i slot = item.m_gridPos;
            string itemName = item.m_shared.m_name;
            int stack = item.m_stack;

            float bh = RowH - 2f;
            // Right edges, measured from the row's right edge outwards.
            AddTakeButton(row, "All", -BtnEdge, bh, canTake, slot, itemName, stack);
            AddTakeButton(row, "10", -(BtnEdge + BtnW + BtnGap), bh, canTake && stack > 1, slot, itemName,
                          stack < 10 ? stack : 10);
            AddTakeButton(row, "1", -(BtnEdge + (BtnW + BtnGap) * 2f), bh, canTake, slot, itemName, 1);
        }

        private static void AddTakeButton(GameObject row, string label, float x, float h, bool enabled,
                                          Vector2i slot, string itemName, int amount)
        {
            Button b = NewButton(row.transform, label, FontSmall, delegate
            {
                KistClient.Take(slot, itemName, amount);
            });
            Place(b.transform as RectTransform, new Vector2(1f, 0.5f), new Vector2(x, 0f), new Vector2(BtnW, h));
            b.interactable = enabled;
            if (!enabled) Tint(b, new Color(0.16f, 0.15f, 0.13f, 1f));
        }

        // ------------------------------------------------------------------
        // Hird tab
        // ------------------------------------------------------------------

        private static void BuildHirdRows()
        {
            if (_countLabel != null) _countLabel.text = "";

            if (KistClient.HasPendingInvite)
            {
                AddSection("INVITATION");
                AddNotice(_hirdContent, KistClient.PendingInviteFrom + " invites you to join \"" +
                                        KistClient.PendingInviteHird + "\".");
                GameObject row = AddRow(RowH + 4f);
                Button accept = NewButton(row.transform, "Accept", FontRow, KistClient.AcceptInvite);
                Place(accept.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f),
                      new Vector2(78f * S, RowH));
                Button decline = NewButton(row.transform, "Decline", FontRow, KistClient.DeclineInvite);
                Place(decline.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(84f * S + 14f, 0f),
                      new Vector2(78f * S, RowH));
            }

            if (!KistClient.InHird)
            {
                AddSection("FOUND A HIRD");
                AddNotice(_hirdContent,
                    "A Kist belongs to a hird, never to one person. Found one, then invite at least " +
                    (KistClient.MinHirdSize - 1) + " more — the Kist stays shut until then.");

                GameObject row = AddRow(RowH + 4f);
                _hirdName = NewInput(row.transform, "Name your hird...");
                Place(_hirdName.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f),
                      new Vector2(260f * S, RowH));
                _hirdName.text = _hirdNameDraft;
                _hirdName.onValueChanged.AddListener(delegate(string v) { _hirdNameDraft = v; });
                Button create = NewButton(row.transform, "Found", FontRow, delegate
                {
                    KistClient.CreateHird(_hirdNameDraft);
                    _hirdNameDraft = "";
                });
                Place(create.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(266f * S + 18f, 0f),
                      new Vector2(78f * S, RowH));
                return;
            }

            Hird h = KistClient.Hird;
            bool owner = KistClient.IsOwner;

            AddSection("MEMBERS (" + h.Members.Count + ")");
            for (int i = 0; i < h.Members.Count; i++)
            {
                HirdMember m = h.Members[i];
                GameObject row = AddRow(RowH, i % 2 == 0 ? RowBg : RowAlt);
                string tag = m.PlayerId == h.OwnerId ? "  <color=#C9A227>(founder)</color>" : "";
                TMP_Text t = NewText(row.transform, m.Name + tag, FontRow, Parchment, TextAlignmentOptions.Left);
                Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f),
                      new Vector2(420f * S, RowH), new Vector2(0f, 0.5f));

                if (owner && m.PlayerId != h.OwnerId)
                {
                    long id = m.PlayerId;
                    Button kick = NewButton(row.transform, "Remove", FontSmall, delegate { KistClient.Kick(id); });
                    Place(kick.transform as RectTransform, new Vector2(1f, 0.5f), new Vector2(-14f, 0f),
                          new Vector2(68f * S, RowH - 2f));
                }
            }

            if (h.Invites.Count > 0)
            {
                AddSection("INVITED");
                foreach (HirdInvite inv in h.Invites)
                {
                    GameObject row = AddRow(RowH);
                    TMP_Text t = NewText(row.transform, inv.Name + "  <color=#8C8677>awaiting reply</color>",
                                         FontSmall, Dim, TextAlignmentOptions.Left);
                    Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f),
                          new Vector2(420f * S, RowH), new Vector2(0f, 0.5f));
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
                        GameObject row = AddRow(RowH, i % 2 == 0 ? RowBg : RowAlt);
                        TMP_Text t = NewText(row.transform, e.Name, FontRow, Parchment, TextAlignmentOptions.Left);
                        Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f),
                              new Vector2(420f * S, RowH), new Vector2(0f, 0.5f));
                        long id = e.PlayerId;
                        string nm = e.Name;
                        Button b = NewButton(row.transform, "Invite", FontSmall, delegate { KistClient.Invite(id, nm); });
                        // RowH - 2f, matching Remove. At RowH - 4f the label had
                        // 12px of room for a 12.6px line and vanished completely.
                        Place(b.transform as RectTransform, new Vector2(1f, 0.5f), new Vector2(-14f, 0f),
                              new Vector2(84f * S, RowH - 2f));
                    }
                }
            }

            if (owner) AddRenameRow(h);

            AddSection("THE KIST");
            AddNotice(_hirdContent, "The Kist sits at " + Mathf.RoundToInt(h.Anchor.x) + ", " +
                                    Mathf.RoundToInt(h.Anchor.z) + ". Everything reaches it from within the " +
                                    "configured radius.");
            if (owner) AddAnchorRequirements();

            GameObject actions = AddRow(RowH + 4f);
            float x = 14f;
            if (owner)
            {
                int held = KistClient.AnchorCostHeld();
                bool affordable = held >= KistClient.AnchorCostAmount;
                bool allowed = KistClient.AnchorAllowed && affordable;

                Button move = NewButton(actions.transform, "Anchor Kist to this location", FontSmall,
                                        KistClient.SetAnchorHere);
                Place(move.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(x, 0f),
                      new Vector2(190f * S, RowH));
                move.interactable = allowed;
                if (!allowed) Tint(move, new Color(0.16f, 0.15f, 0.13f, 1f));
                x += 196f * S;
            }
            Button leave = NewButton(actions.transform, "Leave hird", FontSmall, KistClient.Leave);
            Place(leave.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(x, 0f),
                  new Vector2(92f * S, RowH));
            x += 98f * S;
            if (owner)
            {
                Button disband = NewButton(actions.transform, "Disband", FontSmall, KistClient.Disband);
                Place(disband.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(x, 0f),
                      new Vector2(80f * S, RowH));
                Tint(disband, new Color(0.34f, 0.16f, 0.14f, 1f));
            }
        }

        /// <summary>
        /// Lets the founder rename the hird. The name is only ever a label --
        /// the hird's id is a GUID and membership, the vault and the map pin all
        /// key off that -- so renaming cannot strand anything.
        /// </summary>
        private static void AddRenameRow(Hird h)
        {
            AddSection("HIRD NAME");

            if (_renameSeededFrom != h.Name)
            {
                _renameDraft = h.Name;
                _renameSeededFrom = h.Name;
            }

            GameObject row = AddRow(RowH + 4f);
            _renameField = NewInput(row.transform, "Name this hird...");
            Place(_renameField.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f),
                  new Vector2(260f * S, RowH));
            _renameField.text = _renameDraft;
            _renameField.onValueChanged.AddListener(delegate(string v) { _renameDraft = v; });

            Button rename = NewButton(row.transform, "Rename", FontSmall, delegate
            {
                KistClient.RenameHird(_renameDraft);
            });
            Place(rename.transform as RectTransform, new Vector2(0f, 0.5f), new Vector2(266f * S + 18f, 0f),
                  new Vector2(78f * S, RowH));
            // Deliberately always clickable. Greying it out would mean redrawing
            // the row list on every keystroke, which destroys the very field
            // being typed into -- and the server already refuses an empty name
            // and ignores a rename to the name it already has.
        }

        /// <summary>
        /// Spells out why the Kist can or cannot be moved. A greyed-out button
        /// with no explanation is worse than no button at all.
        /// </summary>
        private static void AddAnchorRequirements()
        {
            string cost = KistClient.AnchorCostLabel();
            int held = KistClient.AnchorCostHeld();
            bool affordable = held >= KistClient.AnchorCostAmount;

            if (cost.Length > 0)
            {
                string colour = affordable ? "#9BBF7A" : "#D15C4C";
                AddNotice(_hirdContent, "Re-anchoring costs <color=" + colour + ">" + cost + "</color>" +
                                        (affordable ? "" : "  (you carry " + held + ")") +
                                        ", and every member of the hird must be standing within " +
                                        Mathf.RoundToInt(Main.AnchorRadius.Value) + "m.");
            }

            if (!KistClient.AnchorAllowed && KistClient.AnchorReason.Length > 0)
                AddNotice(_hirdContent, "<color=#D15C4C>" + KistClient.AnchorReason + "</color>");
        }

        private static GameObject AddSection(string label)
        {
            GameObject row = AddRow(RowH + 3f, Color.clear);
            TMP_Text t = NewText(row.transform, label, FontSmall, Accent, TextAlignmentOptions.BottomLeft);
            Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(8f, 0f),
                  new Vector2(500f * S, RowH), new Vector2(0f, 0.5f));
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
            float h = NoticeHeight(text, parent);
            le.minHeight = h;
            le.preferredHeight = h;
            _rows.Add(row);
            TMP_Text t = NewText(row.transform, text, FontSmall, Dim, TextAlignmentOptions.TopLeft);
            t.overflowMode = TextOverflowModes.Overflow;
            Stretch(t.rectTransform, 10f, 10f, 2f, 2f);
        }

        /// <summary>
        /// Estimates how tall a wrapped notice needs to be.
        ///
        /// TMP cannot report a preferred height before it has been laid out, and
        /// the layout group needs the height first, so this guesses from the
        /// character count. Rich-text tags are stripped because they cost no
        /// width on screen but plenty of string length -- counting them would
        /// leave a gap under every coloured notice.
        /// </summary>
        private static float NoticeHeight(string text, RectTransform parent)
        {
            if (string.IsNullOrEmpty(text)) return RowH;
            string bare = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
            float width = parent != null && parent.rect.width > 1f ? parent.rect.width - 20f : 600f;
            float charsPerLine = Mathf.Max(20f, width / (FontSmall * 0.52f));
            int lines = Mathf.Max(1, Mathf.CeilToInt(bare.Length / charsPerLine));
            return Mathf.Max(RowH, lines * FontSmall * 1.3f + 6f);
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
            cb.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            b.colors = cb;
            b.onClick.AddListener(onClick);

            TMP_Text t = NewText(go.transform, label, size, Parchment, TextAlignmentOptions.Center);
            Stretch(t.rectTransform, 2f, 2f, 1f, 1f);
            // A button label must never be dropped. NewText defaults to Ellipsis,
            // and when a line does not fit VERTICALLY TextMeshPro renders nothing
            // at all -- not clipped text, not an ellipsis, an empty box. A button
            // two pixels shorter than its neighbours was enough to lose its label
            // entirely while still working perfectly when clicked. Labels here are
            // a word long, so overflowing is always the better failure.
            t.overflowMode = TextOverflowModes.Overflow;
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

            // Built inactive on purpose. AddComponent runs Awake and OnEnable
            // immediately, and TMP_InputField binds its label there -- so a
            // field whose textComponent is assigned afterwards keeps its value
            // but never draws it. That is exactly the "typing works, box looks
            // empty" symptom. Deferring activation until every reference is set
            // means the component initialises with a label to bind to.
            go.SetActive(false);

            GameObject area = new GameObject("area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            Stretch(area.transform as RectTransform, 8f, 8f, 2f, 2f);

            TMP_Text text = NewText(area.transform, "", FontRow, Parchment, TextAlignmentOptions.Left);
            Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
            text.raycastTarget = true;
            // An input field owns its own clipping and must not ellipsize, and
            // rich text would let a typed '<' swallow the rest of the line.
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;

            TMP_Text hint = NewText(area.transform, placeholder, FontRow, new Color(0.45f, 0.42f, 0.36f, 1f),
                                    TextAlignmentOptions.Left);
            Stretch(hint.rectTransform, 0f, 0f, 0f, 0f);
            hint.overflowMode = TextOverflowModes.Overflow;
            hint.richText = false;

            TMP_InputField field = go.AddComponent<TMP_InputField>();
            field.textViewport = area.transform as RectTransform;
            field.textComponent = text;
            field.placeholder = hint;
            field.fontAsset = text.font;
            field.pointSize = FontRow;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.characterLimit = 40;
            field.richText = false;
            // Without a visible caret there is no way to tell the box has focus.
            field.customCaretColor = true;
            field.caretColor = Parchment;
            field.caretWidth = 2;
            field.selectionColor = new Color(0.79f, 0.63f, 0.31f, 0.45f);

            go.SetActive(true);
            field.ForceLabelUpdate();
            return field;
        }

        /// <summary>
        /// A vertical scrolling list. Hands back the content transform rows are
        /// parented to, and the ScrollRect so the wheel can drive it directly.
        /// </summary>
        private static ScrollRect NewScrollList(Transform parent, out RectTransform content)
        {
            GameObject viewport = NewPanel("viewport", parent, new Color(0f, 0f, 0f, 0.25f));
            viewport.AddComponent<RectMask2D>();
            RectTransform vrt = viewport.transform as RectTransform;
            Stretch(vrt, 0f, 0f, 0f, 0f);

            GameObject contentGo = new GameObject("content", typeof(RectTransform));
            contentGo.transform.SetParent(viewport.transform, false);
            RectTransform crt = contentGo.transform as RectTransform;
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.offsetMin = Vector2.zero;
            crt.offsetMax = Vector2.zero;

            VerticalLayoutGroup layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            // MUST be true. With childControlHeight off, VerticalLayoutGroup
            // ignores LayoutElement heights entirely and lays rows out at their
            // own RectTransform height -- which for a bare new RectTransform is
            // 100 units. That is why every row was the same giant size no matter
            // what RowH said, through three releases of shrinking it.
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = RowGap;
            layout.padding = new RectOffset(2, 2, 2, 2);

            ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = viewport.AddComponent<ScrollRect>();
            scroll.content = crt;
            scroll.viewport = vrt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            // The wheel is handled in HandleScroll so the speed is a number of
            // rows rather than whatever this platform reports per notch.
            scroll.scrollSensitivity = 0f;

            content = crt;
            return scroll;
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
