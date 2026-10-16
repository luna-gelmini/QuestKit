using System.Collections.Generic;
using UnityEngine;

namespace QuestKit
{
    // Quadric-error mesh simplification by half-edge collapse on positions. A point is removed by
    // sliding it onto a neighbour, and each of its vertex copies (UV seam sides) is replaced by the
    // copy it shares a triangle with on the other point, so surviving vertices keep their own UVs,
    // bone weights and blend shape deltas. A collapse that would tear a UV island is refused.
    // Open edges get extra quadrics that keep their outline, and vertices moved by blend shapes cost
    // more to remove (or are locked, for visemes and blinking).
    public static class Decimator
    {
        public class Options
        {
            public float ratio = 0.5f;
            // Largest allowed surface deviation, as a fraction of the mesh's bounding box diagonal.
            public float maxError = 0.004f;
            public HashSet<string> protectedShapes = new HashSet<string>();
        }

        class Candidate
        {
            public double cost;
            public int p, q, stamp;
        }

        class State
        {
            public MeshData d;
            public List<int> tris;
            public bool[] alive;
            public int[] pointOf;
            public List<Vector3> point = new List<Vector3>();
            public List<int>[] pointTris;
            public List<int>[] pointVerts;
            public bool[] locked, removed;
            public float[] penalty;
            public double[] Q, wsum;
            public int[] stamp;
            public double maxErrorSq;
        }

        public static Mesh Simplify(Mesh src, Options opt, out int trisBefore, out int trisAfter)
        {
            var st = new State { d = new MeshData(src) };
            int n = st.d.Count;
            st.tris = new List<int>();
            var triSub = new List<int>();
            var topo = new List<MeshTopology>();
            var otherSubs = new Dictionary<int, int[]>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                topo.Add(src.GetTopology(s));
                var idx = src.GetIndices(s);
                if (topo[s] != MeshTopology.Triangles) { otherSubs[s] = idx; continue; }
                st.tris.AddRange(idx);
                for (int i = 0; i < idx.Length / 3; i++) triSub.Add(s);
            }
            int triCount = st.tris.Count / 3;
            trisBefore = triCount;
            int target = Mathf.Max(4, Mathf.RoundToInt(triCount * opt.ratio));
            if (target >= triCount) { trisAfter = triCount; return null; }

            // Points: vertices at the same position share one.
            st.pointOf = new int[n];
            var byPos = new Dictionary<Vector3Int, int>();
            for (int v = 0; v < n; v++)
            {
                var key = Quantize(st.d.pos[v]);
                int pt;
                if (!byPos.TryGetValue(key, out pt)) { pt = st.point.Count; byPos[key] = pt; st.point.Add(st.d.pos[v]); }
                st.pointOf[v] = pt;
            }
            int P = st.point.Count;
            st.pointTris = new List<int>[P];
            st.pointVerts = new List<int>[P];
            for (int p = 0; p < P; p++) { st.pointTris[p] = new List<int>(6); st.pointVerts[p] = new List<int>(2); }
            for (int v = 0; v < n; v++) st.pointVerts[st.pointOf[v]].Add(v);
            st.alive = new bool[triCount];
            for (int t = 0; t < triCount; t++)
            {
                st.alive[t] = true;
                for (int k = 0; k < 3; k++) st.pointTris[st.pointOf[st.tris[t * 3 + k]]].Add(t);
            }

            st.locked = new bool[P];
            st.removed = new bool[P];
            st.penalty = new float[P];
            for (int p = 0; p < P; p++) st.penalty[p] = 1f;
            var deltas = new Vector3[n];
            for (int s = 0; s < src.blendShapeCount; s++)
            {
                bool prot = opt.protectedShapes.Contains(src.GetBlendShapeName(s));
                src.GetBlendShapeFrameVertices(s, src.GetBlendShapeFrameCount(s) - 1, deltas, null, null);
                for (int v = 0; v < n; v++)
                {
                    if (deltas[v].sqrMagnitude < 1e-10f) continue;
                    int p = st.pointOf[v];
                    if (prot) st.locked[p] = true;
                    else st.penalty[p] = Mathf.Max(st.penalty[p], 8f);
                }
            }

