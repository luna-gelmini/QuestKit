using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace QuestKit
{
    public static class Cleanup
    {
        // ---------------------------------------------------------------- shape keys

        // Shape keys nothing can drive: not animated, not visemes or blinking, not used by a shape
        // changer. Returns null when every key may be in use (VRCFury, or a shape changer on it).
        public static List<string> UnusedShapes(Mesh mesh, string path, AnimationScan anim)
        {
            if (mesh == null || mesh.blendShapeCount == 0 || anim.allShapesUsed || anim.allShapesPaths.Contains(path)) return null;
            HashSet<string> used;
            anim.shapeNames.TryGetValue(path, out used);
            var result = new List<string>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var n = mesh.GetBlendShapeName(i);
                if (anim.protectedShapes.Contains(n) || (used != null && used.Contains(n))) continue;
                result.Add(n);
            }
            return result;
        }

        public static float ShapesMB(Mesh mesh, IEnumerable<string> names)
        {
            long bytes = 0;
            var d = new Vector3[mesh.vertexCount];
            foreach (var n in names)
            {
                int s = mesh.GetBlendShapeIndex(n);
                if (s < 0) continue;
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                {
                    mesh.GetBlendShapeFrameVertices(s, f, d, null, null);
                    for (int i = 0; i < d.Length; i++) if (d[i].sqrMagnitude > 1e-12f) bytes += 40;
                }
            }
            return bytes / (1024f * 1024f);
        }

        // Removes the named shape keys. Ones the renderer has turned up are baked into the mesh
        // first, so it looks the same.
        public static Mesh RemoveShapes(SkinnedMeshRenderer smr, Mesh mesh, HashSet<string> remove)
        {
            var weights = new Dictionary<string, float>();
            for (int i = 0; i < mesh.blendShapeCount; i++) weights[mesh.GetBlendShapeName(i)] = smr.GetBlendShapeWeight(i);

            var m = Object.Instantiate(mesh);
            m.name = mesh.name;
            var verts = m.vertices;
            var normals = m.normals;
            var keep = new List<KeyValuePair<string, List<KeyValuePair<float, Vector3[][]>>>>();
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                var name = mesh.GetBlendShapeName(s);
                int frames = mesh.GetBlendShapeFrameCount(s);
                if (remove.Contains(name))
                {
                    float w = weights[name];
                    if (w > 0.01f)
                    {
                        var dv = new Vector3[mesh.vertexCount];
                        var dn = new Vector3[mesh.vertexCount];
                        mesh.GetBlendShapeFrameVertices(s, frames - 1, dv, dn, null);
                        float k = w / mesh.GetBlendShapeFrameWeight(s, frames - 1);
                        for (int i = 0; i < verts.Length; i++) verts[i] += dv[i] * k;
                        if (normals.Length == verts.Length) for (int i = 0; i < verts.Length; i++) normals[i] = (normals[i] + dn[i] * k).normalized;
                    }
                    continue;
                }
                var list = new List<KeyValuePair<float, Vector3[][]>>();
                for (int f = 0; f < frames; f++)
                {
                    var a = new Vector3[mesh.vertexCount]; var b = new Vector3[mesh.vertexCount]; var c = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(s, f, a, b, c);
                    bool hasN = b.Any(x => x.sqrMagnitude > 1e-12f);
                    list.Add(new KeyValuePair<float, Vector3[][]>(mesh.GetBlendShapeFrameWeight(s, f), new[] { a, hasN ? b : null, hasN ? c : null }));
                }
                keep.Add(new KeyValuePair<string, List<KeyValuePair<float, Vector3[][]>>>(name, list));
            }
            m.vertices = verts;
            if (normals.Length == verts.Length) m.normals = normals;
            m.ClearBlendShapes();
            foreach (var sh in keep)
                foreach (var f in sh.Value) m.AddBlendShapeFrame(sh.Key, f.Key, f.Value[0], f.Value[1], f.Value[2]);

            smr.sharedMesh = m;
            for (int i = 0; i < m.blendShapeCount; i++) smr.SetBlendShapeWeight(i, weights[m.GetBlendShapeName(i)]);
            return m;
        }

        // ---------------------------------------------------------------- bones

        // Bones that deform nothing and that nothing else needs. They are counted by VRChat as long
        // as a skinned mesh lists them.
        public static HashSet<Transform> UnusedBones(GameObject avatar, AnimationScan anim, IEnumerable<VRCPhysBone> keptPhysBones)
        {
            var root = avatar.transform;
            var listed = new HashSet<Transform>();
            var required = new HashSet<Transform>();

            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = smr.bones;
                foreach (var b in bones) if (b != null) listed.Add(b);
                if (smr.rootBone != null) required.Add(smr.rootBone);
                if (smr.sharedMesh == null) continue;
                foreach (var w in smr.sharedMesh.boneWeights)
                {
                    if (w.weight0 > 0 && w.boneIndex0 < bones.Length) required.Add(bones[w.boneIndex0]);
                    if (w.weight1 > 0 && w.boneIndex1 < bones.Length) required.Add(bones[w.boneIndex1]);
                    if (w.weight2 > 0 && w.boneIndex2 < bones.Length) required.Add(bones[w.boneIndex2]);
                    if (w.weight3 > 0 && w.boneIndex3 < bones.Length) required.Add(bones[w.boneIndex3]);
                }
            }

            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                foreach (HumanBodyBones hb in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (hb == HumanBodyBones.LastBone) continue;
                    var t = animator.GetBoneTransform(hb);
                    if (t != null) required.Add(t);
                }

            foreach (var pb in keptPhysBones)
            {
                if (pb == null) continue;
                var start = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                var ignore = new HashSet<Transform>(pb.ignoreTransforms.Where(x => x != null));
                AddChain(start, ignore, required);
            }

            foreach (var c in avatar.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                required.Add(c.transform);
                if (c is SkinnedMeshRenderer) continue;
                var so = new SerializedObject(c);
                var it = so.GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null) continue;
                    var o = it.objectReferenceValue;
                    var t = o as Transform ?? (o is GameObject ? ((GameObject)o).transform : o is Component ? ((Component)o).transform : null);
                    if (t != null && t.IsChildOf(root)) required.Add(t);
                }
            }

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (anim.animatedPaths.Contains(QuestPlan.RelPath(root, t)) || anim.togglePaths.Contains(QuestPlan.RelPath(root, t))) required.Add(t);

            foreach (var t in required.ToList())
                for (var x = t; x != null && x != root; x = x.parent) required.Add(x);

            var unused = new HashSet<Transform>();
            foreach (var b in listed) if (!required.Contains(b)) unused.Add(b);
            return unused;
        }

        static void AddChain(Transform t, HashSet<Transform> ignore, HashSet<Transform> into)
        {
            if (ignore.Contains(t)) return;
            into.Add(t);
            foreach (Transform c in t) AddChain(c, ignore, into);
        }

        // Points every skinned mesh's references to unused bones at the nearest kept ancestor (the
        // weights there are zero, so nothing moves), then deletes unused bones with nothing needed below.
        public static int RemoveBones(GameObject avatar, HashSet<Transform> unused)
        {
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = smr.bones;
                bool changed = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null || !unused.Contains(bones[i])) continue;
                    var t = bones[i];
                    while (t != null && unused.Contains(t)) t = t.parent;
                    bones[i] = t != null ? t : avatar.transform;
                    changed = true;
                }
                if (changed) smr.bones = bones;
            }
            int deleted = 0;
            foreach (var t in unused.OrderByDescending(x => Depth(x)).ToList())
            {
                if (t == null) continue;
                bool allChildrenUnused = true;
                foreach (Transform c in t) if (!unused.Contains(c)) { allChildrenUnused = false; break; }
                if (!allChildrenUnused) continue;
                Object.DestroyImmediate(t.gameObject);
                deleted++;
            }
            return deleted;
        }

        static int Depth(Transform t)
        {
            int d = 0;
            for (var x = t; x != null; x = x.parent) d++;
            return d;
        }
    }
}
