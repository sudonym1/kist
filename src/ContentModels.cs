using System;
using System.Collections.Generic;
using UnityEngine;

namespace SharedInventory;

// Editable original model source. Metres, Y up, front at -Z; chest pivot is on
// the floor. Geometry and materials are built once, shared by every instance.
internal sealed class ContentModels : IDisposable
{
    public readonly Mesh Chest;
    public readonly Mesh Essence;
    public readonly Material[] Materials;
    public readonly Material[] DimmedMaterials;

    public ContentModels(Material source)
    {
        Materials = new[]
        {
            Material(source, "Oiled heartwood", new Color(.29f, .15f, .075f)),
            Material(source, "Forged dark iron", new Color(.085f, .105f, .12f)),
            Material(source, "Old gold carving", new Color(.67f, .43f, .14f)),
            Material(source, "Einherjar amber", new Color(1f, .54f, .09f), true)
        };
        Chest = BuildChest();
        Essence = BuildEssence();
        DimmedMaterials = (Material[])Materials.Clone();
        DimmedMaterials[3] = Material(source, "Dimmed Einherjar violet", new Color(.6f, .2f, .95f), true);
    }

    private static Material Material(Material source, string name, Color color, bool glow = false)
    {
        var material = new Material(source) { name = "Einherjar / " + name };
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", Texture2D.whiteTexture);
        if (material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", null);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Style")) material.SetFloat("_Style", 0);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .18f);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0);
        if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", glow ? color * .23f : Color.black);
        if (glow) material.EnableKeyword("_EMISSION");
        return material;
    }

    public GameObject Attach(Transform parent, Mesh mesh)
    {
        var model = new GameObject("model");
        model.transform.SetParent(parent, false);
        model.AddComponent<MeshFilter>().sharedMesh = mesh;
        model.AddComponent<MeshRenderer>().sharedMaterials = Materials;
        return model;
    }

    private static Mesh BuildChest()
    {
        var b = new Builder();
        // Deep reliquary on four feet, eight individually bevelled planks.
        b.Box(new Vector3(0, .17f, 0), new Vector3(1.84f, .16f, .94f), 1);
        for (var x = -1; x <= 1; x += 2)
        for (var z = -1; z <= 1; z += 2)
            b.Box(new Vector3(x * .73f, .09f, z * .34f), new Vector3(.22f, .18f, .22f), 1);
        for (var i = 0; i < 8; i++)
            b.Box(new Vector3(-.77f + i * .22f, .48f, 0), new Vector3(.211f, .54f, .82f), 0);
        b.Box(new Vector3(0, .73f, 0), new Vector3(1.88f, .09f, .97f), 2);
        // Angular, five-sided lid; no vanilla chest mesh is retained.
        var lid = new[] { new Vector2(-.49f, .79f), new Vector2(.49f, .79f),
            new Vector2(.49f, .92f), new Vector2(0, 1.19f), new Vector2(-.49f, .92f) };
        b.Profile(lid, -.94f, .94f, 0);
        foreach (var x in new[] { -.65f, .65f })
        {
            b.Box(new Vector3(x, .47f, -.437f), new Vector3(.10f, .54f, .06f), 1);
            b.Box(new Vector3(x, .47f, .437f), new Vector3(.10f, .54f, .06f), 1);
            b.Profile(new[] { new Vector2(-.51f, .79f), new Vector2(.51f, .79f),
                new Vector2(.51f, .94f), new Vector2(0, 1.22f), new Vector2(-.51f, .94f) }, x - .055f, x + .055f, 1);
            foreach (var z in new[] { -.474f, .474f })
                foreach (var y in new[] { .28f, .65f })
                    b.Box(new Vector3(x, y, z), new Vector3(.045f, .045f, .018f), 2);
        }
        // Tall carved end-cheeks with a stepped Nordic prow silhouette.
        var end = new[] { new Vector2(-.47f, .18f), new Vector2(.47f, .18f),
            new Vector2(.47f, .96f), new Vector2(.27f, 1.08f), new Vector2(.17f, 1.36f),
            new Vector2(0, 1.25f), new Vector2(-.17f, 1.36f), new Vector2(-.27f, 1.08f), new Vector2(-.47f, .96f) };
        b.Profile(end, -.99f, -.88f, 2);
        b.Profile(end, .88f, .99f, 2);
        // Central iron escutcheon, faceted soul-stone and the matching rune.
        b.Box(new Vector3(0, .57f, -.47f), new Vector3(.39f, .46f, .065f), 1);
        b.Crystal(new Vector3(0, .58f, -.55f), new Vector3(.13f, .23f, .065f));
        b.Rune(new Vector3(-.34f, .49f, -.448f), .12f, 2);
        b.Rune(new Vector3(.34f, .49f, -.448f), .12f, 2);
        // Thin ridge and lid edge carving catch the light from above.
        b.Box(new Vector3(0, 1.208f, 0), new Vector3(1.72f, .035f, .04f), 2);
        return b.Mesh("Einherjar reliquary v1");
    }

    private static Mesh BuildEssence()
    {
        var b = new Builder();
        b.Crystal(new Vector3(0, .23f, 0), new Vector3(.13f, .23f, .105f));
        b.Rune(new Vector3(0, .23f, -.107f), .095f, 1);
        return b.Mesh("Einherjar soul crystal v1");
    }

    public void Dispose()
    {
        UnityEngine.Object.Destroy(Chest);
        UnityEngine.Object.Destroy(Essence);
        foreach (var material in Materials) UnityEngine.Object.Destroy(material);
        UnityEngine.Object.Destroy(DimmedMaterials[3]);
    }

    private sealed class Builder
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<int>[] triangles = { new List<int>(), new List<int>(), new List<int>(), new List<int>() };

        private void Triangle(Vector3 a, Vector3 b, Vector3 c, int material)
        {
            var index = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            uv.Add(Vector2.zero); uv.Add(Vector2.right); uv.Add(Vector2.up);
            triangles[material].Add(index); triangles[material].Add(index + 1); triangles[material].Add(index + 2);
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int material)
        { Triangle(a, b, c, material); Triangle(a, c, d, material); }

        public void Box(Vector3 center, Vector3 size, int material)
        {
            var lo = center - size * .5f; var hi = center + size * .5f;
            Quad(new Vector3(lo.x, lo.y, lo.z), new Vector3(lo.x, hi.y, lo.z), new Vector3(hi.x, hi.y, lo.z), new Vector3(hi.x, lo.y, lo.z), material);
            Quad(new Vector3(hi.x, lo.y, hi.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(lo.x, hi.y, hi.z), new Vector3(lo.x, lo.y, hi.z), material);
            Quad(new Vector3(lo.x, lo.y, hi.z), new Vector3(lo.x, hi.y, hi.z), new Vector3(lo.x, hi.y, lo.z), new Vector3(lo.x, lo.y, lo.z), material);
            Quad(new Vector3(hi.x, lo.y, lo.z), new Vector3(hi.x, hi.y, lo.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(hi.x, lo.y, hi.z), material);
            Quad(new Vector3(lo.x, hi.y, lo.z), new Vector3(lo.x, hi.y, hi.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(hi.x, hi.y, lo.z), material);
            Quad(new Vector3(lo.x, lo.y, hi.z), new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, lo.y, lo.z), new Vector3(hi.x, lo.y, hi.z), material);
        }

        // Counter-clockwise Z/Y profile extruded along X. Ear clipping handles
        // the concave carved ends without intersecting triangles in the caps.
        public void Profile(Vector2[] profile, float left, float right, int material)
        {
            Vector3 P(int i, float x) => new Vector3(x, profile[i].y, profile[i].x);
            for (var i = 0; i < profile.Length; i++)
            {
                var next = (i + 1) % profile.Length;
                Quad(P(next, left), P(i, left), P(i, right), P(next, right), material);
            }
            var remaining = new List<int>();
            for (var i = 0; i < profile.Length; i++) remaining.Add(i);
            float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
            while (remaining.Count > 2)
            {
                var clipped = false;
                for (var n = 0; n < remaining.Count; n++)
                {
                    var a = remaining[(n + remaining.Count - 1) % remaining.Count];
                    var b = remaining[n]; var c = remaining[(n + 1) % remaining.Count];
                    if (Cross(profile[b] - profile[a], profile[c] - profile[b]) <= 0) continue;
                    var occupied = false;
                    foreach (var p in remaining)
                        if (p != a && p != b && p != c && Cross(profile[b] - profile[a], profile[p] - profile[a]) >= 0 &&
                            Cross(profile[c] - profile[b], profile[p] - profile[b]) >= 0 && Cross(profile[a] - profile[c], profile[p] - profile[c]) >= 0)
                            occupied = true;
                    if (occupied) continue;
                    Triangle(P(a, left), P(b, left), P(c, left), material);
                    Triangle(P(c, right), P(b, right), P(a, right), material);
                    remaining.RemoveAt(n); clipped = true; break;
                }
                if (!clipped) throw new InvalidOperationException("Invalid Einherjar mesh profile");
            }
        }

        public void Crystal(Vector3 center, Vector3 scale)
        {
            const int sides = 6;
            Vector3 Ring(int index, float y, float width)
            {
                var angle = (float)(index * Math.PI * 2 / sides + Math.PI / 6);
                return center + Vector3.Scale(new Vector3(Mathf.Cos(angle) * width, y, Mathf.Sin(angle) * width), scale);
            }
            for (var i = 0; i < sides; i++)
            {
                Triangle(center + Vector3.up * scale.y, Ring(i + 1, .35f, 1), Ring(i, .35f, 1), 3);
                Quad(Ring(i, .35f, 1), Ring(i + 1, .35f, 1), Ring(i + 1, -.48f, .72f), Ring(i, -.48f, .72f), 3);
                Triangle(center - Vector3.up * scale.y, Ring(i, -.48f, .72f), Ring(i + 1, -.48f, .72f), 3);
            }
        }

        public void Rune(Vector3 center, float size, int material)
        {
            Box(center, new Vector3(size * .15f, size * 1.55f, .012f), material);
            // Two branching strokes of a stylized soul rune, cut as real geometry.
            foreach (var direction in new[] { -1f, 1f })
            {
                var a = center + new Vector3(0, size * .15f, -.008f);
                var b = center + new Vector3(size * .55f * direction, size * .62f, -.008f);
                var width = new Vector3(size * .08f, -size * .08f * direction, 0);
                Quad(a - width, b - width, b + width, a + width, material);
                Quad(a + width, b + width, b - width, a - width, material);
            }
        }

        public Mesh Mesh(string name)
        {
            var mesh = new Mesh { name = name, subMeshCount = triangles.Length };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv);
            var colors = new Color[vertices.Count];
            for (var i = 0; i < colors.Length; i++) colors[i] = new Color(0, 0, 0, 1);
            mesh.colors = colors;
            for (var i = 0; i < triangles.Length; i++) mesh.SetTriangles(triangles[i], i);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }
    }
}