            st.Q = new double[P * 10];
            st.wsum = new double[P];
            double diag = src.bounds.size.magnitude * opt.maxError;
            st.maxErrorSq = diag * diag;
            var edgeTris = new Dictionary<long, int>();
            var edgeTri = new Dictionary<long, int>();
            for (int t = 0; t < triCount; t++)
            {
                int a = st.pointOf[st.tris[t * 3]], b = st.pointOf[st.tris[t * 3 + 1]], c = st.pointOf[st.tris[t * 3 + 2]];
                var nrm = Vector3.Cross(st.point[b] - st.point[a], st.point[c] - st.point[a]);
                double area = nrm.magnitude * 0.5;
                if (area < 1e-14) continue;
                nrm.Normalize();
                AddPlane(st, a, nrm, st.point[a], area);
                AddPlane(st, b, nrm, st.point[a], area);
                AddPlane(st, c, nrm, st.point[a], area);
                int[] pts = { a, b, c };
                for (int k = 0; k < 3; k++)
                {
                    long e = EdgeKey(pts[k], pts[(k + 1) % 3]);
                    int cnt;
                    edgeTris.TryGetValue(e, out cnt);
                    edgeTris[e] = cnt + 1;
                    edgeTri[e] = t;
                }
            }
            // Open edges: a plane through the edge, perpendicular to its triangle, holds the outline.
            foreach (var kv in edgeTris)
            {
                if (kv.Value != 1) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                int t = edgeTri[kv.Key];
                Vector3 t0 = st.point[st.pointOf[st.tris[t * 3]]], t1 = st.point[st.pointOf[st.tris[t * 3 + 1]]], t2 = st.point[st.pointOf[st.tris[t * 3 + 2]]];
                var tn = Vector3.Cross(t1 - t0, t2 - t0).normalized;
                var edge = st.point[b] - st.point[a];
                var bn = Vector3.Cross(edge, tn).normalized;
                if (bn.sqrMagnitude < 0.5f) continue;
                double w = edge.sqrMagnitude * 10.0;
                AddPlane(st, a, bn, st.point[a], w);
                AddPlane(st, b, bn, st.point[a], w);
            }

            st.stamp = new int[P];
            var heap = new Heap();
            for (int p = 0; p < P; p++) Push(heap, st, p);

            int live = triCount;
            long steps = 0, maxSteps = (long)P * 20;
            var deadline = System.DateTime.Now.AddSeconds(60);
            var wedgeMap = new Dictionary<int, int>();
            while (live > target && heap.Count > 0)
            {
                // Safety net: never let a bad mesh hang the editor.
                if (++steps > maxSteps || ((steps & 1023) == 0 && System.DateTime.Now > deadline)) break;
                var c = heap.Pop();
                int p = c.p, q = c.q;
                if (st.removed[p] || st.removed[q] || c.stamp != st.stamp[p]) continue;
                // Re-queued only when its neighbourhood changes; retrying now would pick the same target forever.
                if (!Valid(st, p, q, wedgeMap)) { st.stamp[p]++; continue; }

                foreach (var t in st.pointTris[p])
                {
                    if (!st.alive[t]) continue;
                    bool hasQ = false;
                    for (int k = 0; k < 3; k++) if (st.pointOf[st.tris[t * 3 + k]] == q) hasQ = true;
                    if (hasQ) { st.alive[t] = false; live--; continue; }
                    for (int k = 0; k < 3; k++)
                    {
                        int v = st.tris[t * 3 + k];
                        if (st.pointOf[v] == p) st.tris[t * 3 + k] = wedgeMap[v];
                    }
                    st.pointTris[q].Add(t);
                }
                st.removed[p] = true;
                for (int i = 0; i < 10; i++) st.Q[q * 10 + i] += st.Q[p * 10 + i];
                st.wsum[q] += st.wsum[p];
                st.penalty[q] = Mathf.Max(st.penalty[q], st.penalty[p]);

                var touched = new HashSet<int> { q };
                foreach (var t in st.pointTris[q])
                    if (st.alive[t]) for (int k = 0; k < 3; k++) touched.Add(st.pointOf[st.tris[t * 3 + k]]);
                foreach (var w in touched)
                {
                    st.stamp[w]++;
                    Push(heap, st, w);
                }
            }
            trisAfter = live;

