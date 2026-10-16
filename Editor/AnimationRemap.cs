using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace QuestKit
{
    // Gives the Quest copy its own controllers, clips and material references wherever the PC ones
    // would break: lilToon-only material properties, material swaps to PC materials, and curves on
    // meshes that were merged away. Covers the descriptor and Modular Avatar / VRCFury components.
    public class AnimationRemap
    {
        // lilToon property -> Toon Standard property. Values are divided by the PC material's
        // value, since the builder already baked that value into the Quest material.
        static readonly Dictionary<string, string> Properties = new Dictionary<string, string>
        {
            { "_EmissionBlend", "_EmissionStrength" },
        };

        const float MaxStrength = 1.3f;

        readonly QuestPlan plan;
        readonly Dictionary<string, string> mergedPaths;
        readonly Dictionary<Material, Material> questMats;
        readonly string folder;
        readonly Dictionary<string, Dictionary<string, float>> baseValues;
        readonly Dictionary<string, Object> done = new Dictionary<string, Object>();
        public int changed;

        AnimationRemap(QuestPlan plan, Dictionary<string, string> mergedPaths, Dictionary<Material, Material> questMats, string folder)
        {
            this.plan = plan;
            this.mergedPaths = mergedPaths;
            this.questMats = questMats;
            this.folder = folder;
            baseValues = BaseValues(plan);
        }

        public static int Apply(GameObject copy, QuestPlan plan, Dictionary<string, string> mergedPaths, Dictionary<Material, Material> questMats, string folder)
        {
            var r = new AnimationRemap(plan, mergedPaths, questMats, folder);
            var desc = copy.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                if (desc.baseAnimationLayers != null)
                    for (int i = 0; i < desc.baseAnimationLayers.Length; i++)
                    {
                        var l = desc.baseAnimationLayers[i];
                        l.animatorController = r.Controller(l.animatorController, "");
                        desc.baseAnimationLayers[i] = l;
                    }
                if (desc.specialAnimationLayers != null)
                    for (int i = 0; i < desc.specialAnimationLayers.Length; i++)
                    {
                        var l = desc.specialAnimationLayers[i];
                        l.animatorController = r.Controller(l.animatorController, "");
                        desc.specialAnimationLayers[i] = l;
                    }
            }

            foreach (var c in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                var ns = c.GetType().Namespace ?? "";
                if (!ns.StartsWith("nadena") && !ns.StartsWith("VF")) continue;
                string prefix = r.PrefixFor(c, copy.transform);
                var so = new SerializedObject(c);
                var it = so.GetIterator();
                bool dirty = false;
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null) continue;
                    var o = it.objectReferenceValue;
                    Object q = o;
                    if (o is RuntimeAnimatorController) q = r.Controller((RuntimeAnimatorController)o, prefix);
                    else if (o is AnimationClip) q = r.Clip((AnimationClip)o, prefix);
                    else if (o is Material && questMats.ContainsKey((Material)o)) q = questMats[(Material)o];
                    if (q != o) { it.objectReferenceValue = q; dirty = true; }
                }
                if (dirty) so.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.SaveAssets();
            return r.changed;
        }

        string PrefixFor(MonoBehaviour c, Transform root)
        {
            if (!c.GetType().Name.Contains("MergeAnimator")) return "";
            var so = new SerializedObject(c);
            var mode = so.FindProperty("pathMode");
            if (mode == null || mode.enumValueIndex != 0) return "";
            var rel = so.FindProperty("relativePathRoot.referencePath");
            return rel != null && !string.IsNullOrEmpty(rel.stringValue) ? rel.stringValue : QuestPlan.RelPath(root, c.transform);
        }

        static Dictionary<string, Dictionary<string, float>> BaseValues(QuestPlan plan)
        {
            var result = new Dictionary<string, Dictionary<string, float>>();
            foreach (var info in plan.renderers)
            {
                var values = new Dictionary<string, float>();
                foreach (var m in info.r.sharedMaterials)
                {
                    if (m == null) continue;
                    foreach (var p in Properties.Keys)
                        if (m.HasProperty(p) && !values.ContainsKey(p)) values[p] = m.GetFloat(p);
                }
                result[info.path] = values;
            }
            return result;
        }

        RuntimeAnimatorController Controller(RuntimeAnimatorController rc, string prefix)
        {
            var ac = rc as AnimatorController;
            if (ac == null) return rc;
            Object cached;
            var key = "ac:" + ac.GetInstanceID() + ":" + prefix;
            if (done.TryGetValue(key, out cached)) return (RuntimeAnimatorController)cached;

            var clipMap = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var clip in ac.animationClips.Distinct())
            {
                var q = Clip(clip, prefix);
                if (q != clip) clipMap[clip] = q;
            }
            if (clipMap.Count == 0) { done[key] = ac; return ac; }

            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + QuestBuilder.Safe(ac.name) + "_Quest.controller");
            AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(ac), path);
            var qac = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            foreach (var l in qac.layers) ReplaceIn(l.stateMachine, clipMap);
            EditorUtility.SetDirty(qac);
            done[key] = qac;
            return qac;
        }

        AnimationClip Clip(AnimationClip clip, string prefix)
        {
            if (clip == null) return null;
            Object cached;
            var key = "clip:" + clip.GetInstanceID() + ":" + prefix;
            if (done.TryGetValue(key, out cached)) return (AnimationClip)cached;
            var q = RemapClip(clip, prefix);
            if (q == null) { done[key] = clip; return clip; }
            AssetDatabase.CreateAsset(q, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + QuestBuilder.Safe(clip.name) + "_Quest.anim"));
            done[key] = q;
            changed++;
            return q;
        }

        string Absolute(string prefix, string path)
        {
            if (string.IsNullOrEmpty(prefix)) return path;
            return string.IsNullOrEmpty(path) ? prefix : prefix + "/" + path;
        }

        string Relative(string prefix, string abs)
        {
            if (string.IsNullOrEmpty(prefix)) return abs;
            return abs.StartsWith(prefix + "/") ? abs.Substring(prefix.Length + 1) : abs;
        }

        bool Merged(string prefix, string path, out string target)
        {
            string abs;
            if (mergedPaths.TryGetValue(Absolute(prefix, path), out abs)) { target = Relative(prefix, abs); return true; }
            target = path;
            return false;
        }

        AnimationClip RemapClip(AnimationClip clip, string prefix)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            string unused;
            bool needed = bindings.Any(b => Merged(prefix, b.path, out unused)
                    || (b.propertyName.StartsWith("material.") && Properties.ContainsKey(b.propertyName.Substring(9))))
                || objBindings.Any(b => Merged(prefix, b.path, out unused)
                    || AnimationUtility.GetObjectReferenceCurve(clip, b).Any(k => k.value is Material && questMats.ContainsKey((Material)k.value)));
            if (!needed) return null;

            var q = Object.Instantiate(clip);
            q.name = clip.name + "_Quest";
            foreach (var b in AnimationUtility.GetCurveBindings(q)) AnimationUtility.SetEditorCurve(q, b, null);
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(q)) AnimationUtility.SetObjectReferenceCurve(q, b, null);

            var written = new HashSet<string>();
            foreach (var b in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                var nb = b;
                string target;
                if (Merged(prefix, b.path, out target)) nb.path = target;
                if (b.propertyName.StartsWith("material."))
                {
                    var prop = b.propertyName.Substring(9);
                    string questProp;
                    if (Properties.TryGetValue(prop, out questProp))
                    {
                        RemapValues(curve, BaseValue(Absolute(prefix, b.path), prop));
                        nb.propertyName = "material." + questProp;
                    }
                }
                if (!written.Add(nb.path + "|" + nb.type + "|" + nb.propertyName)) continue;
                AnimationUtility.SetEditorCurve(q, nb, curve);
            }
            foreach (var b in objBindings)
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                for (int i = 0; i < keys.Length; i++)
                {
                    var m = keys[i].value as Material;
                    if (m != null && questMats.ContainsKey(m)) keys[i].value = questMats[m];
                }
                var nb = b;
                string target;
                if (Merged(prefix, b.path, out target)) nb.path = target;
                if (!written.Add(nb.path + "|" + nb.type + "|" + nb.propertyName)) continue;
                AnimationUtility.SetObjectReferenceCurve(q, nb, keys);
            }
            return q;
        }

        float BaseValue(string absPath, string prop)
        {
            Dictionary<string, float> values;
            if (baseValues.TryGetValue(absPath, out values) && values.ContainsKey(prop) && values[prop] > 1e-4f) return values[prop];
            return 1f;
        }

        // lilToon's blend is a lerp that tops out; Toon Standard's strength keeps adding light.
        // Map the curve linearly so its low point is kept and its peak stays below MaxStrength.
        static void RemapValues(AnimationCurve curve, float baseValue)
        {
            var keys = curve.keys;
            if (keys.Length == 0) return;
            float rawLo = keys.Min(k => k.value);
            float lo = rawLo / baseValue, hi = keys.Max(k => k.value) / baseValue;
            float a = 1f / baseValue, c = 0f;
            if (hi > MaxStrength && hi - lo > 1e-4f)
            {
                a = (MaxStrength - lo) / (hi - lo) / baseValue;
                c = lo - a * rawLo;
            }
            for (int k = 0; k < keys.Length; k++)
            {
                keys[k].value = keys[k].value * a + c;
                keys[k].inTangent *= a;
                keys[k].outTangent *= a;
            }
            curve.keys = keys;
        }

        static void ReplaceIn(AnimatorStateMachine sm, Dictionary<AnimationClip, AnimationClip> map)
        {
            foreach (var cs in sm.states) cs.state.motion = Replace(cs.state.motion, map);
            foreach (var child in sm.stateMachines) ReplaceIn(child.stateMachine, map);
        }

        static Motion Replace(Motion m, Dictionary<AnimationClip, AnimationClip> map)
        {
            var clip = m as AnimationClip;
            AnimationClip q;
            if (clip != null) return map.TryGetValue(clip, out q) ? q : m;
            var tree = m as BlendTree;
            if (tree == null) return m;
            var children = tree.children;
            for (int i = 0; i < children.Length; i++) children[i].motion = Replace(children[i].motion, map);
            tree.children = children;
            return m;
        }
    }
}
