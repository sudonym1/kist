using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// Feedback for putting things in and taking things out.
    ///
    /// These are the game's own effect prefabs, spawned the way SmarterContainers
    /// spawns them -- ZNetScene.GetPrefab + Instantiate at a world position. A
    /// synthesised chime never sounded like it belonged in Valheim; a prefab the
    /// game already ships does, and it inherits the player's audio settings,
    /// reverb zones and occlusion for free.
    ///
    /// Prefab names are config, not constants, so a different sound does not
    /// need a new build.
    /// </summary>
    public static class KistFx
    {
        private static bool _warnedSfx;
        private static bool _warnedVfx;

        /// <summary>Item left the pack for the Kist.</summary>
        public static void Deposit()
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;

            if (Main.DepositSound.Value)
                Spawn(Main.DepositSfxPrefab.Value, p.GetCenterPoint(), ref _warnedSfx);

            if (Main.DepositVfx.Value)
                Spawn(Main.DepositVfxPrefab.Value, p.GetCenterPoint(), ref _warnedVfx);
        }

        /// <summary>Item arrived from the Kist.</summary>
        public static void Take()
        {
            if (!Main.TakeSound.Value) return;
            Player p = Player.m_localPlayer;
            if (p == null) return;

            // Vanilla's own pickup feedback, straight off the Player. Using the
            // effect list rather than a prefab name means this is exactly what
            // picking an item off the ground sounds like, and it cannot go
            // missing the way a hard-coded prefab name can.
            if (p.m_pickupEffects != null && p.m_pickupEffects.HasEffects())
                p.m_pickupEffects.Create(p.transform.position, Quaternion.identity);
        }

        private static void Spawn(string prefabName, Vector3 pos, ref bool warned)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            if (ZNetScene.instance == null) return;

            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                // Once, not once per deposit: a bad prefab name in the config
                // would otherwise fill the log.
                if (!warned)
                {
                    warned = true;
                    Main.Log.LogWarning("ThorsKist: no prefab named '" + prefabName +
                                        "'. Check the Feedback section of the config.");
                }
                return;
            }
            Object.Instantiate(prefab, pos, Quaternion.identity);
        }
    }
}
