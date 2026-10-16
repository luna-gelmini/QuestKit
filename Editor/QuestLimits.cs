namespace QuestKit
{
    public enum Rank { Excellent, Good, Medium, Poor, VeryPoor }

    public enum Stat
    {
        Triangles, SkinnedMeshes, Meshes, MaterialSlots, Bones,
        PhysBones, PhysBoneTransforms, PhysBoneColliders, CollisionChecks,
        Contacts, Constraints, ParticleSystems, TextureMemoryMB, DownloadMB
    }

    // Android limits from the VRChat performance rank docs. Excellent, Good, Medium, Poor.
    public static class QuestLimits
    {
        static readonly float[][] Table =
        {
            new float[] { 7500, 10000, 15000, 20000 },
            new float[] { 1, 1, 2, 2 },
            new float[] { 1, 1, 2, 2 },
            new float[] { 1, 1, 2, 4 },
            new float[] { 75, 90, 150, 150 },
            new float[] { 0, 4, 6, 8 },
            new float[] { 0, 16, 32, 64 },
            new float[] { 0, 4, 8, 16 },
            new float[] { 0, 16, 32, 64 },
            new float[] { 2, 4, 8, 16 },
            new float[] { 30, 60, 120, 150 },
            new float[] { 0, 0, 0, 2 },
            new float[] { 10, 18, 25, 40 },
            new float[] { 10, 10, 10, 10 },
        };

        public const float DownloadCapMB = 10f;

        public static readonly string[] StatNames =
        {
            "Triangles", "Skinned meshes", "Meshes", "Material slots", "Bones",
            "PhysBones", "PhysBone transforms", "PhysBone colliders", "Collision checks",
            "Contacts", "Constraints", "Particle systems", "Texture memory (MB)", "Download size (MB)"
        };

        // Over the Poor limit VRChat drops every one of these on Quest, even on a Very Poor avatar.
        public static bool IsHardCap(Stat s)
        {
            return s == Stat.PhysBones || s == Stat.PhysBoneTransforms || s == Stat.PhysBoneColliders
                || s == Stat.CollisionChecks || s == Stat.Contacts || s == Stat.DownloadMB;
        }

        public static float Limit(Stat s, Rank r)
        {
            if (r == Rank.VeryPoor) return IsHardCap(s) ? Table[(int)s][3] : float.PositiveInfinity;
            return Table[(int)s][(int)r];
        }

        public static Rank RankOf(Stat s, float value)
        {
            for (int r = 0; r < 4; r++)
                if (value <= Table[(int)s][r]) return (Rank)r;
            return Rank.VeryPoor;
        }

        public static string RankName(Rank r)
        {
            return r == Rank.VeryPoor ? "Very Poor" : r.ToString();
        }
    }
}
