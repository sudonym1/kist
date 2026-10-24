using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace SharedInventory;

// One local preview for the hovered chest or hammer ghost, never one Update
// per placed chest. Three great circles describe the actual 3D access sphere.
internal sealed class ChestRadiusVisual : IDisposable
{
    private static readonly AccessTools.FieldRef<Player, GameObject> ghost =
        AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
    private GameObject? root;
    private Material? material;
    private LineRenderer[]? rings;
    internal bool Visible => root != null && root.activeSelf;

    internal void Update()
    {
        var player = Player.m_localPlayer;
        Transform? target = null;
        if (player != null && !player.IsDead())
        {
            var placement = ghost(player);
            if (placement != null && placement.activeInHierarchy && placement.GetComponent<SharedChest>() != null)
                target = placement.transform;
            else
                target = player.GetHoverObject()?.GetComponentInParent<SharedChest>()?.transform;
        }
        if (target == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        { root?.SetActive(false); return; }
        if (root == null) Create();
        if (root == null) return;
        root.SetActive(true);
        root.transform.position = target.position;
    }

    private void Create()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) return;
        material = new Material(shader) { name = "Einherjar radius" };
        root = new GameObject("Einherjar access sphere");
        rings = new LineRenderer[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var child = new GameObject("Radius " + axis);
            child.transform.SetParent(root.transform, false);
            var line = rings[axis] = child.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = false; line.loop = true; line.positionCount = 96;
            line.startWidth = line.endWidth = .12f;
            line.startColor = line.endColor = new Color(1, .65f, .17f, .65f);
            line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
            for (var i = 0; i < line.positionCount; i++)
            {
                var angle = i * Mathf.PI * 2 / line.positionCount;
                var a = Mathf.Cos(angle) * ChestAccess.Radius;
                var b = Mathf.Sin(angle) * ChestAccess.Radius;
                line.SetPosition(i, axis == 0 ? new Vector3(a, 0, b) :
                    axis == 1 ? new Vector3(a, b, 0) : new Vector3(0, a, b));
            }
        }
    }

    public void Dispose()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        if (material != null) UnityEngine.Object.Destroy(material);
        root = null; material = null; rings = null;
    }
}