            var used = new bool[n];
            var subs = new List<List<int>>();
            for (int s = 0; s < src.subMeshCount; s++) subs.Add(new List<int>());
            for (int t = 0; t < triCount; t++)
            {
                if (!st.alive[t]) continue;
                for (int k = 0; k < 3; k++) { subs[triSub[t]].Add(st.tris[t * 3 + k]); used[st.tris[t * 3 + k]] = true; }
            }
            foreach (var kv in otherSubs) { subs[kv.Key].AddRange(kv.Value); foreach (var i in kv.Value) used[i] = true; }
            return st.d.Build(src.name, subs, topo, used);
        }

        static void AddPlane(State st, int p, Vector3 n, Vector3 on, double w)
        {
            var Q = st.Q;
            st.wsum[p] += w;
            double a = n.x, b = n.y, c = n.z, d = -Vector3.Dot(n, on);
            int o = p * 10;
            Q[o] += w * a * a; Q[o + 1] += w * a * b; Q[o + 2] += w * a * c; Q[o + 3] += w * a * d;
            Q[o + 4] += w * b * b; Q[o + 5] += w * b * c; Q[o + 6] += w * b * d;
            Q[o + 7] += w * c * c; Q[o + 8] += w * c * d; Q[o + 9] += w * d * d;
        }

        static Vector3Int Quantize(Vector3 p)
        {
            return new Vector3Int(Mathf.RoundToInt(p.x * 1e5f), Mathf.RoundToInt(p.y * 1e5f), Mathf.RoundToInt(p.z * 1e5f));
        }

        static long EdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        static void Push(Heap heap, State st, int p)
        {
            if (st.locked[p] || st.removed[p]) return;
            double best = double.MaxValue;
            int bestQ = -1;
            var seen = new HashSet<int>();
            foreach (var t in st.pointTris[p])
            {
                if (!st.alive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int q = st.pointOf[st.tris[t * 3 + k]];
                    if (q == p || st.removed[q] || !seen.Add(q)) continue;
                    // Mean squared distance to the original planes; over the ceiling the collapse is never made.
                    double meanSq = Error(st.Q, p, q, st.point[q]) / System.Math.Max(1e-20, st.wsum[p] + st.wsum[q]);
                    if (meanSq > st.maxErrorSq) continue;
                    double cost = meanSq + 1e-14;
                    cost *= st.penalty[p] * (1.0 + 4.0 * WeightDiff(st.d, st.pointVerts[p][0], st.pointVerts[q][0]));
                    if (cost < best) { best = cost; bestQ = q; }
                }
            }
            if (bestQ >= 0) heap.Push(new Candidate { cost = best, p = p, q = bestQ, stamp = st.stamp[p] });
        }

        static double Error(double[] Q, int u, int v, Vector3 p)
        {
            double x = p.x, y = p.y, z = p.z;
            int a = u * 10, b = v * 10;
            double q0 = Q[a] + Q[b], q1 = Q[a + 1] + Q[b + 1], q2 = Q[a + 2] + Q[b + 2], q3 = Q[a + 3] + Q[b + 3];
            double q4 = Q[a + 4] + Q[b + 4], q5 = Q[a + 5] + Q[b + 5], q6 = Q[a + 6] + Q[b + 6];
            double q7 = Q[a + 7] + Q[b + 7], q8 = Q[a + 8] + Q[b + 8], q9 = Q[a + 9] + Q[b + 9];
            return q0 * x * x + 2 * q1 * x * y + 2 * q2 * x * z + 2 * q3 * x
                 + q4 * y * y + 2 * q5 * y * z + 2 * q6 * y
                 + q7 * z * z + 2 * q8 * z + q9;
        }

