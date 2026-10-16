using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestKit
{
    public static class MeshMerger
    {
        // Combines skinned meshes into baseR. Same materials share one submesh, blend shapes are
        // joined by name, and the other renderers are removed.
        public static void Merge(List<SkinnedMeshRenderer> members, SkinnedMeshRenderer baseR)
        {
            members = members.OrderBy(m => m == baseR ? 0 : 1).ToList();
            var baseW2L = baseR.transform.worldToLocalMatrix;
            var baseL2W = baseR.transform.localToWorldMatrix;

            int total = members.Sum(m => m.sharedMesh.vertexCount);
            var verts = new List<Vector3>(total);
            var normals = new List<Vector3>(total);
            var tangents = new List<Vector4>(total);
            var colors = new List<Color>(total);
            var uvs = new List<Vector4>[4];
            bool[] hasUV = new bool[4];
            bool hasColors = members.Any(m => m.sharedMesh.colors.Length > 0);
            for (int c = 0; c < 4; c++)
            {
                uvs[c] = new List<Vector4>(total);
                hasUV[c] = members.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord0 + c));
            }

            var bones = new List<Transform>();
            var binds = new List<Matrix4x4>();
            var weights = new List<BoneWeight>(total);
            var mats = new List<Material>();
            var subs = new List<List<int>>();
            var shapeOrder = new List<string>();
            var shapes = new Dictionary<string, KeyValuePair<float, Vector3[]>[]>();
            var shapeWeights = new Dictionary<string, float>();

            int offset = 0;
            foreach (var r in members)
            {
                var mesh = r.sharedMesh;
                var M = baseW2L * r.transform.localToWorldMatrix;
                int n = mesh.vertexCount;

                foreach (var v in mesh.vertices) verts.Add(M.MultiplyPoint3x4(v));
                var nr = mesh.normals;
                for (int i = 0; i < n; i++) normals.Add(i < nr.Length ? M.MultiplyVector(nr[i]).normalized : Vector3.up);
                var tg = mesh.tangents;
                for (int i = 0; i < n; i++)
                {
                    if (i < tg.Length)
                    {
                        var t = M.MultiplyVector(tg[i]).normalized;
                        tangents.Add(new Vector4(t.x, t.y, t.z, tg[i].w));
                    }
                    else tangents.Add(new Vector4(1, 0, 0, 1));
                }
                if (hasColors)
                {
                    var cl = mesh.colors;
                    for (int i = 0; i < n; i++) colors.Add(i < cl.Length ? cl[i] : Color.white);
                }
                for (int c = 0; c < 4; c++)
                {
                    if (!hasUV[c]) continue;
                    var list = new List<Vector4>();
                    mesh.GetUVs(c, list);
                    for (int i = 0; i < n; i++) uvs[c].Add(i < list.Count ? list[i] : Vector4.zero);
                }

                // Bind poses are relative to the mesh; re-express them for the base renderer's space.
                var remap = new int[Mathf.Max(1, r.bones.Length)];
                var srcBones = r.bones;
                var srcBinds = mesh.bindposes;
                var Minv = M.inverse;
                if (srcBones.Length == 0)
                {
                    remap[0] = AddBone(bones, binds, r.transform, Minv);
                }
                for (int b = 0; b < srcBones.Length; b++)
                {
                    var bp = b < srcBinds.Length ? srcBinds[b] * Minv : Matrix4x4.identity;
                    remap[b] = AddBone(bones, binds, srcBones[b], bp);
                }
                var bw = mesh.boneWeights;
                for (int i = 0; i < n; i++)
                {
                    if (bw.Length == 0) { weights.Add(new BoneWeight { boneIndex0 = remap[0], weight0 = 1 }); continue; }
                    var w = bw[i];
                    w.boneIndex0 = remap[Mathf.Min(w.boneIndex0, remap.Length - 1)];
                    w.boneIndex1 = remap[Mathf.Min(w.boneIndex1, remap.Length - 1)];
                    w.boneIndex2 = remap[Mathf.Min(w.boneIndex2, remap.Length - 1)];
                    w.boneIndex3 = remap[Mathf.Min(w.boneIndex3, remap.Length - 1)];
                    weights.Add(w);
                }

                var rm = r.sharedMaterials;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var mat = s < rm.Length ? rm[s] : null;
                    int slot = mats.IndexOf(mat);
                    if (slot < 0) { mats.Add(mat); subs.Add(new List<int>()); slot = mats.Count - 1; }
                    foreach (var idx in mesh.GetIndices(s)) subs[slot].Add(idx + offset);
                }

                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    var name = mesh.GetBlendShapeName(s);
                    int frames = mesh.GetBlendShapeFrameCount(s);
                    KeyValuePair<float, Vector3[]>[] data;
                    if (!shapes.TryGetValue(name, out data))
                    {
                        data = new KeyValuePair<float, Vector3[]>[frames];
                        for (int f = 0; f < frames; f++)
                            data[f] = new KeyValuePair<float, Vector3[]>(mesh.GetBlendShapeFrameWeight(s, f), new Vector3[total]);
                        shapes[name] = data;
                        shapeOrder.Add(name);
                        shapeWeights[name] = r.GetBlendShapeWeight(s);
                    }
                    var d = new Vector3[n];
                    for (int f = 0; f < frames && f < data.Length; f++)
                    {
                        mesh.GetBlendShapeFrameVertices(s, f, d, null, null);
                        var dst = data[f].Value;
                        for (int i = 0; i < n; i++) dst[offset + i] = M.MultiplyVector(d[i]);
                    }
                }
                offset += n;
            }

            var merged = new Mesh { name = baseR.sharedMesh.name + "_Merged" };
            merged.indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            merged.SetVertices(verts);
            merged.SetNormals(normals);
            merged.SetTangents(tangents);
            if (hasColors) merged.SetColors(colors);
            for (int c = 0; c < 4; c++) if (hasUV[c]) merged.SetUVs(c, uvs[c]);
            merged.boneWeights = weights.ToArray();
            merged.bindposes = binds.ToArray();
            merged.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) merged.SetTriangles(subs[s], s, false);
            foreach (var name in shapeOrder)
                foreach (var f in shapes[name]) merged.AddBlendShapeFrame(name, f.Key, f.Value, null, null);
            merged.RecalculateBounds();

            var rootBone = baseR.rootBone != null ? baseR.rootBone : baseR.transform;
            var toRoot = rootBone.worldToLocalMatrix * baseL2W;
            var bounds = new Bounds(toRoot.MultiplyPoint3x4(verts[0]), Vector3.zero);
            foreach (var v in verts) bounds.Encapsulate(toRoot.MultiplyPoint3x4(v));

            baseR.sharedMesh = merged;
            baseR.bones = bones.ToArray();
            baseR.sharedMaterials = mats.ToArray();
            baseR.localBounds = bounds;
            for (int i = 0; i < shapeOrder.Count; i++) baseR.SetBlendShapeWeight(i, shapeWeights[shapeOrder[i]]);

            foreach (var r in members)
            {
                if (r == baseR) continue;
                if (r.transform.childCount == 0 && r.GetComponents<Component>().Length == 2) Object.DestroyImmediate(r.gameObject);
                else Object.DestroyImmediate(r);
            }
        }

        static int AddBone(List<Transform> bones, List<Matrix4x4> binds, Transform bone, Matrix4x4 bind)
        {
            for (int i = 0; i < bones.Count; i++)
                if (bones[i] == bone && Close(binds[i], bind)) return i;
            bones.Add(bone);
            binds.Add(bind);
            return bones.Count - 1;
        }

        static bool Close(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
            return true;
        }
    }
}
