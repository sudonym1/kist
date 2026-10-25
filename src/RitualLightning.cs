using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace SharedInventory;

// Reuse the installed Obliterator's particles, lights and ZSFX/audio clips.
// Prepare under an inactive parent: damage/network scripts never get Awake or
// OnEnable, and are removed before any instance can run. No game assets ship.
internal static class RitualLightning
{
    private static GameObject? library, visual;
    internal static int Played { get; private set; }
    internal static GameObject? Template => visual;
    public static bool Ready
    {
        get
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return true;
            if (visual != null) return true;
            if (ZNetScene.instance == null) return false;
            // Prefab-library lookup once, never a scan of world entities.
            var incinerator = ZNetScene.instance.m_prefabs.Select(p => p.GetComponent<Incinerator>()).FirstOrDefault(i => i != null);
            if (incinerator == null || incinerator.m_lightingAOEs == null) return false;
            library = new GameObject("Einherjar cosmetic effect library");
            library.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(library);
            visual = UnityEngine.Object.Instantiate(incinerator.m_lightingAOEs, library.transform);
            visual.name = "Einherjar Obliterator lightning";
            foreach (var component in visual.GetComponentsInChildren<Component>(true))
            {
                // Whitelist the original presentation/lifetime scripts. New
                // gameplay scripts in future game prefabs cannot leak through.
                if (component is Collider || component is Rigidbody ||
                    (component is MonoBehaviour && component is not ZSFX && component is not TimedDestruction &&
                     component is not LightFlicker && component is not CamShaker))
                    UnityEngine.Object.DestroyImmediate(component);
            }
            foreach (var sound in visual.GetComponentsInChildren<ZSFX>(true)) sound.m_maxConcurrentSources = 0;
            visual.SetActive(true); // Parent remains inactive; no scripts execute.
            return true;
        }
    }
    public static void Play(Vector3 position)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        if (!Ready) throw new InvalidOperationException("Obliterator lightning effects are unavailable.");
        var instance = UnityEngine.Object.Instantiate(visual!, position, Quaternion.identity);
        instance.name = "Einherjar ritual lightning";
        Played++;
        UnityEngine.Object.Destroy(instance, 10); // Bounded fallback to original lifetime scripts.
    }
    public static void Dispose()
    {
        if (library != null) UnityEngine.Object.Destroy(library);
        visual = null; library = null;
    }
}
