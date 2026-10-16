using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace QuestKit
{
    // Packs Toon Standard materials that share their settings into one texture, so each mesh draws
    // with a single material.
    public static class Atlas
    {
        public const int MaxSize = 2048;
        const int Padding = 4;

        public class Group
        {
            public string key;
            public List<MatEntry> materials = new List<MatEntry>();
            public float scale = 1f;
            public int side;
            public bool emissive;
        }

        public static string GroupKey(MatEntry e)
        {
            var m = e.src;
            float cull = m.HasProperty("_Cull") ? m.GetFloat("_Cull") : 2f;
            return e.mode + "|" + QuestBuilder.RampFor(m) + "|" + cull;
        }

        public static bool Eligible(MatEntry e, out string why)
        {
            why = null;
            if (e.mode != MatMode.ToonStandard && e.mode != MatMode.ToonStandardOutline && e.mode != MatMode.ToonLit) { why = "not Toon Standard or Toon Lit"; return false; }
            if (e.mainTex == null) { why = "no texture"; return false; }
            if (!e.uvInRange) { why = "its UVs repeat the texture"; return false; }
            if (e.hasExtraMaps) { why = "uses a normal map or matcap mask"; return false; }
            return true;
        }

        public static List<Group> Plan(QuestPlan plan)
        {
            var groups = new Dictionary<string, Group>();
            foreach (var e in plan.materials)
            {
                string why;
                if (!plan.atlas || !Eligible(e, out why)) continue;
                var k = GroupKey(e);
                Group g;
                if (!groups.TryGetValue(k, out g)) groups[k] = g = new Group { key = k };
                g.materials.Add(e);
                g.emissive |= e.emissive;
            }
            var result = groups.Values.Where(g => g.materials.Count >= 2).ToList();
            foreach (var g in result)
            {
                float area = 0;
                foreach (var e in g.materials)
                {
                    var t = plan.Tex(e.mainTex);
                    float s = t != null ? t.size + Padding * 2 : 256;
                    area += s * s * (t != null ? t.Aspect : 1f);
                }
                g.scale = Mathf.Min(1f, Mathf.Sqrt(MaxSize * (float)MaxSize * 0.9f / area));
                g.side = Mathf.Min(MaxSize, Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(area / 0.9f))));
            }
            return result;
        }

        // Builds the atlas textures and material for a group. Returns the UV rect of each material.
        public static Dictionary<Material, Rect> Build(QuestPlan plan, Group g, string folder, out Material atlasMat, Material template)
        {
            var mains = new List<Texture2D>();
            var emissions = new List<Texture2D>();
            float emissionScale = 1f;
            foreach (var e in g.materials)
            {
                var c = QuestBuilder.EmissionColorFor(e.src);
                emissionScale = Mathf.Max(emissionScale, c.r, c.g, c.b);
            }

            foreach (var e in g.materials)
            {
                var t = plan.Tex(e.mainTex);
                int size = Mathf.Max(32, Mathf.RoundToInt((t != null ? t.size : 256) * g.scale));
                int w, h;
                var px = TextureUtil.Pixels(e.MainOrBaked, size, out w, out h);
                Color tint = e.src.HasProperty("_Color") ? e.src.GetColor("_Color") : Color.white;
                for (int i = 0; i < px.Length; i++) px[i] = new Color(px[i].r * tint.r, px[i].g * tint.g, px[i].b * tint.b, 1);
                // Toon Lit has no emission, so glow is painted into the colour instead.
                if (e.emissive && (template.shader.name == "VRChat/Mobile/Toon Lit" || plan.bakeGlowIntoAtlas))
                {
                    var glow = QuestBuilder.EmissionColorFor(e.src);
                    int gw, gh;
                    var gp = TextureUtil.Pixels(e.emissionTex != null ? e.emissionTex : e.mainTex, Mathf.Max(w, h), out gw, out gh);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            var gs = gp[Mathf.Min(gh - 1, y * gh / h) * gw + Mathf.Min(gw - 1, x * gw / w)];
                            var c = px[y * w + x];
                            px[y * w + x] = new Color(Mathf.Clamp01(c.r + gs.r * glow.r * 0.5f), Mathf.Clamp01(c.g + gs.g * glow.g * 0.5f), Mathf.Clamp01(c.b + gs.b * glow.b * 0.5f), 1);
                        }
                }
                mains.Add(Make(px, w, h));

                var ep = new Color[w * h];
                if (e.emissive)
                {
                    var glow = QuestBuilder.EmissionColorFor(e.src) / emissionScale;
                    int ew, eh;
                    var src = TextureUtil.Pixels(e.emissionTex != null ? e.emissionTex : e.mainTex, Mathf.Max(w, h), out ew, out eh);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            var s = src[Mathf.Min(eh - 1, y * eh / h) * ew + Mathf.Min(ew - 1, x * ew / w)];
                            ep[y * w + x] = new Color(s.r * glow.r, s.g * glow.g, s.b * glow.b, 1);
                        }
                }
                else for (int i = 0; i < ep.Length; i++) ep[i] = Color.black;
                emissions.Add(Make(ep, w, h));
            }

            var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            var rects = atlas.PackTextures(mains.ToArray(), Padding, MaxSize, false);
            string name = "Atlas_" + plan.avatar.name + "_" + Mathf.Abs(g.key.GetHashCode());
            var mainAtlas = TextureUtil.WritePng(atlas.GetPixels(), atlas.width, atlas.height, folder + "/" + QuestBuilder.Safe(name) + ".png");
            TextureUtil.SetAndroidSize(mainAtlas, Mathf.NextPowerOfTwo(Mathf.Max(atlas.width, atlas.height)));

            Texture2D emissionAtlas = null;
            if (g.emissive && template.shader.name != "VRChat/Mobile/Toon Lit" && !plan.bakeGlowIntoAtlas)
            {
                var ea = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false, false);
                var black = new Color[atlas.width * atlas.height];
                for (int i = 0; i < black.Length; i++) black[i] = Color.black;
                ea.SetPixels(black);
                for (int i = 0; i < emissions.Count; i++)
                {
                    var r = rects[i];
                    int x0 = Mathf.RoundToInt(r.x * atlas.width), y0 = Mathf.RoundToInt(r.y * atlas.height);
                    int rw = Mathf.RoundToInt(r.width * atlas.width), rh = Mathf.RoundToInt(r.height * atlas.height);
                    var src = emissions[i];
                    for (int y = 0; y < rh; y++)
                        for (int x = 0; x < rw; x++)
                            ea.SetPixel(x0 + x, y0 + y, src.GetPixel(x * src.width / Mathf.Max(1, rw), y * src.height / Mathf.Max(1, rh)));
                }
                ea.Apply();
                emissionAtlas = TextureUtil.WritePng(ea.GetPixels(), ea.width, ea.height, folder + "/" + QuestBuilder.Safe(name) + "_Emission.png");
                // Glow is soft by nature; half resolution saves a quarter of the atlas memory.
                TextureUtil.SetAndroidSize(emissionAtlas, Mathf.Max(256, Mathf.NextPowerOfTwo(Mathf.Max(atlas.width, atlas.height)) / 2));
                Object.DestroyImmediate(ea);
            }

            atlasMat = new Material(template);
            atlasMat.DisableKeyword("USE_NORMAL_MAPS");
            atlasMat.name = name + "_x" + g.materials.Count;
            atlasMat.SetTexture("_MainTex", mainAtlas);
            atlasMat.SetColor("_Color", Color.white);
            if (emissionAtlas != null)
            {
                atlasMat.SetTexture("_EmissionMap", emissionAtlas);
                atlasMat.SetColor("_EmissionColor", new Color(emissionScale, emissionScale, emissionScale, 1));
                atlasMat.SetFloat("_EmissionStrength", 1f);
            }
            else atlasMat.SetColor("_EmissionColor", Color.black);
            var path = folder + "/" + QuestBuilder.Safe(atlasMat.name) + ".mat";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(atlasMat, path);

            var result = new Dictionary<Material, Rect>();
            for (int i = 0; i < g.materials.Count; i++) result[g.materials[i].src] = rects[i];
            foreach (var t in mains.Concat(emissions)) Object.DestroyImmediate(t);
            Object.DestroyImmediate(atlas);
            return result;
        }

        static Texture2D Make(Color[] px, int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        // Moves each atlased submesh's UVs into its rect, duplicating vertices shared between
        // submeshes that land in different rects, then joins submeshes that now share a material.
        public static Mesh Remap(Mesh mesh, Material[] srcMats, Dictionary<Material, Rect> rects, Dictionary<Material, Material> finalMats, out Material[] newMats)
        {
            var d = new MeshData(mesh);
            var owner = new int[d.Count];
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;
            var rectList = new List<Rect>();
            var subIndices = new List<int[]>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var idx = mesh.GetIndices(s);
                var m = s < srcMats.Length ? srcMats[s] : null;
                Rect r;
                if (m != null && rects.TryGetValue(m, out r))
                {
                    int ri = rectList.IndexOf(r);
                    if (ri < 0) { rectList.Add(r); ri = rectList.Count - 1; }
                    var moved = new Dictionary<int, int>();
                    for (int i = 0; i < idx.Length; i++)
                    {
                        int v = idx[i];
                        int nv;
                        if (moved.TryGetValue(v, out nv)) { idx[i] = nv; continue; }
                        if (owner[v] == -1) { owner[v] = ri; nv = v; }
                        else if (owner[v] == ri) nv = v;
                        else { nv = d.Clone(v); }
                        if (nv >= owner.Length) System.Array.Resize(ref owner, nv + 1);
                        owner[nv] = ri;
                        moved[v] = nv;
                        idx[i] = nv;
                    }
                }
                subIndices.Add(idx);
            }
            for (int v = 0; v < d.Count; v++)
            {
                if (v >= owner.Length || owner[v] < 0) continue;
                var r = rectList[owner[v]];
                var uv = d.uv[0][v];
                d.uv[0][v] = new Vector4(r.x + uv.x * r.width, r.y + uv.y * r.height, uv.z, uv.w);
            }

            var finals = new List<Material>();
            var merged = new List<List<int>>();
            var topo = new List<MeshTopology>();
            for (int s = 0; s < subIndices.Count; s++)
            {
                var m = s < srcMats.Length ? srcMats[s] : null;
                var fm = m != null && finalMats.ContainsKey(m) ? finalMats[m] : m;
                int slot = finals.IndexOf(fm);
                if (slot < 0 || fm == null) { finals.Add(fm); merged.Add(new List<int>()); topo.Add(mesh.GetTopology(s)); slot = finals.Count - 1; }
                merged[slot].AddRange(subIndices[s]);
            }
            newMats = finals.ToArray();
            return d.Build(mesh.name, merged, topo);
        }
    }

    // Full copy of a mesh's per-vertex data, so vertices can be duplicated and the mesh rebuilt.
    public class MeshData
    {
        public List<Vector3> pos = new List<Vector3>(), nrm = new List<Vector3>();
        public List<Vector4> tan = new List<Vector4>();
        public List<Color> col = new List<Color>();
        public List<Vector4>[] uv = new List<Vector4>[4];
        public List<BoneWeight> bw = new List<BoneWeight>();
        readonly Matrix4x4[] binds;
        readonly Bounds bounds;
        readonly List<Shape> shapes = new List<Shape>();

        class Shape
        {
            public string name;
            public List<float> weights = new List<float>();
            public List<List<Vector3>> dv = new List<List<Vector3>>(), dn = new List<List<Vector3>>(), dt = new List<List<Vector3>>();
            public bool hasNormals;
        }

        public int Count { get { return pos.Count; } }

        public MeshData(Mesh m)
        {
            m.GetVertices(pos);
            m.GetNormals(nrm);
            m.GetTangents(tan);
            m.GetColors(col);
            for (int c = 0; c < 4; c++)
            {
                uv[c] = new List<Vector4>();
                m.GetUVs(c, uv[c]);
            }
            if (uv[0].Count == 0) for (int i = 0; i < pos.Count; i++) uv[0].Add(Vector4.zero);
            bw.AddRange(m.boneWeights);
            binds = m.bindposes;
            bounds = m.bounds;
            int n = m.vertexCount;
            for (int s = 0; s < m.blendShapeCount; s++)
            {
                var sh = new Shape { name = m.GetBlendShapeName(s) };
                for (int f = 0; f < m.GetBlendShapeFrameCount(s); f++)
                {
                    var a = new Vector3[n]; var b = new Vector3[n]; var c = new Vector3[n];
                    m.GetBlendShapeFrameVertices(s, f, a, b, c);
                    sh.weights.Add(m.GetBlendShapeFrameWeight(s, f));
                    sh.dv.Add(a.ToList()); sh.dn.Add(b.ToList()); sh.dt.Add(c.ToList());
                    if (b.Any(x => x.sqrMagnitude > 1e-12f)) sh.hasNormals = true;
                }
                shapes.Add(sh);
            }
        }

        public int Clone(int v)
        {
            pos.Add(pos[v]);
            if (nrm.Count > v) nrm.Add(nrm[v]);
            if (tan.Count > v) tan.Add(tan[v]);
            if (col.Count > v) col.Add(col[v]);
            for (int c = 0; c < 4; c++) if (uv[c].Count > v) uv[c].Add(uv[c][v]);
            if (bw.Count > v) bw.Add(bw[v]);
            foreach (var sh in shapes)
                for (int f = 0; f < sh.dv.Count; f++)
                {
                    sh.dv[f].Add(sh.dv[f][v]); sh.dn[f].Add(sh.dn[f][v]); sh.dt[f].Add(sh.dt[f][v]);
                }
            return pos.Count - 1;
        }

        // Rebuilds the mesh; with keep, vertices not flagged are dropped and indices remapped.
        public Mesh Build(string name, List<List<int>> subs, List<MeshTopology> topo, bool[] keep = null)
        {
            var map = new int[pos.Count];
            int count = 0;
            for (int i = 0; i < pos.Count; i++) map[i] = keep == null || keep[i] ? count++ : -1;
            var m = new Mesh { name = name };
            m.indexFormat = count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(Pick(pos, map, count));
            if (nrm.Count == pos.Count) m.SetNormals(Pick(nrm, map, count));
            if (tan.Count == pos.Count) m.SetTangents(Pick(tan, map, count));
            if (col.Count == pos.Count) m.SetColors(Pick(col, map, count));
            for (int c = 0; c < 4; c++) if (uv[c].Count == pos.Count) m.SetUVs(c, Pick(uv[c], map, count));
            if (bw.Count == pos.Count) m.boneWeights = Pick(bw, map, count).ToArray();
            m.bindposes = binds;
            m.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) m.SetIndices(subs[s].Select(i => map[i]).ToList(), topo[s], s, false);
            foreach (var sh in shapes)
                for (int f = 0; f < sh.dv.Count; f++)
                    m.AddBlendShapeFrame(sh.name, sh.weights[f], Pick(sh.dv[f], map, count).ToArray(),
                        sh.hasNormals ? Pick(sh.dn[f], map, count).ToArray() : null, sh.hasNormals ? Pick(sh.dt[f], map, count).ToArray() : null);
            m.bounds = bounds;
            return m;
        }

        static List<T> Pick<T>(List<T> src, int[] map, int count)
        {
            var r = new List<T>(count);
            for (int i = 0; i < src.Count; i++) if (map[i] >= 0) r.Add(src[i]);
            return r;
        }
    }
}
