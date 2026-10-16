using System.IO;
using UnityEditor;
using UnityEngine;

namespace QuestKit
{
    public static class TextureUtil
    {
        // Reads any texture (compressed or not readable) through the GPU, in sRGB.
        public static Color[] Read(Texture tex, int maxSize, out int w, out int h)
        {
            float s = maxSize > 0 ? Mathf.Min(1f, maxSize / (float)Mathf.Max(tex.width, tex.height)) : 1f;
            w = Mathf.Max(1, Mathf.RoundToInt(tex.width * s));
            h = Mathf.Max(1, Mathf.RoundToInt(tex.height * s));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t2 = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            t2.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t2.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = t2.GetPixels();
            Object.DestroyImmediate(t2);
            return px;
        }

        // The imported size depends on the active platform, so ask the importer for the PC size.
        public static int SourceSize(Texture tex)
        {
            var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (ti == null) return Mathf.Max(tex.width, tex.height);
            int w, h;
            ti.GetSourceTextureWidthAndHeight(out w, out h);
            int max = ti.GetPlatformTextureSettings("Standalone").overridden ? ti.GetPlatformTextureSettings("Standalone").maxTextureSize : ti.maxTextureSize;
            return Mathf.Min(Mathf.Max(w, h), max);
        }

        // Full-resolution pixels from the source file when it's a PNG or JPG, else from the GPU copy.
        public static Color[] ReadSource(Texture tex, out int w, out int h)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (t.LoadImage(File.ReadAllBytes(path)))
                {
                    int max = SourceSize(tex);
                    if (Mathf.Max(t.width, t.height) <= max)
                    {
                        w = t.width; h = t.height;
                        var px = t.GetPixels();
                        Object.DestroyImmediate(t);
                        return px;
                    }
                    var scaled = Read(t, max, out w, out h);
                    Object.DestroyImmediate(t);
                    return scaled;
                }
                Object.DestroyImmediate(t);
            }
            return Read(tex, 0, out w, out h);
        }

        // Source pixels scaled so the longest side is at most maxSide.
        public static Color[] Pixels(Texture tex, int maxSide, out int w, out int h)
        {
            var px = ReadSource(tex, out w, out h);
            if (Mathf.Max(w, h) <= maxSide) return px;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            t.SetPixels(px);
            t.Apply();
            var scaled = Read(t, maxSide, out w, out h);
            Object.DestroyImmediate(t);
            return scaled;
        }

        public static Texture2D WritePng(Color[] px, int w, int h, string assetPath)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            t.SetPixels(px);
            t.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            File.WriteAllBytes(assetPath, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(assetPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // Quest-only size: the PC build keeps the original texture.
        public static void SetAndroidSize(Texture tex, int size)
        {
            var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (ti == null) return;
            var ps = ti.GetPlatformTextureSettings("Android");
            if (ps.overridden && ps.maxTextureSize == size && ps.format == TextureImporterFormat.ASTC_6x6) return;
            ps.overridden = true;
            ps.maxTextureSize = size;
            ps.format = TextureImporterFormat.ASTC_6x6;
            ti.SetPlatformTextureSettings(ps);
            ti.SaveAndReimport();
        }
    }
}
