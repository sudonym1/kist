using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ThorsKist
{
    /// <summary>
    /// The deposit flourish: a chime and a spray of light streaks leaving the
    /// slot the item sat in.
    ///
    /// Both are built at runtime rather than shipped in an asset bundle, so the
    /// mod stays a single DLL and cannot desync from a bundle built against a
    /// different Unity version.
    /// </summary>
    public static class DepositFx
    {
        private static AudioSource _source;
        private static AudioClip _chime;
        private static Sprite _pixel;
        private static Canvas _canvas;
        private static MonoBehaviour _runner;

        // ------------------------------------------------------------------
        // Public entry
        // ------------------------------------------------------------------

        public static void Play()
        {
            if (Main.DepositSound.Value) PlayChime();
            if (Main.DepositVfx.Value) PlayStreaks();
        }

        // ------------------------------------------------------------------
        // Sound
        // ------------------------------------------------------------------

        private static void PlayChime()
        {
            EnsureAudio();
            if (_source == null || _chime == null) return;
            // Slight pitch jitter so repeated deposits do not machine-gun.
            _source.pitch = Random.Range(0.97f, 1.05f);
            _source.PlayOneShot(_chime, Mathf.Clamp01(Main.DepositVolume.Value));
        }

        private static void EnsureAudio()
        {
            if (_source != null && _chime != null) return;

            if (_source == null)
            {
                GameObject go = new GameObject("ThorsKist_Audio");
                Object.DontDestroyOnLoad(go);
                _source = go.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.spatialBlend = 0f;   // it happens in the UI, not the world
                _source.bypassEffects = true;
                _source.bypassReverbZones = true;
                _source.ignoreListenerPause = true;
                _source.ignoreListenerVolume = false;
            }
            if (_chime == null) _chime = BuildChime();
        }

        /// <summary>
        /// A struck-bell arpeggio. Bells are inharmonic, so the partials are
        /// deliberately not whole multiples of the root -- that ratio set is
        /// what makes it read as "mystic" rather than as a sine beep.
        /// </summary>
        private static AudioClip BuildChime()
        {
            const int rate = 44100;
            const float length = 1.15f;
            int samples = (int)(rate * length);
            float[] data = new float[samples];

            // Three notes of a rising minor arpeggio, each struck a beat later.
            float[] roots = { 587.33f, 880.00f, 1174.66f };   // D5, A5, D6
            float[] starts = { 0f, 0.075f, 0.15f };
            float[] gains = { 0.55f, 0.45f, 0.40f };

            // Bell partial ratios and how fast each one dies away.
            float[] partials = { 1.00f, 2.02f, 2.99f, 4.21f, 5.43f, 6.79f };
            float[] partialGain = { 1.00f, 0.52f, 0.34f, 0.20f, 0.12f, 0.07f };
            float[] partialDecay = { 2.6f, 3.4f, 4.2f, 5.6f, 7.0f, 8.8f };

            for (int n = 0; n < roots.Length; n++)
            {
                int offset = (int)(starts[n] * rate);
                for (int i = offset; i < samples; i++)
                {
                    float t = (i - offset) / (float)rate;
                    float v = 0f;
                    for (int p = 0; p < partials.Length; p++)
                    {
                        float f = roots[n] * partials[p];
                        // A touch of detune gives the shimmer a beat frequency.
                        float a = Mathf.Sin(2f * Mathf.PI * f * t);
                        float b = Mathf.Sin(2f * Mathf.PI * (f * 1.004f) * t);
                        v += (a + b) * 0.5f * partialGain[p] * Mathf.Exp(-partialDecay[p] * t);
                    }
                    // 4 ms strike so it starts without a click.
                    float attack = Mathf.Clamp01(t / 0.004f);
                    data[i] += v * gains[n] * attack * 0.16f;
                }
            }

            // A breath of filtered noise underneath: the "whoosh" of the item
            // being drawn away.
            float lp = 0f;
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)rate;
                float noise = Random.Range(-1f, 1f);
                lp += (noise - lp) * 0.06f;
                float env = Mathf.Exp(-7f * t) * Mathf.Clamp01(t / 0.01f);
                data[i] += lp * env * 0.12f;
            }

            // Normalise, then leave headroom so PlayOneShot volume is the only
            // thing that decides how loud this is.
            float peak = 0f;
            for (int i = 0; i < samples; i++)
            {
                float a = data[i] < 0f ? -data[i] : data[i];
                if (a > peak) peak = a;
            }
            if (peak > 0.0001f)
            {
                float scale = 0.9f / peak;
                for (int i = 0; i < samples; i++) data[i] *= scale;
            }

            // Filled through a reader callback rather than SetData: SetData's
            // only remaining overload takes a Span, which a net472 build cannot
            // reference against the game's mscorlib.
            _chimeData = data;
            _chimeRead = 0;
            return AudioClip.Create("ThorsKist_Chime", samples, 1, rate, false,
                ReadChimePcm, SeekChimePcm);
        }

        private static float[] _chimeData;
        private static int _chimeRead;

        private static void ReadChimePcm(float[] into)
        {
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = (_chimeData != null && _chimeRead < _chimeData.Length) ? _chimeData[_chimeRead] : 0f;
                _chimeRead++;
            }
        }

        private static void SeekChimePcm(int position)
        {
            _chimeRead = position;
        }

        // ------------------------------------------------------------------
        // Streaks
        // ------------------------------------------------------------------

        private static void PlayStreaks()
        {
            EnsureCanvas();
            if (_canvas == null || _runner == null) return;

            // The cursor is where the player just clicked, so it is both the
            // truest origin for the gesture and the only one reachable without
            // InventoryGrid.GetElement / m_width, which are private on the real
            // game assembly.
            Vector3 from = Input.mousePosition;

            RectTransform canvasRect = _canvas.transform as RectTransform;
            // The streaks converge on the top edge of the screen: the Kist is
            // "somewhere else", and there is no on-screen object to fly to.
            Vector3 to = canvasRect.TransformPoint(new Vector3(0f, canvasRect.rect.height * 0.5f + 40f, 0f));

            _runner.StartCoroutine(StreakBurst(from, to));
        }

        private static IEnumerator StreakBurst(Vector3 from, Vector3 to)
        {
            const int count = 16;
            List<GameObject> spawned = new List<GameObject>();

            GameObject flash = NewImage("tk_flash", new Color(0.72f, 0.88f, 1f, 0.9f));
            flash.transform.position = from;
            RectTransform flashRt = flash.transform as RectTransform;
            flashRt.sizeDelta = new Vector2(70f, 70f);
            spawned.Add(flash);

            for (int i = 0; i < count; i++)
            {
                GameObject go = NewImage("tk_streak", StreakColour(i));
                go.transform.position = from;
                RectTransform rt = go.transform as RectTransform;
                rt.sizeDelta = new Vector2(Random.Range(26f, 64f), Random.Range(1.5f, 3.5f));
                spawned.Add(go);
            }

            float duration = 0.5f;
            float t = 0f;
            // Each streak takes its own bowed path, so the burst reads as a
            // spray rather than sixteen copies of one line.
            Vector3[] control = new Vector3[count];
            float[] delay = new float[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 mid = Vector3.Lerp(from, to, 0.5f);
                control[i] = mid + new Vector3(Random.Range(-260f, 260f), Random.Range(-90f, 140f), 0f);
                delay[i] = Random.Range(0f, 0.13f);
            }

            while (t < duration + 0.2f)
            {
                t += Time.unscaledDeltaTime;

                float ft = Mathf.Clamp01(t / 0.22f);
                flashRt.sizeDelta = new Vector2(70f + 90f * ft, 70f + 90f * ft);
                SetAlpha(flash, (1f - ft) * 0.85f);

                for (int i = 0; i < count; i++)
                {
                    GameObject go = spawned[i + 1];
                    float lt = Mathf.Clamp01((t - delay[i]) / duration);
                    float e = 1f - Mathf.Pow(1f - lt, 3f);           // ease out
                    Vector3 prev = Bezier(from, control[i], to, Mathf.Max(0f, e - 0.05f));
                    Vector3 pos = Bezier(from, control[i], to, e);
                    go.transform.position = pos;

                    Vector3 dir = pos - prev;
                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                        go.transform.rotation = Quaternion.Euler(0f, 0f, ang);
                    }
                    SetAlpha(go, lt < 0.15f ? lt / 0.15f : 1f - Mathf.Pow(lt, 2f));
                }
                yield return null;
            }

            foreach (GameObject go in spawned)
                if (go != null) Object.Destroy(go);
        }

        private static Color StreakColour(int i)
        {
            // Cold blue-white with the odd gold spark.
            if (i % 5 == 0) return new Color(1f, 0.86f, 0.55f, 1f);
            float b = Random.Range(0.85f, 1f);
            return new Color(0.68f * b, 0.85f * b, 1f * b, 1f);
        }

        private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * b + t * t * c;
        }

        private static void SetAlpha(GameObject go, float a)
        {
            Image img = go.GetComponent<Image>();
            if (img == null) return;
            Color c = img.color;
            c.a = Mathf.Clamp01(a);
            img.color = c;
        }

        private static GameObject NewImage(string name, Color colour)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(_canvas.transform, false);
            Image img = go.GetComponent<Image>();
            img.sprite = Pixel();
            img.color = colour;
            img.raycastTarget = false;
            return go;
        }

        private static Sprite Pixel()
        {
            if (_pixel != null) return _pixel;
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _pixel = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            _pixel.hideFlags = HideFlags.HideAndDontSave;
            return _pixel;
        }

        private static void EnsureCanvas()
        {
            if (_canvas != null && _runner != null) return;
            if (InventoryGui.instance == null) return;

            Canvas c = InventoryGui.instance.GetComponentInParent<Canvas>();
            if (c == null) return;
            _canvas = c.rootCanvas != null ? c.rootCanvas : c;
            _runner = Main.Instance;
        }
    }
}
