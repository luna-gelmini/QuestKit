using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace QuestKit
{
    public static class QuestBuilder
    {
        public static GameObject Build(QuestPlan plan)
        {
            var src = plan.avatar;
            string folder = "Assets/QuestKit/Generated/" + Safe(src.name);
            Directory.CreateDirectory(folder + "/Meshes");

            var old = src.scene.GetRootGameObjects().FirstOrDefault(g => g.name == plan.outputName && g != src);
            Vector3 pos = old != null ? old.transform.position : src.transform.position + src.transform.right * 1.5f;
            if (old != null) Undo.DestroyObjectImmediate(old);

            var copy = Object.Instantiate(src, pos, src.transform.rotation);
            copy.name = plan.outputName;
            copy.SetActive(true);
            Undo.RegisterCreatedObjectUndo(copy, "Build Quest avatar");
            var planData = copy.GetComponent<QuestPlanData>();
            if (planData != null) Object.DestroyImmediate(planData);

            var map = new ObjectMap(src.transform, copy.transform);

            try
            {
                AssetDatabase.StartAssetEditing();
                Progress("Removing components", 0.05f);
                foreach (var e in plan.components.Where(c => c.remove))
                {
                    var c = map.Get(e.c);
                    if (c == null) continue;
                    if (e.isParticles)
                    {
                        var psr = c.GetComponent<ParticleSystemRenderer>();
                        Object.DestroyImmediate(c);
                        if (psr != null) Object.DestroyImmediate(psr);
                    }
                    else Object.DestroyImmediate(c);
                }

                var keptColliders = new HashSet<Component>();
                foreach (var e in plan.physBones)
                {
                    var pb = map.Get(e.pb) as VRCPhysBone;
                    if (pb == null) continue;
                    if (!e.keep) { Object.DestroyImmediate(pb); continue; }
                    if (e.thin) Thin(e, pb, map);
                    if (e.trimColliders)
                    {
                        pb.colliders.Clear();
                        foreach (var c in e.keptColliders)
                        {
                            var cc = map.Get(c) as VRC.Dynamics.VRCPhysBoneColliderBase;
                            if (cc != null) pb.colliders.Add(cc);
                        }
                    }
                    foreach (var c in pb.colliders) if (c != null) keptColliders.Add(c);
                }
                foreach (var col in copy.GetComponentsInChildren<VRCPhysBoneCollider>(true))
                    if (!keptColliders.Contains(col)) Object.DestroyImmediate(col);

                foreach (var e in plan.contacts.Where(c => !c.keep))
                {
                    var c = map.Get(e.c);
                    if (c != null) Object.DestroyImmediate(c);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Progress("Baking lilToon layers", 0.15f);
            foreach (var e in plan.materials)
            {
                e.bakedMain = null;
                if (e.mode == MatMode.Drop || e.mode == MatMode.Keep) continue;
                var tex = plan.Tex(e.mainTex);
                e.bakedMain = LilLooks.BakeLayers(e.src, e.mainTex, folder, tex != null ? tex.size : 1024);
            }

            Progress("Converting materials", 0.2f);
            var questMats = new Dictionary<Material, Material>();
            foreach (var e in plan.materials)
                if (e.mode != MatMode.Drop) questMats[e.src] = MakeMaterial(plan, e, folder);

            Progress("Rebuilding meshes", 0.45f);
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                var keep = new List<int>();
                for (int i = 0; i < mats.Length; i++)
                {
                    var me = plan.Mat(mats[i]);
                    if (me == null || me.mode != MatMode.Drop) keep.Add(i);
                }
                var smr = r as SkinnedMeshRenderer;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = smr != null ? smr.sharedMesh : (mf != null && !(r is ParticleSystemRenderer) ? mf.sharedMesh : null);
                bool strip = plan.stripBlendShapeNormals && mesh != null && mesh.blendShapeCount > 0;
                if (mesh != null && (keep.Count < mats.Length || strip))
                {
                    var nm = CopyMesh(mesh, mats.Length, keep, strip);
                    if (smr != null) smr.sharedMesh = nm; else mf.sharedMesh = nm;
                }
                r.sharedMaterials = keep.Select(i => mats[i] != null && questMats.ContainsKey(mats[i]) ? questMats[mats[i]] : mats[i]).ToArray();
                if (keep.Count == 0) r.enabled = false;
            }

            if (plan.removeShapes)
            {
                Progress("Removing unused shape keys", 0.5f);
                foreach (var info in plan.renderers)
                {
                    if (info.unusedShapes == null || info.unusedShapes.Count == 0) continue;
                    var smr = map.Get(info.r) as SkinnedMeshRenderer;
                    if (smr == null || smr.sharedMesh == null) continue;
                    Cleanup.RemoveShapes(smr, smr.sharedMesh, new HashSet<string>(info.unusedShapes));
                }
            }

            if (plan.reduce)
            {
                Progress("Reducing triangles", 0.55f);
                int before = 0, after = 0;
                var opt = new Decimator.Options { protectedShapes = plan.protectedShapes, maxError = plan.reduceTolerance };
                foreach (var info in plan.renderers)
                {
                    if (info.ratio >= 0.999f) continue;
                    var r = map.Get(info.r) as Renderer;
                    if (r == null) continue;
                    var smr = r as SkinnedMeshRenderer;
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    opt.ratio = info.ratio;
                    int b, a;
                    var nm = Decimator.Simplify(mesh, opt, out b, out a);
                    before += b; after += a;
                    if (nm == null) continue;
                    if (smr != null) smr.sharedMesh = nm; else mf.sharedMesh = nm;
                }
                plan.lastReduction = before + " → " + after;
            }

            Progress("Merging meshes", 0.65f);
            var mergedPaths = new Dictionary<string, string>();
            foreach (var g in plan.merges.Where(x => x.enabled))
            {
                var members = g.members.Select(m => map.Get(m) as SkinnedMeshRenderer).Where(m => m != null && m.sharedMesh != null).ToList();
                var baseR = map.Get(g.baseRenderer) as SkinnedMeshRenderer;
                if (members.Count < 2 || baseR == null) continue;
                var basePath = QuestPlan.RelPath(copy.transform, baseR.transform);
                foreach (var m in members)
                    if (m != baseR) mergedPaths[QuestPlan.RelPath(copy.transform, m.transform)] = basePath;
                MeshMerger.Merge(members, baseR);
            }

            Progress("Fixing animations", 0.7f);
            var animFolder = folder + "/Animations";
            AssetDatabase.DeleteAsset(animFolder);
            Directory.CreateDirectory(animFolder);
            AssetDatabase.Refresh();
            AnimationRemap.Apply(copy, plan, mergedPaths, questMats, animFolder);

            Progress("Building atlases", 0.72f);
            var groups = Atlas.Plan(plan);
            foreach (var g in groups)
            {
                Material atlasMat;
                var template = questMats[g.materials[0].src];
                var rects = Atlas.Build(plan, g, folder, out atlasMat, template);
                var finalMats = new Dictionary<Material, Material>();
                foreach (var e in g.materials) finalMats[questMats[e.src]] = atlasMat;
                var questRects = new Dictionary<Material, Rect>();
                foreach (var kv in rects) questRects[questMats[kv.Key]] = kv.Value;
                foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.sharedMaterials.Any(m => m != null && questRects.ContainsKey(m))) continue;
                    var smr = r as SkinnedMeshRenderer;
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    Material[] newMats;
                    var nm = Atlas.Remap(mesh, r.sharedMaterials, questRects, finalMats, out newMats);
                    if (smr != null) smr.sharedMesh = nm; else mf.sharedMesh = nm;
                    r.sharedMaterials = newMats;
                }
            }

            if (plan.removeBones)
            {
                Progress("Removing unused bones", 0.74f);
                var keptPBs = copy.GetComponentsInChildren<VRCPhysBone>(true);
                var unused = Cleanup.UnusedBones(copy, new AnimationScan(copy), keptPBs);
                plan.lastBones = unused.Count + " unused, " + Cleanup.RemoveBones(copy, unused) + " deleted";
            }

            Progress("Saving meshes", 0.75f);
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
            {
                var smr = r as SkinnedMeshRenderer;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                if (mesh == null || AssetDatabase.Contains(mesh)) continue;
                var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/Meshes/" + Safe(QuestPlan.RelPath(copy.transform, r.transform)) + ".asset");
                AssetDatabase.CreateAsset(mesh, path);
            }

            if (plan.setTextureSizes)
            {
                Progress("Setting Quest texture sizes", 0.85f);
                foreach (var t in plan.textures.Where(x => x.used)) TextureUtil.SetAndroidSize(t.tex, t.size);
            }

            AssetDatabase.SaveAssets();
            EditorUtility.ClearProgressBar();
            Selection.activeGameObject = copy;
            return copy;
        }

        // Thinned chains leave the simulation and copy the local rotation of the chains beside them.
        static void Thin(PBEntry e, VRCPhysBone pb, ObjectMap map)
        {
            foreach (var t in e.thinIgnored)
            {
                var ct = map.Get(t) as Transform;
                if (ct != null) pb.ignoreTransforms.Add(ct);
            }
            foreach (var kv in e.followers)
            {
                var target = map.Get(kv.Key) as Transform;
                if (target == null) continue;
                var con = target.gameObject.AddComponent<VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint>();
                con.SolveInLocalSpace = true;
                float w = 1f / kv.Value.Length;
                foreach (var src in kv.Value)
                {
                    var cs = map.Get(src) as Transform;
                    if (cs != null) con.Sources.Add(new VRC.Dynamics.VRCConstraintSource(cs, w, Vector3.zero, Vector3.zero));
                }
                con.ActivateConstraint();
                con.Locked = true;
                con.IsActive = true;
            }
        }

        // Builds the Quest copy as an LZMA Android asset bundle, the way VRChat packs uploads, and
        // returns its size in bytes. Modular Avatar and VRCFury don't run here, so their additions
        // aren't counted.
        public static long MeasureAndroid(GameObject quest)
        {
            string folder = "Assets/QuestKit/Generated";
            Directory.CreateDirectory(folder);
            var prefabPath = folder + "/_measure.prefab";
            var outDir = "Temp/QuestKitMeasure";
            try
            {
                EditorUtility.DisplayProgressBar("Quest Planner", "Building an Android bundle (first time can take a while)", 0.5f);
                CopyVrcDefinesToAndroid();
                PrefabUtility.SaveAsPrefabAsset(quest, prefabPath);
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                Directory.CreateDirectory(outDir);
                var build = new AssetBundleBuild { assetBundleName = "measure", assetNames = new[] { prefabPath } };
                var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build }, BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.Android);
                if (manifest == null) return -1;
                return new FileInfo(Path.Combine(outDir, "measure")).Length;
            }
            finally
            {
                AssetDatabase.DeleteAsset(prefabPath);
                EditorUtility.ClearProgressBar();
            }
        }

        // The SDK adds its VRC_* defines to Android when you switch platforms. Building without
        // switching needs them too, or packages that check them (Gesture Manager) won't compile.
        static void CopyVrcDefinesToAndroid()
        {
            var pc = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';');
            var android = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android).Split(';').Where(x => x != "").ToList();
            var missing = pc.Where(x => x.StartsWith("VRC_") && !android.Contains(x)).ToList();
            if (missing.Count == 0) return;
            android.AddRange(missing);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android, string.Join(";", android.ToArray()));
        }

        static void Progress(string what, float f)
        {
            EditorUtility.DisplayProgressBar("Quest Planner", what, f);
        }

        public static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('/', '_');
        }

        // ---------------------------------------------------------------- materials

        static Material MakeMaterial(QuestPlan plan, MatEntry e, string folder)
        {
            if (e.mode == MatMode.Keep) return e.src;
            var m = e.src;
            Material q;
            string suffix;
            Color tint = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            var tex = plan.Tex(e.mainTex);
            int size = tex != null ? tex.size : 512;

            switch (e.mode)
            {
                case MatMode.Additive:
                case MatMode.Multiply:
                {
                    bool add = e.mode == MatMode.Additive;
                    q = new Material(Shader.Find(add ? "VRChat/Mobile/Particles/Additive" : "VRChat/Mobile/Particles/Multiply"));
                    suffix = add ? "_Additive" : "_Multiply";
                    if (e.mainTex != null)
                    {
                        int w, h;
                        var px = TextureUtil.ReadSource(e.MainOrBaked, out w, out h);
                        Color glow = add && e.emissive && m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black;
                        for (int i = 0; i < px.Length; i++)
                        {
                            var c = px[i] * tint;
                            float a = px[i].a * tint.a;
                            px[i] = add
                                ? new Color(Mathf.Clamp01(c.r * a + glow.r * a * 0.5f), Mathf.Clamp01(c.g * a + glow.g * a * 0.5f), Mathf.Clamp01(c.b * a + glow.b * a * 0.5f), 1)
                                : new Color(Mathf.Lerp(1, c.r, a), Mathf.Lerp(1, c.g, a), Mathf.Lerp(1, c.b, a), 1);
                        }
                        var baked = TextureUtil.WritePng(px, w, h, folder + "/" + QuestBuilder.Safe(m.name) + suffix + ".png");
                        TextureUtil.SetAndroidSize(baked, size);
                        q.SetTexture("_MainTex", baked);
                    }
                    break;
                }
                case MatMode.ToonLit:
                    q = new Material(Shader.Find("VRChat/Mobile/Toon Lit"));
                    suffix = "_ToonLit";
                    q.SetTexture("_MainTex", e.mainTex);
                    break;
                case MatMode.MatcapLit:
                    q = new Material(Shader.Find("VRChat/Mobile/MatCap Lit"));
                    suffix = "_MatcapLit";
                    q.SetTexture("_MainTex", e.mainTex);
                    if (m.HasProperty("_MatCapTex")) q.SetTexture("_MatCap", m.GetTexture("_MatCapTex"));
                    else if (m.HasProperty("_MatCap")) q.SetTexture("_MatCap", m.GetTexture("_MatCap"));
                    break;
                default:
                {
                    bool outline = e.mode == MatMode.ToonStandardOutline;
                    q = new Material(Shader.Find(outline ? "VRChat/Mobile/Toon Standard (Outline)" : "VRChat/Mobile/Toon Standard"));
                    suffix = "_Quest";
                    q.SetTexture("_MainTex", e.MainOrBaked);
                    q.SetColor("_Color", new Color(tint.r, tint.g, tint.b, 1));
                    LilLooks.Apply(m, q);
                    var ramp = Resources.Load<Texture>("VRChat/" + RampFor(m));
                    if (ramp != null) q.SetTexture("_Ramp", ramp);
                    q.SetFloat("_MinBrightness", 0.05f);
                    bool reflective = m.HasProperty("_UseReflection") ? m.GetFloat("_UseReflection") > 0.5f : m.HasProperty("_Glossiness");
                    float smooth = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 0f;
                    q.SetFloat("_GlossStrength", reflective ? smooth : 0f);
                    if (reflective && smooth > 0) q.EnableKeyword("USE_SPECULAR");
                    if (m.HasProperty("_Cull")) q.SetFloat("_Culling", m.GetFloat("_Cull"));
                    if (e.emissive)
                    {
                        q.SetTexture("_EmissionMap", e.emissionTex != null ? e.emissionTex : e.mainTex);
                        q.SetColor("_EmissionColor", EmissionColorFor(m));
                        q.SetFloat("_EmissionStrength", 1f);
                    }
                    else q.SetColor("_EmissionColor", Color.black);
                    if (outline)
                    {
                        if (m.HasProperty("_OutlineColor")) q.SetColor("_OutlineColor", m.GetColor("_OutlineColor"));
                        if (m.HasProperty("_OutlineWidth")) q.SetFloat("_OutlineThickness", m.GetFloat("_OutlineWidth"));
                    }
                    break;
                }
            }
            q.name = m.name + suffix;
            var path = folder + "/" + Safe(q.name) + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.shader = q.shader;
                existing.CopyPropertiesFromMaterial(q);
                Object.DestroyImmediate(q);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(q, path);
            return q;
        }

        // lilToon scales emission by _EmissionBlend.
        public static Color EmissionColorFor(Material m)
        {
            if (!m.HasProperty("_EmissionColor")) return Color.black;
            var glow = m.GetColor("_EmissionColor");
            if (m.HasProperty("_EmissionBlend")) glow *= m.GetFloat("_EmissionBlend");
            return new Color(glow.r, glow.g, glow.b, 1);
        }

        // Follows lilToon's shadow settings: off means flat lighting, a wide blur means soft shading.
        public static string RampFor(Material m)
        {
            if (m.HasProperty("_UseShadow"))
            {
                if (m.GetFloat("_UseShadow") < 0.5f) return "ShadowRampFlat";
                if (m.HasProperty("_ShadowBlur") && m.GetFloat("_ShadowBlur") >= 0.3f) return "ShadowRampRealisticVerySoft";
            }
            return "ShadowRampToon2Band";
        }

        // ---------------------------------------------------------------- meshes

        static Mesh CopyMesh(Mesh src, int slotCount, List<int> keepSlots, bool stripNormals)
        {
            var m = Object.Instantiate(src);
            m.name = src.name + "_Quest";
            if (keepSlots.Count < slotCount)
            {
                var subs = keepSlots.Where(i => i < src.subMeshCount).Select(i => new { idx = src.GetIndices(i), topo = src.GetTopology(i) }).ToList();
                m.subMeshCount = subs.Count;
                for (int i = 0; i < subs.Count; i++) m.SetIndices(subs[i].idx, subs[i].topo, i);
            }
            if (stripNormals)
            {
                var shapes = new List<KeyValuePair<string, List<KeyValuePair<float, Vector3[]>>>>();
                for (int s = 0; s < src.blendShapeCount; s++)
                {
                    var frames = new List<KeyValuePair<float, Vector3[]>>();
                    for (int f = 0; f < src.GetBlendShapeFrameCount(s); f++)
                    {
                        var d = new Vector3[src.vertexCount];
                        src.GetBlendShapeFrameVertices(s, f, d, null, null);
                        frames.Add(new KeyValuePair<float, Vector3[]>(src.GetBlendShapeFrameWeight(s, f), d));
                    }
                    shapes.Add(new KeyValuePair<string, List<KeyValuePair<float, Vector3[]>>>(src.GetBlendShapeName(s), frames));
                }
                m.ClearBlendShapes();
                foreach (var s in shapes)
                    foreach (var f in s.Value) m.AddBlendShapeFrame(s.Key, f.Key, f.Value, null, null);
            }
            return m;
        }

        // Maps objects in the source avatar to the same object in the copy.
        class ObjectMap
        {
            readonly Transform from, to;
            public ObjectMap(Transform from, Transform to) { this.from = from; this.to = to; }

            public Component Get(Component c)
            {
                if (c == null) return null;
                // Walk by sibling index: object names may contain '/', which breaks Transform.Find.
                var indices = new List<int>();
                for (var x = c.transform; x != from; x = x.parent)
                {
                    if (x == null) return null;
                    indices.Add(x.GetSiblingIndex());
                }
                var t = to;
                for (int k = indices.Count - 1; k >= 0; k--)
                {
                    if (indices[k] >= t.childCount) return null;
                    t = t.GetChild(indices[k]);
                }
                var all = c.GetComponents(c.GetType());
                int i = System.Array.IndexOf(all, c);
                var there = t.GetComponents(c.GetType());
                return i >= 0 && i < there.Length ? there[i] : null;
            }
        }
    }
}
