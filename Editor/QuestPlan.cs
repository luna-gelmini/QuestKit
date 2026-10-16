using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace QuestKit
{
    public enum MatMode { Keep, ToonStandard, ToonStandardOutline, ToonLit, MatcapLit, Additive, Multiply, Drop }

    public class MatEntry
    {
        public Material src;
        public string key;
        public MatMode mode, recommended;
        public string reason;
        public bool userSet;
        public bool transparent, cutout, emissive;
        public float clearFraction, brightness;
        public int tris;
        public Texture mainTex, emissionTex;
        public bool uvInRange = true, hasExtraMaps;
        public Texture bakedMain;
        public Texture MainOrBaked { get { return bakedMain != null ? bakedMain : mainTex; } }
        public List<string> users = new List<string>();
    }

    public class TexEntry
    {
        public Texture tex;
        public string key;
        public int sourceSize, size, recommended, userSize = -1;
        public float weight, headWeight, area;
        public bool used;
        public bool IsFace { get { return area > 0 && headWeight / area > 0.5f; } }
        public List<string> users = new List<string>();

        public float Aspect { get { return tex.width >= tex.height ? tex.height / (float)tex.width : tex.width / (float)tex.height; } }
        public float MB(int s) { return QuestPlan.TextureBytes(s, Aspect) / (1024f * 1024f); }
    }

    public class PBEntry
    {
        public VRCPhysBone pb;
        public string path, key;
        public int transforms, checks;
        public HashSet<Component> colliders = new HashSet<Component>();
        public float weight;
        public bool keep, recommended, userSet, trimColliders;

        // Thinning: every other parallel chain is left out of the simulation and follows its neighbours.
        public bool thin, recommendedThin;
        public int thinTransforms;
        public List<Transform> thinIgnored = new List<Transform>();
        public Dictionary<Transform, Transform[]> followers = new Dictionary<Transform, Transform[]>();
        public List<Component> keptColliders = new List<Component>();
        public bool CanThin { get { return thinIgnored.Count > 0; } }
        public int Transforms { get { return thin ? thinTransforms : transforms; } }
        public int Checks { get { return Transforms * colliders.Count; } }
        public IEnumerable<Component> ActiveColliders { get { return trimColliders ? keptColliders : (IEnumerable<Component>)colliders; } }
    }

    public class ContactEntry
    {
        public Component c;
        public string path, key, parameter, reason;
        public bool receiver, paramUsed, keep, recommended, userSet;
        public int priority;
    }

    public class CompEntry
    {
        public Component c;
        public string path, key, type, reason;
        public bool remove, recommended, userSet;
        public bool isConstraint, isParticles;
    }

    public class MergeGroup
    {
        public string key, signature;
        public List<SkinnedMeshRenderer> members = new List<SkinnedMeshRenderer>();
        public SkinnedMeshRenderer baseRenderer;
        public List<string> excluded = new List<string>();
        public bool enabled, recommended, userSet;
    }

    public class RendererInfo
    {
        public Renderer r;
        public string path, signature;
        public bool blendShapesAnimated, materialsAnimated, neverVisible;
        public int tris;
        public float headShare, ratio = 1f;
        public List<string> unusedShapes;
        public float unusedShapesMB;
    }

    public struct StatValue
    {
        public float value;
        public Rank rank;
    }

    public class QuestPlan
    {
        public GameObject avatar;
        public QuestPlanData data;
        public Rank target = Rank.VeryPoor;
        public float textureBudgetMB;
        public bool stripBlendShapeNormals = true;
        public bool atlas, atlasUserSet;
        public bool reduce, reduceUserSet;
        public int triTarget, totalTris;
        public string lastReduction;
        public bool removeShapes = true, removeBones = true;
        // Options for tools that drive the planner with their own style.
        public float reduceTolerance = 0.004f;
        public bool bakeGlowIntoAtlas, setTextureSizes = true;
        public HashSet<Transform> unusedBones = new HashSet<Transform>();
        public string lastBones;
        public AnimationScan scan;
        public HashSet<string> protectedShapes = new HashSet<string>();
        public string outputName;

        public List<MatEntry> materials = new List<MatEntry>();
        public List<TexEntry> textures = new List<TexEntry>();
        public List<PBEntry> physBones = new List<PBEntry>();
        public List<ContactEntry> contacts = new List<ContactEntry>();
        public List<CompEntry> components = new List<CompEntry>();
        public List<MergeGroup> merges = new List<MergeGroup>();
        public List<RendererInfo> renderers = new List<RendererInfo>();
        public List<string> warnings = new List<string>();

        public float meshMB;
        public int vrcConstraints, boneCount;

        readonly Dictionary<Material, MatEntry> matLookup = new Dictionary<Material, MatEntry>();
        readonly Dictionary<Texture, TexEntry> texLookup = new Dictionary<Texture, TexEntry>();

        public MatEntry Mat(Material m)
        {
            MatEntry e;
            return m != null && matLookup.TryGetValue(m, out e) ? e : null;
        }

        public TexEntry Tex(Texture t)
        {
            TexEntry e;
            return t != null && texLookup.TryGetValue(t, out e) ? e : null;
        }

        // ASTC 6x6 is 128 bits per 36 pixels; mipmaps add a third.
        public static long TextureBytes(int maxSide, float aspect)
        {
            double pixels = (double)maxSide * Mathf.Max(1, Mathf.RoundToInt(maxSide * aspect));
            return (long)(pixels * 128.0 / 36.0 / 8.0 * 4.0 / 3.0);
        }

        public static string RelPath(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string AssetKey(Object o)
        {
            string guid;
            long local;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out guid, out local)) return guid + ":" + local;
            return o.name;
        }

        // ---------------------------------------------------------------- analysis

        public static QuestPlan Analyze(GameObject avatar)
        {
            var p = new QuestPlan { avatar = avatar };
            p.data = avatar.GetComponent<QuestPlanData>();
            p.outputName = avatar.name + "_Quest";
            if (p.data != null)
            {
                p.target = (Rank)p.data.targetRank;
                if (!string.IsNullOrEmpty(p.data.outputName)) p.outputName = p.data.outputName;
                string v = null;
                if (p.data.TryGet("opt:stripNormals", out v)) p.stripBlendShapeNormals = v == "1";
            }

            var anim = new AnimationScan(avatar);
            p.scan = anim;
            p.protectedShapes = anim.protectedShapes;
            p.AnalyzeRenderers(anim);
            p.AnalyzeMaterials();
            p.AnalyzePhysBones();
            p.AnalyzeContacts(anim);
            p.AnalyzeComponents();
            p.AnalyzeMerges();
            p.AnalyzeCleanup(anim);
            p.warnings.AddRange(anim.warnings);

            if (p.data != null && p.data.textureBudgetMB > 0) p.textureBudgetMB = p.data.textureBudgetMB;
            else p.textureBudgetMB = p.DefaultTextureBudget();
            uvCache.Clear();
            p.Recommend();
            return p;
        }

        void AnalyzeRenderers(AnimationScan anim)
        {
            var root = avatar.transform;
            var head = HeadSet();
            var bones = new HashSet<Transform>();
            meshMB = 0;

            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer) && !(r is ParticleSystemRenderer)) continue;
                var info = new RendererInfo { r = r, path = RelPath(root, r.transform) };
                info.signature = anim.ToggleSignature(root, r.transform);
                info.blendShapesAnimated = anim.blendShapePaths.Contains(info.path);
                info.materialsAnimated = anim.materialPaths.Contains(info.path);
                info.neverVisible = info.signature == "" && (!r.gameObject.activeInHierarchy || !r.enabled);
                renderers.Add(info);

                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) { mesh = smr.sharedMesh; foreach (var b in smr.bones) if (b != null) bones.Add(b); }
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && !(r is ParticleSystemRenderer)) mesh = mf.sharedMesh;
                if (mesh != null)
                {
                    meshMB += MeshMB(mesh);
                    for (int s = 0; s < mesh.subMeshCount; s++) info.tris += (int)(mesh.GetIndexCount(s) / 3);
                    if (smr != null && mesh.subMeshCount > 0)
                    {
                        float hs = 0;
                        for (int s = 0; s < mesh.subMeshCount; s++) hs += HeadShare(mesh, s, smr.bones, head) * mesh.GetIndexCount(s);
                        info.headShare = info.tris > 0 ? hs / (info.tris * 3f) : 0;
                    }
                    else if (head.Contains(r.transform)) info.headShare = 1;
                }

                var mats = r.sharedMaterials;
                for (int s = 0; s < mats.Length; s++)
                {
                    var m = mats[s];
                    if (m == null) continue;
                    MatEntry e;
                    if (!matLookup.TryGetValue(m, out e))
                    {
                        e = new MatEntry { src = m, key = "mat:" + AssetKey(m) };
                        matLookup[m] = e;
                        materials.Add(e);
                    }
                    if (!e.users.Contains(info.path)) e.users.Add(info.path);
                    if (mesh == null || s >= mesh.subMeshCount) continue;

                    e.tris += (int)(mesh.GetIndexCount(s) / 3);
                    if (e.uvInRange && !UVsInRange(mesh, s)) e.uvInRange = false;
                    float area = SubmeshArea(mesh, s, r.transform.lossyScale);
                    float headShare = smr != null ? HeadShare(mesh, s, smr.bones, head) : (head.Contains(r.transform) ? 1f : 0f);
                    float w = area * (1f + 3f * headShare);
                    AddTextureWeight(MainTexture(m), w, area, headShare, info.path);
                    AddTextureWeight(EmissionTexture(m), w * 0.5f, area, headShare, info.path);
                }
            }
            boneCount = bones.Count;
        }

        HashSet<Transform> HeadSet()
        {
            var set = new HashSet<Transform>();
            var animator = avatar.GetComponent<Animator>();
            var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head != null) foreach (var t in head.GetComponentsInChildren<Transform>(true)) set.Add(t);
            return set;
        }

        void AddTextureWeight(Texture t, float w, float area, float headShare, string user)
        {
            if (t == null) return;
            TexEntry e;
            if (!texLookup.TryGetValue(t, out e))
            {
                e = new TexEntry { tex = t, key = "tex:" + AssetKey(t), sourceSize = TextureUtil.SourceSize(t) };
                texLookup[t] = e;
                textures.Add(e);
            }
            e.weight += w;
            e.area += area;
            e.headWeight += area * headShare;
            if (!e.users.Contains(user)) e.users.Add(user);
        }

        static readonly Dictionary<Mesh, List<Vector2>> uvCache = new Dictionary<Mesh, List<Vector2>>();

        static bool UVsInRange(Mesh mesh, int sub)
        {
            List<Vector2> uv;
            if (!uvCache.TryGetValue(mesh, out uv)) { uv = new List<Vector2>(); mesh.GetUVs(0, uv); uvCache[mesh] = uv; }
            if (uv.Count == 0) return true;
            foreach (var i in mesh.GetIndices(sub))
            {
                var p = uv[i];
                if (p.x < -0.01f || p.x > 1.01f || p.y < -0.01f || p.y > 1.01f) return false;
            }
            return true;
        }

        static float SubmeshArea(Mesh mesh, int sub, Vector3 scale)
        {
            var v = mesh.vertices;
            var idx = mesh.GetIndices(sub);
            if (mesh.GetTopology(sub) != MeshTopology.Triangles) return 0;
            float a = 0;
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                var p0 = Vector3.Scale(v[idx[i]], scale);
                var p1 = Vector3.Scale(v[idx[i + 1]], scale);
                var p2 = Vector3.Scale(v[idx[i + 2]], scale);
                a += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
            }
            return a;
        }

        static float HeadShare(Mesh mesh, int sub, Transform[] bones, HashSet<Transform> head)
        {
            var bw = mesh.boneWeights;
            if (bw.Length == 0 || head.Count == 0) return 0;
            var idx = mesh.GetIndices(sub);
            if (idx.Length == 0) return 0;
            int inHead = 0;
            foreach (var i in idx)
            {
                int b = bw[i].boneIndex0;
                if (b < bones.Length && bones[b] != null && head.Contains(bones[b])) inHead++;
            }
            return inHead / (float)idx.Length;
        }

        public static float MeshMB(Mesh mesh)
        {
            long verts = mesh.vertexCount;
            long bytes = verts * 64;
            if (mesh.uv2 != null && mesh.uv2.Length > 0) bytes += verts * 8;
            long idx = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) idx += mesh.GetIndexCount(s);
            bytes += idx * (mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4 : 2);
            if (mesh.blendShapeCount > 0)
            {
                var d = new Vector3[mesh.vertexCount];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(s);
                    for (int f = 0; f < frames; f++)
                    {
                        mesh.GetBlendShapeFrameVertices(s, f, d, null, null);
                        int moved = 0;
                        for (int i = 0; i < d.Length; i++) if (d[i].sqrMagnitude > 1e-12f) moved++;
                        bytes += moved * 40L;
                    }
                }
            }
            return bytes / (1024f * 1024f);
        }

        public static Texture MainTexture(Material m)
        {
            if (m.HasProperty("_MainTex")) return m.GetTexture("_MainTex");
            return m.mainTexture;
        }

        public static Texture EmissionTexture(Material m)
        {
            return IsEmissive(m) && m.HasProperty("_EmissionMap") ? m.GetTexture("_EmissionMap") : null;
        }

        public static bool IsEmissive(Material m)
        {
            if (m.HasProperty("_UseEmission") && m.GetFloat("_UseEmission") < 0.5f) return false;
            if (!m.HasProperty("_UseEmission") && m.shader.name == "Standard" && !m.IsKeywordEnabled("_EMISSION")) return false;
            if (!m.HasProperty("_EmissionColor")) return false;
            var c = m.GetColor("_EmissionColor");
            return Mathf.Max(c.r, c.g, c.b) > 0.01f;
        }

        public static bool IsQuestShader(Shader s)
        {
            return s != null && s.name.StartsWith("VRChat/Mobile/") && !s.name.Contains("Alpha Blended");
        }

        void AnalyzeMaterials()
        {
            foreach (var e in materials)
            {
                var m = e.src;
                string sn = m.shader.name;
                e.transparent = sn.Contains("Transparent") || sn.Contains("Fade") || m.renderQueue >= 2500;
                e.cutout = !e.transparent && (sn.Contains("Cutout") || m.renderQueue >= 2450);
                e.emissive = IsEmissive(m);
                e.mainTex = MainTexture(m);
                e.hasExtraMaps = LilLooks.On(m, "_UseBumpMap") && m.GetTexture("_BumpMap") != null
                    || LilLooks.On(m, "_UseMatCap") && m.HasProperty("_MatCapBlendMask") && m.GetTexture("_MatCapBlendMask") != null;
                e.emissionTex = EmissionTexture(m);
                if (e.mainTex != null)
                {
                    int w, h;
                    var px = TextureUtil.Read(e.mainTex, 256, out w, out h);
                    int clear = 0;
                    double lum = 0, wsum = 0;
                    foreach (var c in px)
                    {
                        if (c.a < 0.5f) clear++;
                        lum += (0.299 * c.r + 0.587 * c.g + 0.114 * c.b) * c.a;
                        wsum += c.a;
                    }
                    e.clearFraction = clear / (float)Mathf.Max(1, px.Length);
                    e.brightness = wsum > 0 ? (float)(lum / wsum) : 0;
                }
                else
                {
                    e.brightness = m.HasProperty("_Color") ? m.color.grayscale : 1f;
                }
            }
        }

        void AnalyzePhysBones()
        {
            var weights = new Dictionary<Transform, float>();
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var bones = smr.bones;
                foreach (var w in smr.sharedMesh.boneWeights)
                {
                    AddWeight(weights, bones, w.boneIndex0, w.weight0);
                    AddWeight(weights, bones, w.boneIndex1, w.weight1);
                    AddWeight(weights, bones, w.boneIndex2, w.weight2);
                    AddWeight(weights, bones, w.boneIndex3, w.weight3);
                }
            }

            foreach (var pb in avatar.GetComponentsInChildren<VRCPhysBone>(true))
            {
                var e = new PBEntry { pb = pb, path = RelPath(avatar.transform, pb.transform) };
                e.key = "pb:" + e.path + "#" + System.Array.IndexOf(pb.GetComponents<VRCPhysBone>(), pb);
                var rootT = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                var ignore = new HashSet<Transform>(pb.ignoreTransforms.Where(t => t != null));
                e.transforms = CountChain(rootT, ignore, weights, ref e.weight);
                foreach (var c in pb.colliders) if (c != null) e.colliders.Add(c);
                e.checks = e.transforms * e.colliders.Count;
                FindThinning(e, rootT, ignore);
                physBones.Add(e);
            }
        }

        static void FindThinning(PBEntry e, Transform root, HashSet<Transform> ignore)
        {
            var branch = root;
            while (true)
            {
                var kids = branch.Cast<Transform>().Where(c => !ignore.Contains(c)).ToList();
                if (kids.Count == 1) { branch = kids[0]; continue; }
                if (kids.Count < 3) return;
                float dummy = 0;
                var none = new Dictionary<Transform, float>();
                e.thinTransforms = e.transforms;
                for (int i = 1; i < kids.Count; i += 2)
                {
                    e.thinIgnored.Add(kids[i]);
                    e.thinTransforms -= CountChain(kids[i], ignore, none, ref dummy);
                    var lines = new List<List<Transform>> { Line(kids[i - 1], ignore) };
                    if (i + 1 < kids.Count) lines.Add(Line(kids[i + 1], ignore));
                    foreach (var t in kids[i].GetComponentsInChildren<Transform>(true))
                    {
                        int depth = 0;
                        for (var x = t; x != kids[i]; x = x.parent) depth++;
                        e.followers[t] = lines.Select(l => l[Mathf.Min(depth, l.Count - 1)]).ToArray();
                    }
                }
                return;
            }
        }

        static List<Transform> Line(Transform start, HashSet<Transform> ignore)
        {
            var line = new List<Transform>();
            for (var t = start; t != null; t = t.Cast<Transform>().FirstOrDefault(c => !ignore.Contains(c))) line.Add(t);
            return line;
        }

        static void AddWeight(Dictionary<Transform, float> d, Transform[] bones, int i, float w)
        {
            if (w <= 0 || i >= bones.Length || bones[i] == null) return;
            float cur;
            d.TryGetValue(bones[i], out cur);
            d[bones[i]] = cur + w;
        }

        static int CountChain(Transform t, HashSet<Transform> ignore, Dictionary<Transform, float> weights, ref float weight)
        {
            if (ignore.Contains(t)) return 0;
            float w;
            if (weights.TryGetValue(t, out w)) weight += w;
            int n = 1;
            foreach (Transform c in t) n += CountChain(c, ignore, weights, ref weight);
            return n;
        }

        void AnalyzeContacts(AnimationScan anim)
        {
            foreach (var c in avatar.GetComponentsInChildren<VRC.Dynamics.ContactBase>(true))
            {
                var e = new ContactEntry { c = c, path = RelPath(avatar.transform, c.transform) };
                e.key = "contact:" + e.path + "#" + System.Array.IndexOf(c.GetComponents<VRC.Dynamics.ContactBase>(), c);
                var rec = c as VRCContactReceiver;
                e.receiver = rec != null;
                if (rec != null)
                {
                    e.parameter = rec.parameter;
                    e.paramUsed = !string.IsNullOrEmpty(rec.parameter) && anim.parameters.Contains(rec.parameter);
                    e.priority = e.paramUsed ? 3 : 0;
                    e.reason = e.paramUsed ? "Drives \"" + rec.parameter + "\"" : "Its parameter isn't used by any animator";
                }
                else
                {
                    e.priority = 2;
                    e.reason = "Lets other people's contacts react to you";
                }
                contacts.Add(e);
            }
        }

        static readonly string[] AllowedNamespaces = { "VRC.", "nadena.", "VF.", "QuestKit", "BlackStartX.", "Thry", "lilToon" };

        void AnalyzeComponents()
        {
            foreach (var c in avatar.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string reason = null;
                bool constraint = false, particles = false;
                if (c is Light) reason = "Lights aren't allowed on Quest";
                else if (c is Cloth) reason = "Cloth isn't allowed on Quest";
                else if (c is Camera) reason = "Cameras aren't allowed on Quest";
                else if (c is AudioSource) reason = "Audio sources aren't allowed on Quest avatars";
                else if (c is Rigidbody || c is Joint) reason = "Rigidbodies and joints aren't allowed on Quest";
                else if (c is Collider) reason = "Physics colliders aren't allowed on Quest";
                else if (c is TrailRenderer || c is LineRenderer) reason = "Trail and line renderers cost too much on Quest";
                else if (c is ParticleSystem) { particles = true; reason = "Particles are heavy on Quest; Poor allows at most 2 systems"; }
                else if (c is UnityEngine.Animations.IConstraint) { constraint = true; reason = "Unity constraints don't work on Quest; convert them to VRChat constraints first if you need them"; }
                else if (c is MonoBehaviour)
                {
                    var ns = c.GetType().FullName ?? "";
                    if (c is VRC.SDKBase.IEditorOnly) continue;
                    if (AllowedNamespaces.Any(a => ns.StartsWith(a))) continue;
                    reason = "Custom scripts can't be uploaded to Quest";
                }
                if (reason == null) continue;
                var e = new CompEntry
                {
                    c = c, path = RelPath(avatar.transform, c.transform), type = c.GetType().Name,
                    reason = reason, isConstraint = constraint, isParticles = particles
                };
                e.key = "comp:" + e.path + "#" + e.type;
                components.Add(e);
            }

            foreach (var info in renderers)
            {
                if (!info.neverVisible) continue;
                components.Add(new CompEntry
                {
                    c = info.r, path = info.path, type = info.r.GetType().Name,
                    reason = "Hidden and never toggled on, so nobody can see it",
                    key = "comp:" + info.path + "#hidden"
                });
            }

            vrcConstraints = avatar.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true).Length;
        }

        void AnalyzeMerges()
        {
            var bySig = new Dictionary<string, MergeGroup>();
            foreach (var info in renderers)
            {
                var smr = info.r as SkinnedMeshRenderer;
                if (smr == null || smr.sharedMesh == null || info.neverVisible) continue;
                MergeGroup g;
                if (!bySig.TryGetValue(info.signature, out g))
                {
                    g = new MergeGroup { signature = info.signature, key = "merge:" + info.signature };
                    bySig[info.signature] = g;
                }
                if (info.materialsAnimated)
                {
                    g.excluded.Add(info.path + " (material swap animation)");
                    continue;
                }
                if (info.blendShapesAnimated)
                {
                    if (g.baseRenderer != null && Info(g.baseRenderer).blendShapesAnimated)
                    {
                        g.excluded.Add(info.path + " (its blend shapes are animated too)");
                        continue;
                    }
                    g.baseRenderer = smr;
                }
                g.members.Add(smr);
            }

            foreach (var g in bySig.Values)
            {
                if (g.members.Count < 2) continue;
                if (g.baseRenderer == null)
                    g.baseRenderer = g.members.OrderByDescending(m => m.sharedMesh.vertexCount).First();
                merges.Add(g);
            }
        }

        void AnalyzeCleanup(AnimationScan anim)
        {
            foreach (var info in renderers)
            {
                var smr = info.r as SkinnedMeshRenderer;
                if (smr == null || smr.sharedMesh == null) continue;
                info.unusedShapes = Cleanup.UnusedShapes(smr.sharedMesh, info.path, anim);
                if (info.unusedShapes != null && info.unusedShapes.Count > 0) info.unusedShapesMB = Cleanup.ShapesMB(smr.sharedMesh, info.unusedShapes);
            }
            string v = null;
            if (data != null && data.TryGet("opt:removeShapes", out v)) removeShapes = v == "1";
            if (data != null && data.TryGet("opt:removeBones", out v)) removeBones = v == "1";
        }

        // Called after PhysBone choices change, since kept PhysBones need their bones.
        public void FindUnusedBones()
        {
            unusedBones = Cleanup.UnusedBones(avatar, scan, physBones.Where(p => p.keep).Select(p => p.pb));
        }

        public RendererInfo Info(Renderer r)
        {
            return renderers.FirstOrDefault(i => i.r == r);
        }

        // ---------------------------------------------------------------- recommendations

        public float DefaultTextureBudget()
        {
            float forDownload = (QuestLimits.DownloadCapMB * 0.9f - meshMB * DownloadMeshFactor) / DownloadTextureFactor;
            float forRank = target == Rank.VeryPoor ? 40f : QuestLimits.Limit(Stat.TextureMemoryMB, target);
            return Mathf.Max(1f, Mathf.Min(forDownload, forRank));
        }

        public const float DownloadTextureFactor = 0.85f;
        public const float DownloadMeshFactor = 0.33f;

        public void Recommend()
        {
            foreach (var e in materials) RecommendMaterial(e);
            RecommendComponents();
            RecommendPhysBones();
            RecommendContacts();
            foreach (var g in merges)
            {
                g.recommended = target != Rank.VeryPoor || g.members.Count >= 3;
                string v = null;
                g.userSet = data != null && data.TryGet(g.key, out v);
                g.enabled = g.userSet ? v == "1" : g.recommended;
            }
            foreach (var t in textures)
            {
                string v = null;
                int s;
                if (data != null && data.TryGet(t.key, out v) && int.TryParse(v, out s)) t.userSize = s;
            }
            string rv = null, tv = null;
            reduceUserSet = data != null && data.TryGet("opt:reduce", out rv);
            totalTris = renderers.Where(i => !(i.r is ParticleSystemRenderer)).Sum(i => i.tris);
            int rankTris = (int)Mathf.Min(QuestLimits.Limit(Stat.Triangles, target), float.MaxValue);
            reduce = reduceUserSet ? rv == "1" : target != Rank.VeryPoor && totalTris > rankTris;
            int savedTarget;
            if (data != null && data.TryGet("opt:triTarget", out tv) && int.TryParse(tv, out savedTarget)) triTarget = savedTarget;
            else triTarget = target != Rank.VeryPoor ? Mathf.RoundToInt(rankTris * 0.95f) : Mathf.RoundToInt(totalTris * 0.5f);
            ComputeRatios();

            string av = null;
            atlasUserSet = data != null && data.TryGet("opt:atlas", out av);
            FitTextures();
            if (atlasUserSet) atlas = av == "1";
            else
            {
                // Atlasing costs resolution once textures no longer fit in one 2048 texture, which
                // only pays off when the rank target needs fewer material slots.
                atlas = true;
                var groups = Atlas.Plan(this);
                atlas = groups.Count > 0 && (target != Rank.VeryPoor || groups.All(g => g.scale >= 0.99f));
            }
        }

        void RecommendMaterial(MatEntry e)
        {
            var m = e.src;
            if (IsQuestShader(m.shader)) { e.recommended = MatMode.Keep; e.reason = "Already a Quest shader"; }
            else if ((e.transparent || e.cutout) && e.clearFraction > 0.05f)
            {
                string kind = e.cutout ? "cutout" : "see-through";
                if (e.emissive || e.brightness > 0.6f)
                {
                    e.recommended = MatMode.Additive;
                    e.reason = "Bright " + kind + " part (glow, highlight): Additive adds its light on top. Quest has no alpha blending";
                }
                else if (e.brightness < 0.45f)
                {
                    e.recommended = MatMode.Multiply;
                    e.reason = "Dark " + kind + " overlay (shadow, blush, lines): Multiply darkens what's under it. Quest has no alpha blending";
                }
                else if (e.clearFraction > 0.5f)
                {
                    e.recommended = MatMode.Multiply;
                    e.reason = "Mostly clear " + kind + " overlay: as a solid material it would cover what's under it, so Multiply tints instead. Quest has no alpha blending";
                }
                else
                {
                    e.recommended = MatMode.ToonStandard;
                    e.reason = "Mid-tone " + kind + " part: Quest can't hide the clear pixels, so they turn solid. Check it, or drop it";
                }
            }
            else if (e.transparent || e.cutout)
            {
                e.recommended = MatMode.ToonStandard;
                e.reason = "Marked " + (e.cutout ? "cutout" : "transparent") + " but the texture is solid, so it converts cleanly";
            }
            else
            {
                e.recommended = MatMode.ToonStandard;
                e.reason = e.emissive ? "Opaque with glow: Toon Standard keeps shading and emission" : "Opaque: Toon Standard keeps toon shading";
            }

            string v = null;
            e.userSet = data != null && data.TryGet(e.key, out v) && System.Enum.IsDefined(typeof(MatMode), v);
            e.mode = e.userSet ? (MatMode)System.Enum.Parse(typeof(MatMode), data.entries.First(x => x.key == e.key).value) : e.recommended;
        }

        void RecommendComponents()
        {
            int particleCap = (int)Mathf.Min(QuestLimits.Limit(Stat.ParticleSystems, target), 2);
            int keptParticles = 0;
            foreach (var e in components)
            {
                e.recommended = true;
                if (e.isParticles && keptParticles < particleCap)
                {
                    e.recommended = false;
                    keptParticles++;
                }
                string v = null;
                e.userSet = data != null && data.TryGet(e.key, out v);
                e.remove = e.userSet ? v == "1" : e.recommended;
            }
        }

        void RecommendPhysBones()
        {
            int capPB = (int)QuestLimits.Limit(Stat.PhysBones, target);
            int capT = (int)QuestLimits.Limit(Stat.PhysBoneTransforms, target);
            int capC = (int)QuestLimits.Limit(Stat.PhysBoneColliders, target);
            int capK = (int)QuestLimits.Limit(Stat.CollisionChecks, target);

            foreach (var e in physBones) { e.recommended = false; e.recommendedThin = false; e.trimColliders = false; e.thin = false; }
            var chosen = PickPhysBones(capPB, capT);
            foreach (var e in physBones) e.thin = e.recommendedThin;
            foreach (var e in chosen) e.recommended = true;
            foreach (var e in physBones)
            {
                string v = null;
                e.userSet = data != null && data.TryGet(e.key, out v);
                e.keep = e.userSet ? v != "0" : e.recommended;
                if (e.userSet) e.thin = v == "thin" && e.CanThin;
            }
            AssignColliders(physBones.Where(x => x.keep).ToList(), capC, capK);
            FindUnusedBones();
        }

        // Collision checks are the scarce part. First every PhysBone gets its most useful collider
        // (floors first: they keep chains out of the ground), then the rest go by importance.
        public void AssignColliders(List<PBEntry> chosen, int capC, int capK)
        {
            int k = 0;
            var used = new HashSet<Component>();
            foreach (var e in chosen) { e.keptColliders.Clear(); e.trimColliders = false; }
            // Pass 0 goes cheapest first so small chains (tails, ears) aren't starved by one big one.
            var passes = new[] { chosen.OrderBy(x => x.Transforms).ToList(), chosen.OrderByDescending(x => x.weight).ToList() };
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var e in passes[pass])
                {
                    var candidates = e.colliders.Where(c => !e.keptColliders.Contains(c))
                        .OrderBy(c => IsPlane(c) ? 0 : 1)
                        .ThenBy(c => used.Contains(c) ? 0 : 1).ToList();
                    foreach (var c in candidates)
                    {
                        if (e.keptColliders.Contains(c)) continue;
                        // Left/right colliders only make sense together, or one leg goes through the cloth.
                        var group = new List<Component> { c };
                        var twin = Twin(c, e.colliders);
                        if (twin != null && !e.keptColliders.Contains(twin)) group.Add(twin);
                        int newCols = group.Count(g => !used.Contains(g));
                        if (k + e.Transforms * group.Count > capK || used.Count + newCols > capC) continue;
                        foreach (var g in group) { e.keptColliders.Add(g); used.Add(g); }
                        k += e.Transforms * group.Count;
                        if (pass == 0) break;
                    }
                }
            }
            foreach (var e in chosen) e.trimColliders = e.keptColliders.Count < e.colliders.Count;
        }

        static Component Twin(Component c, IEnumerable<Component> pool)
        {
            string n = c.name, other = null;
            if (n.EndsWith("_L")) other = n.Substring(0, n.Length - 2) + "_R";
            else if (n.EndsWith("_R")) other = n.Substring(0, n.Length - 2) + "_L";
            else if (n.EndsWith(".L")) other = n.Substring(0, n.Length - 2) + ".R";
            else if (n.EndsWith(".R")) other = n.Substring(0, n.Length - 2) + ".L";
            return other == null ? null : pool.FirstOrDefault(x => x != null && x.name == other);
        }

        static bool IsPlane(Component c)
        {
            var col = c as VRC.Dynamics.VRCPhysBoneColliderBase;
            return col != null && col.shapeType == VRC.Dynamics.VRCPhysBoneColliderBase.ShapeType.Plane;
        }

        // Knapsack over component and transform caps. Value grows slower than the vertex count so
        // one huge chain doesn't automatically beat several smaller ones.
        List<PBEntry> PickPhysBones(int capPB, int capT)
        {
            // Each PhysBone can be left out, kept whole, or kept thinned (most of the look, fewer bones).
            var items = physBones.Where(e => (e.CanThin ? e.thinTransforms : e.transforms) <= capT).ToList();
            int n = items.Count;
            var best = new float[n + 1, capPB + 1, capT + 1];
            var choice = new byte[n + 1, capPB + 1, capT + 1];
            for (int i = 1; i <= n; i++)
            {
                var e = items[i - 1];
                float v = Mathf.Pow(Mathf.Max(1f, e.weight), 0.75f);
                for (int c = 0; c <= capPB; c++)
                    for (int t = 0; t <= capT; t++)
                    {
                        best[i, c, t] = best[i - 1, c, t];
                        if (c < 1) continue;
                        if (t >= e.transforms && best[i - 1, c - 1, t - e.transforms] + v > best[i, c, t])
                        {
                            best[i, c, t] = best[i - 1, c - 1, t - e.transforms] + v;
                            choice[i, c, t] = 1;
                        }
                        if (e.CanThin && t >= e.thinTransforms && best[i - 1, c - 1, t - e.thinTransforms] + v * 0.85f > best[i, c, t])
                        {
                            best[i, c, t] = best[i - 1, c - 1, t - e.thinTransforms] + v * 0.85f;
                            choice[i, c, t] = 2;
                        }
                    }
            }
            var picked = new List<PBEntry>();
            int cc = capPB, tt = capT;
            for (int i = n; i >= 1; i--)
            {
                var ch = choice[i, cc, tt];
                if (ch == 0) continue;
                var e = items[i - 1];
                e.recommendedThin = ch == 2;
                picked.Add(e);
                cc--;
                tt -= ch == 2 ? e.thinTransforms : e.transforms;
            }
            return picked;
        }

        void RecommendContacts()
        {
            int cap = (int)QuestLimits.Limit(Stat.Contacts, target);
            foreach (var e in contacts) e.recommended = false;
            int n = 0;
            foreach (var e in contacts.Where(x => x.priority > 0).OrderByDescending(x => x.priority))
            {
                if (n >= cap) break;
                e.recommended = true;
                n++;
            }
            foreach (var e in contacts)
            {
                string v = null;
                e.userSet = data != null && data.TryGet(e.key, out v);
                e.keep = e.userSet ? v == "1" : e.recommended;
            }
        }

        // Spend the texture budget where it shows most: start small, keep doubling the texture
        // with the highest weight per pixel until the budget runs out.
        public void FitTextures()
        {
            var used = new HashSet<Texture>();
            foreach (var m in materials)
            {
                if (m.mode == MatMode.Drop) continue;
                if (m.mainTex != null) used.Add(m.mainTex);
                if (m.emissionTex != null && (m.mode == MatMode.ToonStandard || m.mode == MatMode.ToonStandardOutline || m.mode == MatMode.Keep))
                    used.Add(m.emissionTex);
            }

            float spent = 0;
            var auto = new List<TexEntry>();
            foreach (var t in textures)
            {
                t.used = used.Contains(t.tex);
                if (!t.used) continue;
                if (t.userSize > 0) { t.size = Mathf.Min(t.userSize, t.sourceSize); spent += t.MB(t.size) * Copies(t); }
                else { t.size = Mathf.Min(t.IsFace ? 1024 : 256, t.sourceSize); spent += t.MB(t.size) * Copies(t); auto.Add(t); }
            }

            while (true)
            {
                TexEntry best = null;
                float bestScore = 0;
                foreach (var t in auto)
                {
                    if (t.size >= t.sourceSize || t.size >= 2048) continue;
                    float extra = (t.MB(t.size * 2) - t.MB(t.size)) * Copies(t);
                    if (spent + extra > textureBudgetMB) continue;
                    float score = t.weight / (t.size * (float)t.size);
                    if (score > bestScore) { bestScore = score; best = t; }
                }
                if (best == null) break;
                spent += (best.MB(best.size * 2) - best.MB(best.size)) * Copies(best);
                best.size *= 2;
            }
            foreach (var t in auto) t.recommended = t.size;
        }

        // Additive and Multiply each need their own baked copy of the texture.
        int Copies(TexEntry t)
        {
            var modes = new HashSet<MatMode>();
            foreach (var m in materials)
            {
                if (m.mode == MatMode.Drop || (m.mainTex != t.tex && m.emissionTex != t.tex)) continue;
                modes.Add(m.mode == MatMode.Additive || m.mode == MatMode.Multiply ? m.mode : MatMode.Keep);
            }
            return Mathf.Max(1, modes.Count);
        }

        // ---------------------------------------------------------------- projected stats

        public Dictionary<Stat, float> Project()
        {
            var s = new Dictionary<Stat, float>();
            foreach (Stat st in System.Enum.GetValues(typeof(Stat))) s[st] = 0;

            var removed = new HashSet<Component>(components.Where(c => c.remove).Select(c => c.c));
            float meshTotal = 0;
            foreach (var info in renderers)
            {
                if (removed.Contains(info.r) || info.r is ParticleSystemRenderer) continue;
                var smr = info.r as SkinnedMeshRenderer;
                var mf = info.r.GetComponent<MeshFilter>();
                var mesh = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                if (smr != null) s[Stat.SkinnedMeshes]++; else s[Stat.Meshes]++;
                float mb = MeshMB(mesh) - (removeShapes ? info.unusedShapesMB : 0f);
                meshTotal += mb * (stripBlendShapeNormals && mesh.blendShapeCount > 0 ? 0.75f : 1f);
                var mats = info.r.sharedMaterials;
                for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
                {
                    var me = Mat(mats[i]);
                    if (me != null && me.mode == MatMode.Drop) continue;
                    s[Stat.Triangles] += mesh.GetIndexCount(i) / 3 * (reduce ? info.ratio : 1f);
                }
            }
            foreach (var g in merges.Where(x => x.enabled))
            {
                int live = g.members.Count(m => !removed.Contains(m));
                if (live >= 2) s[Stat.SkinnedMeshes] -= live - 1;
            }
            s[Stat.MaterialSlots] = FinalSlots(removed);

            s[Stat.Bones] = boneCount - (removeBones ? unusedBones.Count : 0);
            var cols = new HashSet<Component>();
            foreach (var p in physBones.Where(x => x.keep))
            {
                s[Stat.PhysBones]++;
                s[Stat.PhysBoneTransforms] += p.Transforms;
                var active = p.ActiveColliders.ToList();
                s[Stat.CollisionChecks] += p.Transforms * active.Count;
                cols.UnionWith(active);
            }
            s[Stat.PhysBoneColliders] = cols.Count;
            s[Stat.Contacts] = contacts.Count(c => c.keep);
            s[Stat.Constraints] = vrcConstraints + components.Count(c => c.isConstraint && !c.remove);
            s[Stat.ParticleSystems] = components.Count(c => c.isParticles && !c.remove);

            float tex = 0;
            var groups = Atlas.Plan(this);
            var atlased = new HashSet<Texture>();
            foreach (var g in groups)
            {
                foreach (var m in g.materials) { atlased.Add(m.mainTex); if (m.emissionTex != null) atlased.Add(m.emissionTex); }
                float mb = TextureBytes(g.side, 1f) / (1024f * 1024f);
                tex += g.emissive ? mb * 1.25f : mb;
            }
            foreach (var t in textures.Where(x => x.used))
                if (!atlased.Contains(t.tex) || Copies(t) > 1) tex += t.MB(t.size) * (atlased.Contains(t.tex) ? Copies(t) - 1 : Copies(t));
            s[Stat.TextureMemoryMB] = tex;
            s[Stat.DownloadMB] = tex * DownloadTextureFactor + meshTotal * DownloadMeshFactor;
            return s;
        }

        // Splits the triangle target across meshes: each keeps min(1, k * importance) of its
        // triangles, with k found so the total lands on the target. Head meshes and the viseme mesh
        // count more, so faces keep their detail while clothing gives up the most.
        public void ComputeRatios()
        {
            var live = renderers.Where(i => !(i.r is ParticleSystemRenderer) && i.tris > 0).ToList();
            foreach (var i in live) i.ratio = 1f;
            if (!reduce || triTarget >= totalTris) return;
            System.Func<RendererInfo, float> weight = i => 1f + 3f * i.headShare;
            float lo = 0, hi = 100;
            for (int it = 0; it < 40; it++)
            {
                float k = (lo + hi) / 2;
                float sum = live.Sum(i => i.tris * Mathf.Clamp(k * weight(i), 0.05f, 1f));
                if (sum > triTarget) hi = k; else lo = k;
            }
            foreach (var i in live) i.ratio = Mathf.Clamp(lo * weight(i), 0.05f, 1f);
        }

        // Material slots after merging and atlasing: each final mesh draws each distinct material once.
        int FinalSlots(HashSet<Component> removed)
        {
            var groupOf = new Dictionary<Material, string>();
            foreach (var g in Atlas.Plan(this)) foreach (var m in g.materials) groupOf[m.src] = g.key;
            var meshes = new List<List<Renderer>>();
            var inGroup = new HashSet<Renderer>();
            foreach (var g in merges.Where(x => x.enabled))
            {
                var live = g.members.Where(m => !removed.Contains(m)).Cast<Renderer>().ToList();
                if (live.Count < 2) continue;
                meshes.Add(live);
                inGroup.UnionWith(live);
            }
            foreach (var info in renderers)
                if (!removed.Contains(info.r) && !(info.r is ParticleSystemRenderer) && !inGroup.Contains(info.r)) meshes.Add(new List<Renderer> { info.r });

            int slots = 0;
            foreach (var list in meshes)
            {
                var keys = new HashSet<string>();
                int unnamed = 0;
                foreach (var r in list)
                {
                    var smr = r as SkinnedMeshRenderer;
                    var mf = r.GetComponent<MeshFilter>();
                    var mesh = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
                    {
                        var me = Mat(mats[i]);
                        if (me != null && me.mode == MatMode.Drop) continue;
                        if (mats[i] == null) { unnamed++; continue; }
                        string gk;
                        keys.Add(groupOf.TryGetValue(mats[i], out gk) ? "atlas:" + gk : "mat:" + mats[i].GetInstanceID());
                    }
                }
                slots += keys.Count + unnamed;
            }
            return slots;
        }

        // ---------------------------------------------------------------- persistence

        public void Save()
        {
            if (data == null)
            {
                data = Undo.AddComponent<QuestPlanData>(avatar);
            }
            Undo.RecordObject(data, "Quest plan");
            data.targetRank = (int)target;
            data.outputName = outputName;
            data.textureBudgetMB = textureBudgetMB;
            data.Set("opt:stripNormals", stripBlendShapeNormals ? "1" : "0");
            if (atlasUserSet) data.Set("opt:atlas", atlas ? "1" : "0");
            if (reduceUserSet) data.Set("opt:reduce", reduce ? "1" : "0");
            data.Set("opt:triTarget", triTarget.ToString());
            data.Set("opt:removeShapes", removeShapes ? "1" : "0");
            data.Set("opt:removeBones", removeBones ? "1" : "0");
            foreach (var e in materials) if (e.userSet) data.Set(e.key, e.mode.ToString());
            foreach (var e in textures) if (e.userSize > 0) data.Set(e.key, e.userSize.ToString());
            foreach (var e in physBones) if (e.userSet) data.Set(e.key, !e.keep ? "0" : e.thin ? "thin" : "1");
            foreach (var e in contacts) if (e.userSet) data.Set(e.key, e.keep ? "1" : "0");
            foreach (var e in components) if (e.userSet) data.Set(e.key, e.remove ? "1" : "0");
            foreach (var e in merges) if (e.userSet) data.Set(e.key, e.enabled ? "1" : "0");
            EditorUtility.SetDirty(data);
        }

        public void ClearSaved()
        {
            if (data == null) return;
            Undo.RecordObject(data, "Reset quest plan");
            data.entries.Clear();
            data.textureBudgetMB = -1;
            EditorUtility.SetDirty(data);
        }
    }

    // Finds what the avatar's animations and toggle components touch.
    public class AnimationScan
    {
        public HashSet<string> togglePaths = new HashSet<string>();
        public HashSet<string> blendShapePaths = new HashSet<string>();
        public HashSet<string> materialPaths = new HashSet<string>();
        public HashSet<string> parameters = new HashSet<string>();
        public HashSet<string> protectedShapes = new HashSet<string>();
        public Dictionary<string, HashSet<string>> shapeNames = new Dictionary<string, HashSet<string>>();
        public HashSet<string> allShapesPaths = new HashSet<string>();
        public HashSet<string> animatedPaths = new HashSet<string>();
        public bool allShapesUsed;
        readonly HashSet<string> animatedMaterialProps = new HashSet<string>();
        public List<string> warnings = new List<string>();

        public AnimationScan(GameObject avatar)
        {
            var root = avatar.transform;
            var seen = new HashSet<RuntimeAnimatorController>();
            var desc = avatar.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                foreach (var l in desc.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0]) AddController(l.animatorController, "", seen);
                foreach (var l in desc.specialAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0]) AddController(l.animatorController, "", seen);
                // Visemes and blinking find their mesh through the descriptor, not an animation.
                if (desc.VisemeSkinnedMesh != null) blendShapePaths.Add(QuestPlan.RelPath(root, desc.VisemeSkinnedMesh.transform));
                if (desc.VisemeBlendShapes != null) foreach (var v in desc.VisemeBlendShapes) if (!string.IsNullOrEmpty(v)) protectedShapes.Add(v);
                var eyelids = desc.customEyeLookSettings.eyelidsBlendshapes;
                var eyelidMesh = desc.customEyeLookSettings.eyelidsSkinnedMesh;
                if (eyelids != null && eyelidMesh != null && eyelidMesh.sharedMesh != null)
                    foreach (var i in eyelids)
                        if (i >= 0 && i < eyelidMesh.sharedMesh.blendShapeCount) protectedShapes.Add(eyelidMesh.sharedMesh.GetBlendShapeName(i));
                if (desc.customEyeLookSettings.eyelidsSkinnedMesh != null)
                    blendShapePaths.Add(QuestPlan.RelPath(root, desc.customEyeLookSettings.eyelidsSkinnedMesh.transform));
                if (desc.expressionParameters != null)
                    foreach (var p in desc.expressionParameters.parameters) if (p != null) parameters.Add(p.name);
            }

            foreach (var c in avatar.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null || c is VRCAvatarDescriptor) continue;
                var typeName = c.GetType().Name;
                var ns = c.GetType().Namespace ?? "";
                bool toolComponent = ns.StartsWith("nadena") || ns.StartsWith("VF");
                // VRCFury names shape keys in its own data; keep every key rather than guess.
                if (ns.StartsWith("VF")) allShapesUsed = true;
                string prefix = "";
                var so = new SerializedObject(c);
                if (typeName.Contains("MergeAnimator"))
                {
                    var mode = so.FindProperty("pathMode");
                    var rel = so.FindProperty("relativePathRoot.referencePath");
                    if (mode != null && mode.enumValueIndex == 0)
                        prefix = rel != null && !string.IsNullOrEmpty(rel.stringValue) ? rel.stringValue : QuestPlan.RelPath(root, c.transform);
                }

                var it = so.GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue != null)
                    {
                        var o = it.objectReferenceValue;
                        if (o is RuntimeAnimatorController) AddController((RuntimeAnimatorController)o, prefix, seen);
                        else if (o is AnimationClip) AddClip((AnimationClip)o, prefix);
                        else if (toolComponent && (o is GameObject || o is Renderer))
                        {
                            var t = o is GameObject ? ((GameObject)o).transform : ((Component)o).transform;
                            if (t.IsChildOf(root) && t != root) AddToolTarget(typeName, QuestPlan.RelPath(root, t));
                        }
                    }
                    else if (toolComponent && it.propertyType == SerializedPropertyType.String && it.name == "referencePath"
                             && !string.IsNullOrEmpty(it.stringValue) && !it.propertyPath.Contains("relativePathRoot"))
                    {
                        AddToolTarget(typeName, it.stringValue);
                    }
                }
            }
        }

        void AddToolTarget(string typeName, string path)
        {
            if (typeName.Contains("MergeArmature") || typeName.Contains("BoneProxy") || typeName.Contains("MenuInstaller")) return;
            if (typeName.Contains("ShapeChanger") || typeName.Contains("BlendShape")) { blendShapePaths.Add(path); allShapesPaths.Add(path); }
            else if (typeName.Contains("Material")) materialPaths.Add(path);
            else togglePaths.Add(path);
        }

        void AddController(RuntimeAnimatorController rc, string prefix, HashSet<RuntimeAnimatorController> seen)
        {
            if (rc == null || !seen.Add(rc)) return;
            var ac = rc as AnimatorController;
            if (ac != null) foreach (var p in ac.parameters) parameters.Add(p.name);
            foreach (var clip in rc.animationClips) AddClip(clip, prefix);
        }

        void AddClip(AnimationClip clip, string prefix)
        {
            if (clip == null) return;
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var path = Join(prefix, b.path);
                animatedPaths.Add(path);
                if (b.propertyName == "m_IsActive" || b.propertyName == "m_Enabled") togglePaths.Add(path);
                else if (b.propertyName.StartsWith("blendShape."))
                {
                    blendShapePaths.Add(path);
                    HashSet<string> names;
                    if (!shapeNames.TryGetValue(path, out names)) shapeNames[path] = names = new HashSet<string>();
                    names.Add(b.propertyName.Substring(11));
                }
                else if (b.propertyName.StartsWith("material.") && animatedMaterialProps.Add(path + "|" + b.propertyName))
                    warnings.Add("\"" + (path == "" ? "(root)" : path) + "\" animates " + b.propertyName.Substring(9) + " on its material in " + clip.name
                        + (b.propertyName == "material._EmissionBlend" ? ". The Quest copy gets its own controller that drives the Toon Standard glow instead."
                    : ". Quest shaders may not have that property, so the effect can stop working."));
            }
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!b.propertyName.StartsWith("m_Materials")) continue;
                var path = Join(prefix, b.path);
                if (materialPaths.Add(path))
                    warnings.Add("\"" + path + "\" swaps materials in an animation. The Quest copy gets its own clip that swaps to the Quest materials.");
            }
        }

        static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "/" + b;
        }

        // Everything that can turn this object on or off: toggles on itself or any parent.
        public string ToggleSignature(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent)
            {
                var p = QuestPlan.RelPath(root, x);
                if (togglePaths.Contains(p)) parts.Add(p + (x.gameObject.activeSelf ? "+" : "-"));
            }
            parts.Sort();
            return string.Join("|", parts);
        }
    }
}