        // 0 when both vertices follow the same bones the same way, up to 2 when they share none.
        static float WeightDiff(MeshData d, int u, int v)
        {
            if (d.bw.Count <= Mathf.Max(u, v)) return 0;
            var a = d.bw[u]; var b = d.bw[v];
            var wa = new Dictionary<int, float>();
            Add(wa, a.boneIndex0, a.weight0); Add(wa, a.boneIndex1, a.weight1); Add(wa, a.boneIndex2, a.weight2); Add(wa, a.boneIndex3, a.weight3);
            Add(wa, b.boneIndex0, -b.weight0); Add(wa, b.boneIndex1, -b.weight1); Add(wa, b.boneIndex2, -b.weight2); Add(wa, b.boneIndex3, -b.weight3);
            float sum = 0;
            foreach (var w in wa.Values) sum += Mathf.Abs(w);
            return sum;
        }

        static void Add(Dictionary<int, float> d, int i, float w)
        {
            if (w == 0) return;
            float c;
            d.TryGetValue(i, out c);
            d[i] = c + w;
        }

        // Each vertex copy at p must pair with a copy at q through a triangle that is about to
        // disappear (same UV island); otherwise the collapse would tear the texture. The pairs go
        // into wedgeMap. Also rejects collapses that flip or squash a neighbouring triangle.
        static bool Valid(State st, int p, int q, Dictionary<int, int> wedgeMap)
        {
            wedgeMap.Clear();
            foreach (var t in st.pointTris[p])
            {
                if (!st.alive[t]) continue;
                int vp = -1, vq = -1;
                for (int k = 0; k < 3; k++)
                {
                    int v = st.tris[t * 3 + k];
                    if (st.pointOf[v] == p) vp = v;
                    else if (st.pointOf[v] == q) vq = v;
                }
                if (vp >= 0 && vq >= 0 && !wedgeMap.ContainsKey(vp)) wedgeMap[vp] = vq;
            }
            var pos = st.point;
            foreach (var t in st.pointTris[p])
            {
                if (!st.alive[t]) continue;
                int i0 = st.pointOf[st.tris[t * 3]], i1 = st.pointOf[st.tris[t * 3 + 1]], i2 = st.pointOf[st.tris[t * 3 + 2]];
                if (i0 == q || i1 == q || i2 == q) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = st.tris[t * 3 + k];
                    if (st.pointOf[v] == p && !wedgeMap.ContainsKey(v)) return false;
                }
                var before = Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]);
                Vector3 p0 = pos[i0 == p ? q : i0], p1 = pos[i1 == p ? q : i1], p2 = pos[i2 == p ? q : i2];
                var after = Vector3.Cross(p1 - p0, p2 - p0);
                if (after.sqrMagnitude < 1e-16f) return false;
                if (Vector3.Dot(before.normalized, after.normalized) < 0.3f) return false;
            }
            return true;
        }

        class Heap
        {
            readonly List<Candidate> items = new List<Candidate>();
            public int Count { get { return items.Count; } }

            public void Push(Candidate c)
            {
                items.Add(c);
                int i = items.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (items[p].cost <= items[i].cost) break;
                    var tmp = items[p]; items[p] = items[i]; items[i] = tmp;
                    i = p;
                }
            }

            public Candidate Pop()
            {
                var top = items[0];
                var last = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (items.Count > 0)
                {
                    items[0] = last;
                    int i = 0;
                    while (true)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        if (l < items.Count && items[l].cost < items[m].cost) m = l;
                        if (r < items.Count && items[r].cost < items[m].cost) m = r;
                        if (m == i) break;
                        var tmp = items[m]; items[m] = items[i]; items[i] = tmp;
                        i = m;
                    }
                }
                return top;
            }
        }
    }
}
