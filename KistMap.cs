using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// The Kist's mark on everyone's map.
    ///
    /// The pin is not a thing the player owns -- it is a view of server state,
    /// so it is added unsaved and re-derived rather than written into the map
    /// save. That is what makes it follow the hird: move the Kist and it moves,
    /// leave or get kicked and it is simply no longer there to draw. It also
    /// means a stale pin cannot outlive a hird that no longer exists.
    /// </summary>
    public static class KistMap
    {
        private static Minimap.PinData _pin;
        private static Vector3 _pinnedAt;
        private static string _pinnedName;
        private static float _nextSync;

        // Set only while this class removes its own pin, so the guard that keeps
        // players from deleting it does not also keep us from moving it.
        private static bool _removingOurOwn;

        private static Sprite _icon;
        private static bool _iconResolved;

        /// <summary>The pin a player must not be allowed to delete.</summary>
        public static bool IsProtected(Minimap.PinData pin)
        {
            return !_removingOurOwn && pin != null && pin == _pin;
        }

        /// <summary>
        /// Called when the game wipes every pin (loading map data). The pin
        /// object is gone, so forget it and let the next sync put it back.
        /// </summary>
        public static void Forget()
        {
            _pin = null;
        }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextSync) return;
            _nextSync = Time.realtimeSinceStartup + 1f;
            Sync();
        }

        public static void Sync()
        {
            if (Minimap.instance == null) { _pin = null; return; }

            if (!Main.MapPin.Value || !KistClient.InHird)
            {
                Drop();
                return;
            }

            Vector3 at = KistClient.Hird.Anchor;
            string name = KistClient.Hird.Name;

            if (_pin != null && _pinnedAt == at && _pinnedName == name) return;

            Drop();
            // ownerID stays 0 deliberately. Minimap sweeps out every pin with a
            // non-zero owner when the shared map resets, and ours is not a
            // shared-map pin -- it belongs to the hird, not to a player.
            _pin = Minimap.instance.AddPin(at, Minimap.PinType.Icon3, name, false, false);
            if (_pin != null)
            {
                Sprite icon = ChestIcon();
                if (icon != null) _pin.m_icon = icon;
            }
            _pinnedAt = at;
            _pinnedName = name;
        }

        private static void Drop()
        {
            if (_pin == null) return;
            if (Minimap.instance != null)
            {
                _removingOurOwn = true;
                try { Minimap.instance.RemovePin(_pin); }
                finally { _removingOurOwn = false; }
            }
            _pin = null;
        }

        /// <summary>
        /// The build-menu icon of a chest, borrowed for the pin. Valheim has no
        /// chest among its own pin icons, and lifting the piece's sprite beats
        /// shipping an asset bundle for one image.
        /// </summary>
        private static Sprite ChestIcon()
        {
            if (_iconResolved) return _icon;
            if (ZNetScene.instance == null) return null;
            _iconResolved = true;

            // Configured name first, then the chests every world has, so a
            // renamed prefab in some future patch degrades to a plain pin
            // rather than to nothing at all.
            string[] candidates =
            {
                Main.MapPinPrefab.Value,
                "piece_chest_wood",
                "piece_chest_treasure",
                "piece_chest",
                "piece_chest_private",
                "piece_chest_blackmetal",
            };

            foreach (string name in candidates)
            {
                if (string.IsNullOrEmpty(name)) continue;
                GameObject prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) continue;
                Piece piece = prefab.GetComponent<Piece>();
                if (piece == null || piece.m_icon == null) continue;
                _icon = piece.m_icon;
                Main.Log.LogInfo("ThorsKist: map pin using the icon from '" + name + "'.");
                return _icon;
            }

            Main.Log.LogWarning("ThorsKist: found no chest prefab for the map pin icon; " +
                                "falling back to a plain map pin.");
            return null;
        }
    }
}
