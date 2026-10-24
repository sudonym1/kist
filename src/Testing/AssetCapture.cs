using System;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Rendering;

namespace SharedInventory;

// Development-only, enabled only by the existing isolated harness guard and an
// explicit launch argument. Release builds contain no camera/capture workflow.
internal static class AssetCapture
{
    private static bool captured;
    public static void Update()
    {
        if ((captured && !Input.GetKeyDown(KeyCode.F6)) || Content.Models == null || Content.Chest == null || Content.Essence == null ||
            Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-capture-assets") < 0) return;
        captured = true;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        var path = Path.Combine(Directory.GetParent(Paths.GameRootPath)!.FullName, "asset-capture");
        Directory.CreateDirectory(path);
        Capture(Content.Models.Chest, "chest", path);
        Capture(Content.Models.Essence, "essence", path);
        Debug.Log("EINHERJAR ASSET CAPTURE: " + path);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-einherjar-exit-after-capture") >= 0) Application.Quit();
    }

    private static void Capture(Mesh mesh, string name, string path)
    {
        var stage = new GameObject("Einherjar icon stage");
        var cameraObject = new GameObject("Einherjar icon camera");
        var keyObject = new GameObject("Einherjar icon key");
        var fillObject = new GameObject("Einherjar icon fill");
        var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        var target = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 4);
        var previous = RenderTexture.active;
        var ambient = RenderSettings.ambientLight;
        var ambientMode = RenderSettings.ambientMode;
        var fog = RenderSettings.fog;
        try
        {
            // Keep ordinary world renderers/lights out of the icon camera.
            const int layer = 31;
            stage.layer = layer;
            stage.transform.position = Vector3.zero;
            var model = Content.Models!.Attach(stage.transform, mesh);
            model.layer = layer;
            var bounds = mesh.bounds;
            var center = stage.transform.position + bounds.center;
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.orthographic = true;
            camera.orthographicSize = bounds.size.magnitude * .47f;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 50;
            camera.cullingMask = 1 << layer;
            camera.transform.position = center + new Vector3(1.55f, 1.1f, -2.2f).normalized * 8;
            camera.transform.LookAt(center);
            camera.targetTexture = target;
            var key = keyObject.AddComponent<Light>();
            key.type = LightType.Directional;
            key.cullingMask = 1 << layer;
            key.intensity = 1.25f;
            key.color = new Color(1, .92f, .78f);
            key.transform.rotation = Quaternion.Euler(45, 150, 0);
            var fill = fillObject.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.cullingMask = 1 << layer;
            fill.intensity = .6f;
            fill.color = new Color(.65f, .78f, 1);
            fill.transform.rotation = Quaternion.Euler(20, -45, 0);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.65f, .65f, .65f);
            RenderSettings.fog = false;
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
            texture.Apply();
            var black = texture.GetPixels();
            // Valheim's opaque piece shader writes zero alpha. Reconstruct
            // coverage from two renders instead of trusting that framebuffer
            // channel, retaining transparent background and antialiased edges.
            camera.backgroundColor = Color.white;
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
            texture.Apply();
            var white = texture.GetPixels();
            for (var i = 0; i < black.Length; i++)
            {
                var difference = white[i] - black[i];
                var alpha = Mathf.Clamp01(1 - Mathf.Max(difference.r, Mathf.Max(difference.g, difference.b)));
                black[i] = alpha < .005f ? Color.clear : new Color(black[i].r / alpha, black[i].g / alpha, black[i].b / alpha, alpha);
            }
            texture.SetPixels(black);
            texture.Apply();
            File.WriteAllBytes(Path.Combine(path, name + ".png"), ImageConversion.EncodeToPNG(texture));
        }
        finally
        {
            stage.SetActive(false);
            RenderTexture.active = previous;
            RenderSettings.ambientLight = ambient;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.fog = fog;
            UnityEngine.Object.Destroy(stage); UnityEngine.Object.Destroy(cameraObject);
            UnityEngine.Object.Destroy(keyObject); UnityEngine.Object.Destroy(fillObject);
            UnityEngine.Object.Destroy(texture);
            RenderTexture.ReleaseTemporary(target);
        }
    }
}
