using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace QuestKit
{
    public class QuestPlannerWindow : EditorWindow
    {
        GameObject avatar;
        QuestPlan plan;
        Vector2 scroll;
        readonly Dictionary<string, bool> open = new Dictionary<string, bool>();
        GUIStyle wrap, small, header;
        float measuredMB = -1;
        string measuredFor;

        static readonly string[] MatModeNames =
        {
            "Keep as is", "Toon Standard", "Toon Standard (Outline)", "Toon Lit", "MatCap Lit",
            "Additive (glow)", "Multiply (darken)", "Drop"
        };
        static readonly int[] Sizes = { 128, 256, 512, 1024, 2048 };

        [MenuItem("Tools/Quest Planner")]
        static void Open()
        {
            GetWindow<QuestPlannerWindow>("Quest Planner").minSize = new Vector2(520, 400);
        }

        void OnSelectionChange()
        {
            if (plan == null && Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<VRCAvatarDescriptor>() != null)
            {
                avatar = Selection.activeGameObject;
                Repaint();
            }
        }

        void Styles()
        {
            if (wrap != null) return;
            wrap = new GUIStyle(EditorStyles.label) { wordWrap = true };
            small = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            header = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        }

        void OnGUI()
        {
            Styles();
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                var a = (GameObject)EditorGUILayout.ObjectField("PC avatar", avatar, typeof(GameObject), true);
                if (a != avatar) { avatar = a; plan = null; }
                using (new EditorGUI.DisabledScope(avatar == null))
                    if (GUILayout.Button(plan == null ? "Analyze" : "Re-analyze", GUILayout.Width(90))) Analyze();
            }

            if (avatar != null && avatar.GetComponent<VRCAvatarDescriptor>() == null)
                EditorGUILayout.HelpBox("This object has no VRC Avatar Descriptor.", MessageType.Warning);

            if (plan == null || plan.avatar == null)
            {
                EditorGUILayout.HelpBox("Pick the PC avatar and press Analyze. The PC avatar is never changed: Apply builds a separate Quest copy.", MessageType.Info);
                return;
            }

            DrawTargets();
            DrawStats();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var w in plan.warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            DrawMaterials();
            DrawTextures();
            DrawPhysBones();
            DrawContacts();
            DrawTriangles();
            DrawMeshes();
            DrawAtlas();
            DrawComponents();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to recommended", GUILayout.Height(28)))
                {
                    plan.ClearSaved();
                    Analyze();
                    return;
                }
                if (GUILayout.Button("Apply: build " + plan.outputName, GUILayout.Height(28))) Apply();
            }
        }

        void Analyze()
        {
            EditorUtility.DisplayProgressBar("Quest Planner", "Analyzing " + avatar.name, 0.3f);
            try { plan = QuestPlan.Analyze(avatar); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        void Changed()
        {
            plan.FitTextures();
            plan.Save();
        }

        void DrawTargets()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                var names = System.Enum.GetValues(typeof(Rank)).Cast<Rank>().Select(QuestLimits.RankName).ToArray();
                var t = (Rank)EditorGUILayout.Popup("Target rank", (int)plan.target, names);
                if (t != plan.target)
                {
                    plan.target = t;
                    plan.textureBudgetMB = plan.DefaultTextureBudget();
                    plan.Recommend();
                    plan.Save();
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                float b = EditorGUILayout.Slider("Texture budget (MB)", plan.textureBudgetMB, 1, 40);
                if (GUILayout.Button("Auto", GUILayout.Width(50))) b = plan.DefaultTextureBudget();
                if (!Mathf.Approximately(b, plan.textureBudgetMB)) { plan.textureBudgetMB = b; Changed(); }
            }
            var n = EditorGUILayout.TextField("Output name", plan.outputName);
            if (n != plan.outputName) { plan.outputName = n; plan.Save(); }
        }

        void DrawStats()
        {
            var s = plan.Project();
            EditorGUILayout.Space(4);
            Rank worst = Rank.Excellent;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (Stat st in System.Enum.GetValues(typeof(Stat)))
                {
                    float v = s[st];
                    var rank = QuestLimits.RankOf(st, v);
                    if (st != Stat.DownloadMB && rank > worst) worst = rank;
                    float limit = QuestLimits.Limit(st, plan.target);
                    bool over = v > limit;
                    if (v == 0 && !over && st != Stat.Triangles) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(QuestLimits.StatNames[(int)st], GUILayout.Width(150));
                        string val = st == Stat.TextureMemoryMB || st == Stat.DownloadMB ? "~" + v.ToString("0.0") : v.ToString("0");
                        string lim = float.IsInfinity(limit) ? "" : " / " + (st == Stat.TextureMemoryMB || st == Stat.DownloadMB ? limit.ToString("0") : limit.ToString("0"));
                        var c = GUI.color;
                        if (over) GUI.color = new Color(1f, 0.45f, 0.4f);
                        GUILayout.Label(val + lim, EditorStyles.boldLabel, GUILayout.Width(110));
                        GUI.color = c;
                        if (st == Stat.DownloadMB && measuredMB > 0 && measuredFor == plan.outputName) val = measuredMB.ToString("0.00") + " measured";
                        string note = st == Stat.DownloadMB ? (measuredMB > 0 && measuredFor == plan.outputName ? "real Android bundle of the last build" : "estimate; build, then press Measure")
                            : over && QuestLimits.IsHardCap(st) ? "over this, Quest removes ALL of them"
                            : QuestLimits.RankName(rank);
                        GUILayout.Label(note, EditorStyles.miniLabel);
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Projected rank: " + QuestLimits.RankName(worst), header);
                    var built = plan.avatar.scene.GetRootGameObjects().FirstOrDefault(g => g.name == plan.outputName && g != plan.avatar);
                    using (new EditorGUI.DisabledScope(built == null))
                        if (GUILayout.Button(new GUIContent("Measure real size", "Builds " + plan.outputName + " as an Android bundle and reads its size"), GUILayout.Width(130)))
                        {
                            long bytes = QuestBuilder.MeasureAndroid(built);
                            measuredMB = bytes > 0 ? bytes / (1024f * 1024f) : -1;
                            measuredFor = plan.outputName;
                            if (bytes <= 0) ShowNotification(new GUIContent("Android build failed, see the Console"));
                            GUIUtility.ExitGUI();
                        }
                }
            }
        }

        bool Section(string id, string title, int count)
        {
            bool o;
            if (!open.TryGetValue(id, out o)) o = true;
            o = EditorGUILayout.BeginFoldoutHeaderGroup(o, title + " (" + count + ")");
            EditorGUILayout.EndFoldoutHeaderGroup();
            open[id] = o;
            return o && count > 0;
        }

        static void Ping(Object o, string label)
        {
            if (GUILayout.Button(label, EditorStyles.label, GUILayout.ExpandWidth(true))) EditorGUIUtility.PingObject(o);
        }

        void Recommended(bool isRecommended)
        {
            GUILayout.Label(isRecommended ? "" : "changed", EditorStyles.miniLabel, GUILayout.Width(50));
        }

        void DrawMaterials()
        {
            if (!Section("mat", "Materials", plan.materials.Count)) return;
            foreach (var e in plan.materials)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var tex = e.mainTex != null ? AssetPreview.GetAssetPreview(e.mainTex) : null;
                    GUILayout.Label(tex, GUILayout.Width(40), GUILayout.Height(40));
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            Ping(e.src, e.src.name + "  ·  " + e.tris + " tris");
                            var mode = (MatMode)EditorGUILayout.Popup((int)e.mode, MatModeNames, GUILayout.Width(170));
                            if (mode != e.mode) { e.mode = mode; e.userSet = mode != e.recommended; Changed(); }
                            Recommended(e.mode == e.recommended);
                        }
                        GUILayout.Label(e.mode == e.recommended ? e.reason : "Recommended: " + MatModeNames[(int)e.recommended] + ". " + e.reason, small);
                        GUILayout.Label(e.src.shader.name + "  ·  used by " + string.Join(", ", e.users.ToArray()), EditorStyles.miniLabel);
                    }
                }
            }
        }

        void DrawTextures()
        {
            var used = plan.textures.Where(t => t.used).OrderByDescending(t => t.weight).ToList();
            if (!Section("tex", "Texture sizes on Quest", used.Count)) return;
            GUILayout.Label("Sizes are spent where they show most (big areas, the face). Only the Android build is affected.", small);
            foreach (var t in used)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(AssetPreview.GetAssetPreview(t.tex), GUILayout.Width(32), GUILayout.Height(32));
                    using (new EditorGUILayout.VerticalScope())
                    {
                        Ping(t.tex, t.tex.name + "  ·  source " + t.sourceSize + "  ·  " + t.MB(t.size).ToString("0.00") + " MB");
                        GUILayout.Label("used by " + string.Join(", ", t.users.Take(4).ToArray()) + (t.users.Count > 4 ? "…" : ""), EditorStyles.miniLabel);
                    }
                    var options = new List<string> { "Auto (" + t.recommended + ")" };
                    var values = new List<int> { -1 };
                    foreach (var s in Sizes) if (s <= t.sourceSize) { options.Add(s.ToString()); values.Add(s); }
                    int cur = Mathf.Max(0, values.IndexOf(t.userSize));
                    int pick = EditorGUILayout.Popup(cur, options.ToArray(), GUILayout.Width(110));
                    if (pick != cur) { t.userSize = values[pick]; Changed(); }
                }
            }
        }

        void DrawPhysBones()
        {
            if (!Section("pb", "PhysBones", plan.physBones.Count)) return;
            GUILayout.Label("Ordered by how much of the mesh they move. Over the Poor limits Quest disables every PhysBone, so the plan stays under them.", small);
            foreach (var e in plan.physBones.OrderByDescending(x => x.weight))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    int cur = !e.keep ? 0 : e.thin ? 2 : 1;
                    var opts = e.CanThin
                        ? new[] { "Remove", "Keep (" + e.transforms + " bones)", "Thin (" + e.thinTransforms + " bones)" }
                        : new[] { "Remove", "Keep (" + e.transforms + " bones)" };
                    int pick = EditorGUILayout.Popup(cur, opts, GUILayout.Width(130));
                    if (pick != cur)
                    {
                        e.keep = pick > 0;
                        e.thin = pick == 2;
                        e.userSet = e.keep != e.recommended || e.thin != e.recommendedThin;
                        plan.AssignColliders(plan.physBones.Where(x => x.keep).ToList(),
                            (int)QuestLimits.Limit(Stat.PhysBoneColliders, plan.target), (int)QuestLimits.Limit(Stat.CollisionChecks, plan.target));
                        plan.FindUnusedBones();
                        Changed();
                    }
                    Ping(e.pb, e.path);
                    GUILayout.Label((e.keep && e.trimColliders ? e.keptColliders.Count + " of " + e.colliders.Count + " colliders" : e.colliders.Count + " colliders") + " · moves " + e.weight.ToString("0") + " verts"
                        + (e.thin ? " · every other chain follows its neighbours" : ""), EditorStyles.miniLabel, GUILayout.Width(300));
                    Recommended(e.keep == e.recommended && e.thin == e.recommendedThin);
                }
            }
        }

        void DrawContacts()
        {
            if (!Section("contact", "Contacts", plan.contacts.Count)) return;
            foreach (var e in plan.contacts.OrderByDescending(x => x.priority))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    bool k = EditorGUILayout.Toggle(e.keep, GUILayout.Width(18));
                    if (k != e.keep) { e.keep = k; e.userSet = k != e.recommended; Changed(); }
                    Ping(e.c, (e.receiver ? "Receiver  " : "Sender  ") + e.path);
                    GUILayout.Label(e.reason, EditorStyles.miniLabel, GUILayout.Width(260));
                    Recommended(e.keep == e.recommended);
                }
            }
        }

        void DrawMeshes()
        {
            int count = plan.merges.Count + 3;
            if (!Section("mesh", "Meshes", count)) return;
            bool strip = EditorGUILayout.ToggleLeft("Remove blend shape normals (smaller download, faces shade the same)", plan.stripBlendShapeNormals);
            if (strip != plan.stripBlendShapeNormals) { plan.stripBlendShapeNormals = strip; plan.Save(); }

            var withUnused = plan.renderers.Where(i => i.unusedShapes != null && i.unusedShapes.Count > 0).ToList();
            int unusedCount = withUnused.Sum(i => i.unusedShapes.Count);
            float unusedMB = withUnused.Sum(i => i.unusedShapesMB);
            bool rs = EditorGUILayout.ToggleLeft("Remove " + unusedCount + " shape keys nothing animates (~" + unusedMB.ToString("0.0") + " MB before compression)", plan.removeShapes);
            if (rs != plan.removeShapes) { plan.removeShapes = rs; plan.Save(); }
            if (plan.scan != null && plan.scan.allShapesUsed) GUILayout.Label("VRCFury is on this avatar, so every shape key is kept.", small);
            else if (plan.removeShapes)
                foreach (var i in withUnused)
                    GUILayout.Label(i.path + ": " + string.Join(", ", i.unusedShapes.Take(8).ToArray()) + (i.unusedShapes.Count > 8 ? " … +" + (i.unusedShapes.Count - 8) : ""), EditorStyles.miniLabel);
            GUILayout.Label("Shape keys turned up on the renderer are baked into the mesh, so the look doesn't change.", small);

            bool rb = EditorGUILayout.ToggleLeft("Remove " + plan.unusedBones.Count + " bones that move nothing", plan.removeBones);
            if (rb != plan.removeBones) { plan.removeBones = rb; plan.Save(); }
            if (plan.removeBones && plan.unusedBones.Count > 0)
                GUILayout.Label(string.Join(", ", plan.unusedBones.Take(12).Select(b => b.name).ToArray()) + (plan.unusedBones.Count > 12 ? " …" : ""), EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(plan.lastBones)) GUILayout.Label("Last build: " + plan.lastBones, small);

            foreach (var g in plan.merges)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool en = EditorGUILayout.ToggleLeft("Merge " + g.members.Count + " meshes into " + g.baseRenderer.name + " (saves " + (g.members.Count - 1) + " skinned meshes)", g.enabled);
                        if (en != g.enabled) { g.enabled = en; g.userSet = en != g.recommended; Changed(); }
                        Recommended(g.enabled == g.recommended);
                    }
                    GUILayout.Label(g.signature == "" ? "Always visible, so merging can't break a toggle." : "All toggled together by: " + g.signature.Replace("|", ", ").Replace("+", " (on)").Replace("-", " (off)"), small);
                    GUILayout.Label(string.Join(", ", g.members.Select(m => m.name).ToArray()), EditorStyles.miniLabel);
                    foreach (var x in g.excluded) GUILayout.Label("Left out: " + x, EditorStyles.miniLabel);
                }
            }
        }

        void DrawTriangles()
        {
            if (!Section("tris", "Triangles", plan.renderers.Count)) return;
            bool r = EditorGUILayout.ToggleLeft("Reduce triangles (" + plan.totalTris + " now)", plan.reduce);
            if (r != plan.reduce) { plan.reduce = r; plan.reduceUserSet = true; plan.ComputeRatios(); plan.Save(); }
            if (!plan.reduce)
            {
                GUILayout.Label(plan.target == Rank.VeryPoor ? "Very Poor has no triangle limit, so this is off unless you want a lighter avatar."
                    : "Off: the avatar stays above the " + QuestLimits.RankName(plan.target) + " triangle limit.", small);
                return;
            }
            int t = EditorGUILayout.IntSlider("Target", plan.triTarget, 1000, Mathf.Max(1000, plan.totalTris));
            if (t != plan.triTarget) { plan.triTarget = t; plan.ComputeRatios(); plan.Save(); }
            GUILayout.Label("Seams, open edges and viseme/blink vertices are kept, so some meshes stop short of their share. Hidden toggle meshes count too.", small);
            if (!string.IsNullOrEmpty(plan.lastReduction)) GUILayout.Label("Last build: " + plan.lastReduction + " triangles", small);
            foreach (var info in plan.renderers.Where(i => i.tris > 0).OrderByDescending(i => i.tris))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Ping(info.r, info.path);
                    GUILayout.Label(info.tris + " → ~" + Mathf.RoundToInt(info.tris * info.ratio) + (info.headShare > 0.5f ? "  (face, protected)" : ""),
                        EditorStyles.miniLabel, GUILayout.Width(220));
                }
            }
        }

        void DrawAtlas()
        {
            if (!Section("atlas", "Texture atlas", plan.materials.Count)) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                bool a = EditorGUILayout.ToggleLeft("Combine materials that share settings into one texture (fewer material slots)", plan.atlas);
                if (a != plan.atlas) { plan.atlas = a; plan.atlasUserSet = true; Changed(); }
            }
            var was = plan.atlas;
            plan.atlas = true;
            var groups = Atlas.Plan(plan);
            plan.atlas = was;
            foreach (var g in groups)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(string.Join(" + ", g.materials.Select(m => m.src.name).ToArray()) + " → one " + g.side + " texture", wrap);
                    GUILayout.Label(g.scale >= 0.99f ? "Every texture keeps its planned size."
                        : "Textures shrink to " + Mathf.RoundToInt(g.scale * 100) + "% of their planned size to fit in " + Atlas.MaxSize + ".", small);
                }
            }
            foreach (var e in plan.materials)
            {
                string why;
                if (!Atlas.Eligible(e, out why)) GUILayout.Label("Not atlased: " + e.src.name + " (" + why + ")", EditorStyles.miniLabel);
            }
            if (groups.Count == 0) GUILayout.Label("Nothing to combine: no two materials share Quest settings.", small);
        }

        void DrawComponents()
        {
            if (!Section("comp", "Unsupported or unused parts", plan.components.Count)) return;
            foreach (var e in plan.components)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    bool rm = EditorGUILayout.Toggle(e.remove, GUILayout.Width(18));
                    if (rm != e.remove) { e.remove = rm; e.userSet = rm != e.recommended; Changed(); }
                    Ping(e.c, (e.remove ? "Remove " : "Keep ") + e.type + "  " + e.path);
                    GUILayout.Label(e.reason, EditorStyles.miniLabel, GUILayout.Width(260));
                    Recommended(e.remove == e.recommended);
                }
            }
        }

        void Apply()
        {
            var merges = plan.merges.Where(g => g.enabled).ToList();
            var lines = new List<string>();
            lines.Add("Builds \"" + plan.outputName + "\" as a separate copy. " + avatar.name + " stays untouched.");
            if (plan.avatar.scene.GetRootGameObjects().Any(g => g.name == plan.outputName && g != plan.avatar))
                lines.Add("\nThe existing \"" + plan.outputName + "\" in the scene will be replaced.");
            if (merges.Count > 0)
            {
                lines.Add("\nMeshes to merge:");
                foreach (var g in merges)
                    lines.Add("• " + string.Join(" + ", g.members.Select(m => m.name).ToArray()) + " → " + g.baseRenderer.name);
                lines.Add("Blend shapes and toggles keep working; anything else that points at the merged-away meshes won't.");
            }
            var dropped = plan.materials.Where(m => m.mode == MatMode.Drop).ToList();
            if (dropped.Count > 0) lines.Add("\nDropped: " + string.Join(", ", dropped.Select(m => m.src.name).ToArray()));
            lines.Add("\nQuest texture sizes are set as Android overrides on the original textures; the PC build doesn't change.");
            var changes = new List<string>();
            foreach (var t in plan.textures.Where(x => x.used))
            {
                var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t.tex)) as TextureImporter;
                if (ti == null) continue;
                var ps = ti.GetPlatformTextureSettings("Android");
                if (ps.overridden && ps.maxTextureSize != t.size) changes.Add(t.tex.name + ": " + ps.maxTextureSize + " → " + t.size);
            }
            if (changes.Count > 0)
            {
                lines.Add("These textures already have a Quest size, which other Quest avatars using them will also get:");
                foreach (var c in changes.Take(12)) lines.Add("• " + c);
                if (changes.Count > 12) lines.Add("• … and " + (changes.Count - 12) + " more");
            }

            if (!EditorUtility.DisplayDialog("Build Quest avatar", string.Join("\n", lines.ToArray()), "Build", "Cancel")) return;
            plan.Save();
            GameObject result;
            try { result = QuestBuilder.Build(plan); }
            finally { EditorUtility.ClearProgressBar(); }
            ShowNotification(new GUIContent("Built " + result.name));
        }
    }
}
