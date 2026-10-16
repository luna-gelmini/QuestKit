using System.IO;
using UnityEditor;
using UnityEngine;

namespace QuestKit
{
    // Carries lilToon features over to Toon Standard: matcap, rim light and normal maps map to
    // their Toon Standard versions, and extra main texture layers (decals, overlays) are baked into
    // the base texture.
    public static class LilLooks
    {
        public static bool On(Material m, string toggle)
        {
            return m.HasProperty(toggle) && m.GetFloat(toggle) > 0.5f;
        }

        public static void Apply(Material lil, Material q)
        {
            if (On(lil, "_UseMatCap") && lil.GetTexture("_MatCapTex") != null)
            {
                q.SetTexture("_Matcap", lil.GetTexture("_MatCapTex"));
                var tint = lil.HasProperty("_MatCapColor") ? lil.GetColor("_MatCapColor") : Color.white;
                float blend = lil.HasProperty("_MatCapBlend") ? lil.GetFloat("_MatCapBlend") : 1f;
                q.SetFloat("_MatcapStrength", Mathf.Clamp01(blend * tint.a));
                int mode = lil.HasProperty("_MatCapBlendMode") ? (int)lil.GetFloat("_MatCapBlendMode") : 1;
                q.SetFloat("_MatcapType", mode == 3 ? 1 : 0);
                if (lil.HasProperty("_MatCapBlendMask") && lil.GetTexture("_MatCapBlendMask") != null)
                    q.SetTexture("_MatcapMask", lil.GetTexture("_MatCapBlendMask"));
                q.EnableKeyword("USE_MATCAP");
            }

            if (On(lil, "_UseRim"))
            {
                var rim = lil.HasProperty("_RimColor") ? lil.GetColor("_RimColor") : Color.white;
                q.SetColor("_RimColor", new Color(rim.r, rim.g, rim.b, 1));
                q.SetFloat("_RimIntensity", Mathf.Clamp01(rim.a));
                if (lil.HasProperty("_RimBorder")) q.SetFloat("_RimRange", Mathf.Clamp01(1f - lil.GetFloat("_RimBorder")));
                if (lil.HasProperty("_RimBlur")) q.SetFloat("_RimSharpness", Mathf.Clamp01(1f - lil.GetFloat("_RimBlur")));
                q.EnableKeyword("USE_RIMLIGHT");
            }

            if (On(lil, "_UseBumpMap") && lil.GetTexture("_BumpMap") != null)
            {
                q.SetTexture("_BumpMap", lil.GetTexture("_BumpMap"));
                if (lil.HasProperty("_BumpScale")) q.SetFloat("_BumpScale", lil.GetFloat("_BumpScale"));
                q.EnableKeyword("USE_NORMAL_MAPS");
            }
        }

        // Returns a new texture with lilToon's 2nd and 3rd main layers composited on top, or null
        // when there is nothing that can be baked (no layers, or layers that use their own UVs).
        public static Texture BakeLayers(Material m, Texture main, string folder, int size)
        {
            if (main == null) return null;
            var layers = new[] { "2nd", "3rd" };
            bool any = false;
            foreach (var l in layers) any |= Bakeable(m, l);
            if (!any) return null;

            int w, h;
            var px = TextureUtil.ReadSource(main, out w, out h);
            foreach (var l in layers)
            {
                if (!Bakeable(m, l)) continue;
                var tex = m.GetTexture("_Main" + l + "Tex");
                var color = m.GetColor("_Color" + l);
                int mode = m.HasProperty("_Main" + l + "TexBlendMode") ? (int)m.GetFloat("_Main" + l + "TexBlendMode") : 0;
                var st = m.GetVector("_Main" + l + "Tex_ST");
                int lw = 1, lh = 1;
                Color[] lp = tex != null ? TextureUtil.Pixels(tex, Mathf.Max(w, h), out lw, out lh) : new[] { Color.white };
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float u = Frac((x + 0.5f) / w * st.x + st.z), v = Frac((y + 0.5f) / h * st.y + st.w);
                        var c = lp[Mathf.Min(lh - 1, (int)(v * lh)) * lw + Mathf.Min(lw - 1, (int)(u * lw))] * color;
                        var b = px[y * w + x];
                        Color r;
                        switch (mode)
                        {
                            case 1: r = b + c; break;
                            case 2: r = Color.white - (Color.white - b) * (Color.white - c); break;
                            case 3: r = b * c; break;
                            default: r = c; break;
                        }
                        float a = c.a;
                        px[y * w + x] = new Color(Mathf.Lerp(b.r, r.r, a), Mathf.Lerp(b.g, r.g, a), Mathf.Lerp(b.b, r.b, a), b.a);
                    }
            }
            var path = folder + "/" + QuestBuilder.Safe(m.name) + "_Layers.png";
            var baked = TextureUtil.WritePng(px, w, h, path);
            TextureUtil.SetAndroidSize(baked, size);
            return baked;
        }

        static float Frac(float f)
        {
            return f - Mathf.Floor(f);
        }

        static bool Bakeable(Material m, string l)
        {
            if (!On(m, "_UseMain" + l + "Tex")) return false;
            if (m.HasProperty("_Main" + l + "TexIsDecal") && m.GetFloat("_Main" + l + "TexIsDecal") > 0.5f) return false;
            if (m.HasProperty("_Main" + l + "Tex_UVMode") && m.GetFloat("_Main" + l + "Tex_UVMode") > 0.5f) return false;
            if (m.HasProperty("_Main" + l + "TexAngle") && Mathf.Abs(m.GetFloat("_Main" + l + "TexAngle")) > 0.001f) return false;
            return m.HasProperty("_Main" + l + "Tex") && m.HasProperty("_Color" + l);
        }
    }
}
