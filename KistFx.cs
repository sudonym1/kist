using System.Collections;
using UnityEngine;

namespace ThorsKist
{
    /// <summary>
    /// Feedback for putting things in, taking things out, and planting a Kist.
    ///
    /// These are the game's own effect prefabs, spawned the way SmarterContainers
    /// spawns them -- ZNetScene.GetPrefab + Instantiate at a world position. A
    /// synthesised chime never sounded like it belonged in Valheim; a prefab the
    /// game already ships does, and it inherits the player's audio settings,
    /// reverb zones and occlusion for free.
    ///
    /// Prefab names are config, not constants, so a different sound or colour
    /// does not need a new build. Every default below was checked against the
    /// game's own prefab manifest, not guessed.
    /// </summary>
    public static class KistFx
    {
        private static bool _warnedDepositSfx;
        private static bool _warnedDepositVfx;
        private static bool _warnedAnchorSfx;
        private static bool _warnedAnchorVfx;
        private static bool _warnedAnchorFall;

        // ------------------------------------------------------------------
        // Deposit
        // ------------------------------------------------------------------

        /// <summary>The local player stored something.</summary>
        public static void Deposit()
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;
            DepositAt(p.GetCenterPoint());
        }

        /// <summary>
        /// Someone stored something, at a world position. Effect prefabs carry no
        /// ZNetView, so nothing about them replicates on its own -- the server
        /// tells every member where to draw it.
        /// </summary>
        public static void DepositAt(Vector3 pos)
        {
            if (Main.DepositSound.Value)
                Spawn(Main.DepositSfxPrefab.Value, pos, Main.DepositVolume.Value, ref _warnedDepositSfx);

            if (Main.DepositVfx.Value)
                Spawn(Main.DepositVfxPrefab.Value, pos, 1f, ref _warnedDepositVfx);
        }

        // ------------------------------------------------------------------
        // Take
        // ------------------------------------------------------------------

        public static void Take()
        {
            if (!Main.TakeSound.Value) return;
            Player p = Player.m_localPlayer;
            if (p == null) return;

            // Vanilla's own pickup feedback, straight off the Player. Using the
            // effect list rather than a prefab name means this is exactly what
            // picking an item off the ground sounds like, and it cannot go
            // missing the way a hard-coded prefab name can.
            if (p.m_pickupEffects == null || !p.m_pickupEffects.HasEffects()) return;

            GameObject[] spawned = p.m_pickupEffects.Create(p.transform.position, Quaternion.identity);
            Amplify(spawned, Main.TakeVolume.Value);
        }

        // ------------------------------------------------------------------
        // Anchor
        // ------------------------------------------------------------------

        /// <summary>
        /// Plants the Kist: something blue falls out of the sky and hits the
        /// ground. Built as two beats rather than one prefab, because no single
        /// vanilla effect reads as "this arrived from above".
        /// </summary>
        public static void Anchor(Vector3 groundPos)
        {
            if (!Main.AnchorEffects.Value) return;
            if (Main.Instance == null) return;
            Main.Instance.StartCoroutine(Slam(groundPos));
        }

        private static IEnumerator Slam(Vector3 groundPos)
        {
            float height = Mathf.Max(1f, Main.AnchorDropHeight.Value);
            float fall = Mathf.Max(0.05f, Main.AnchorDropSeconds.Value);

            GameObject falling = Spawn(Main.AnchorFallVfxPrefab.Value, groundPos + Vector3.up * height,
                                       1f, ref _warnedAnchorFall);

            float t = 0f;
            while (t < fall)
            {
                t += Time.deltaTime;
                if (falling != null)
                {
                    // Accelerating, so it reads as falling rather than gliding.
                    float k = Mathf.Clamp01(t / fall);
                    falling.transform.position = Vector3.Lerp(groundPos + Vector3.up * height, groundPos, k * k);
                }
                yield return null;
            }
            if (falling != null) Object.Destroy(falling);

            Spawn(Main.AnchorVfxPrefab.Value, groundPos, 1f, ref _warnedAnchorVfx);
            Spawn(Main.AnchorSfxPrefab.Value, groundPos, Main.AnchorVolume.Value, ref _warnedAnchorSfx);
        }

        // ------------------------------------------------------------------
        // Plumbing
        // ------------------------------------------------------------------

        private static GameObject Spawn(string prefabName, Vector3 pos, float volume, ref bool warned)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            if (ZNetScene.instance == null) return null;

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
                return null;
            }

            GameObject go = Object.Instantiate(prefab, pos, Quaternion.identity);
            Amplify(go, volume);
            return go;
        }

        private static void Amplify(GameObject[] objects, float volume)
        {
            if (objects == null) return;
            foreach (GameObject go in objects) Amplify(go, volume);
        }

        /// <summary>
        /// Turns an effect up.
        ///
        /// Setting AudioSource.volume does NOT work: ZSFX recomputes it every
        /// frame from its own fields, so a direct assignment survives for one
        /// frame and is then silently overwritten. SetVolumeModifier is a factor
        /// inside that same calculation, which is why it sticks.
        /// </summary>
        private static void Amplify(GameObject go, float volume)
        {
            if (go == null || Mathf.Approximately(volume, 1f)) return;
            foreach (ZSFX sfx in go.GetComponentsInChildren<ZSFX>(true))
                sfx.SetVolumeModifier(sfx.GetVolumeModifier() * volume);
        }
    }
}
