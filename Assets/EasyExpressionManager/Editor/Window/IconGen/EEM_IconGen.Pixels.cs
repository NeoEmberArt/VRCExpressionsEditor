// Easy Expression Manager: icon transparency, cropping, resampling and outlines.

using System;
using UnityEngine;

internal static class EEMIconPixels
{

    // ── pixels ────────────────────────────────────────────────────────────────

    internal static Texture2D Compose(IconGenSettings s, Color[] onBlack, Color[] onWhite, int R, int outSize)
    {
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        Color W(Color c) => linear ? c.linear : c;          // settings colours → working space
        int N = R * R;

        // premultiplied colour + coverage
        var P = new Color[N];
        var A = new float[N];
        int minX = R, minY = R, maxX = -1, maxY = -1;
        var tint = W(s.tint);
        bool outlineOnly = s.style == IconStyle.OutlineOnly;
        for (int i = 0; i < N; i++)
        {
            var b = onBlack[i]; var w = onWhite[i];
            float a = Mathf.Clamp01(1f - ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3f);
            A[i] = a;
            if (a <= 0f) { P[i] = default; continue; }

            Color c = new Color(Mathf.Clamp(b.r, 0f, a), Mathf.Clamp(b.g, 0f, a), Mathf.Clamp(b.b, 0f, a), a);
            if (s.style == IconStyle.Silhouette)
                c = new Color(tint.r * a, tint.g * a, tint.b * a, a);
            else if (s.style == IconStyle.Shaded)
            {
                float lum   = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / a;
                float shade = Mathf.Lerp(0.35f, 1.1f, Mathf.Clamp01(linear ? Mathf.LinearToGammaSpace(lum) : lum));
                c = new Color(Mathf.Min(1f, tint.r * shade) * a, Mathf.Min(1f, tint.g * shade) * a,
                              Mathf.Min(1f, tint.b * shade) * a, a);
            }
            // Keep coverage for cropping and outline generation, but omit the
            // item's fill when the user asks for an outline-only icon.
            P[i] = outlineOnly ? default : c;

            if (a > 0.02f)
            {
                int x = i % R, y = i / R;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return null;   // nothing visible

        // crop to the visible pixels and fit them centred into the square
        float outlineFraction = s.OutlineFraction;
        // Outline-only should be useful with the default "No outline" setting.
        // Use a small edge in that case without changing the preference.
        if (outlineOnly && outlineFraction <= 0f) outlineFraction = 0.012f;
        float pad   = Mathf.Clamp(s.padding + outlineFraction, 0f, 0.45f);
        float bw    = maxX - minX + 1, bh = maxY - minY + 1;
        float side  = Mathf.Max(1f, Mathf.Max(bw, bh) / (1f - 2f * pad));
        float cx    = (minX + maxX + 1) * 0.5f, cy = (minY + maxY + 1) * 0.5f;
        float scale = side / outSize;
        int   k     = Mathf.Clamp(Mathf.CeilToInt(scale), 1, 4);
        float x0    = cx - side * 0.5f, y0 = cy - side * 0.5f;

        int M = outSize * outSize;
        var OP = new Color[M];
        var OA = new float[M];
        float inv = 1f / (k * k);
        for (int oy = 0; oy < outSize; oy++)
        for (int ox = 0; ox < outSize; ox++)
        {
            Color acc = default; float aa = 0f;
            for (int j = 0; j < k; j++)
            for (int i = 0; i < k; i++)
            {
                float sx = x0 + (ox + (i + 0.5f) / k) * scale - 0.5f;
                float sy = y0 + (oy + (j + 0.5f) / k) * scale - 0.5f;
                Bilinear(P, A, R, sx, sy, ref acc, ref aa);
            }
            int o = oy * outSize + ox;
            OP[o] = acc * inv;
            OA[o] = aa * inv;
        }

        // outline under the item
        float T = outlineFraction * outSize;
        if (T > 0f)
        {
            var inside = new bool[M];
            for (int i = 0; i < M; i++) inside[i] = OA[i] >= 0.5f;
            var d2 = SquaredDistance(inside, outSize);
            var oc = W(s.outlineColor);
            for (int i = 0; i < M; i++)
            {
                float o = outlineOnly
                    ? (inside[i] ? 0f : Mathf.Clamp01(T - Mathf.Sqrt(d2[i]) + 0.5f))
                    : (inside[i] ? 1f : Mathf.Clamp01(T - Mathf.Sqrt(d2[i]) + 0.5f));
                if (outlineOnly) OA[i] = 0f;
                if (o <= 0f) continue;
                // Outline-only uses the coverage above only as a mask for
                // finding the edge. Do not retain the item's alpha, or the
                // zero RGB fill would become black when un-premultiplied.
                float a = outlineOnly ? 0f : OA[i];
                OP[i] = new Color(OP[i].r + oc.r * o * (1f - a), OP[i].g + oc.g * o * (1f - a),
                                  OP[i].b + oc.b * o * (1f - a), 0f);
                OA[i] = a + o * (1f - a);
            }
        }

        // background colour
        if (s.background == IconBackground.Colour)
        {
            var bg = W(s.bgColor);
            for (int i = 0; i < M; i++)
            {
                float a = OA[i];
                OP[i] = new Color(OP[i].r + bg.r * (1f - a), OP[i].g + bg.g * (1f - a), OP[i].b + bg.b * (1f - a), 0f);
                OA[i] = 1f;
            }
        }

        // un-premultiply, back to sRGB for the PNG
        var px = new Color32[M];
        for (int i = 0; i < M; i++)
        {
            float a = Mathf.Clamp01(OA[i]);
            if (a <= 0f) { px[i] = new Color32(0, 0, 0, 0); continue; }
            float r = Mathf.Clamp01(OP[i].r / a), g = Mathf.Clamp01(OP[i].g / a), b = Mathf.Clamp01(OP[i].b / a);
            if (linear) { r = Mathf.LinearToGammaSpace(r); g = Mathf.LinearToGammaSpace(g); b = Mathf.LinearToGammaSpace(b); }
            px[i] = new Color32((byte)Mathf.RoundToInt(r * 255f), (byte)Mathf.RoundToInt(g * 255f),
                                (byte)Mathf.RoundToInt(b * 255f), (byte)Mathf.RoundToInt(a * 255f));
        }

        var tex = new Texture2D(outSize, outSize, TextureFormat.RGBA32, false, false)
                  { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    static void Bilinear(Color[] P, float[] A, int R, float x, float y, ref Color acc, ref float aa)
    {
        int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
        float fx = x - ix, fy = y - iy;
        for (int dy = 0; dy <= 1; dy++)
        for (int dx = 0; dx <= 1; dx++)
        {
            int px = ix + dx, py = iy + dy;
            if (px < 0 || py < 0 || px >= R || py >= R) continue;   // outside = transparent
            float w = (dx == 0 ? 1f - fx : fx) * (dy == 0 ? 1f - fy : fy);
            int i = py * R + px;
            acc += P[i] * w;
            aa  += A[i] * w;
        }
    }

    /// Exact squared distance to the nearest `inside` pixel (Felzenszwalb &
    /// Huttenlocher: two 1-D passes, linear time).
    static float[] SquaredDistance(bool[] inside, int n)
    {
        const float INF = 1e20f;
        var f = new float[n * n];
        for (int i = 0; i < f.Length; i++) f[i] = inside[i] ? 0f : INF;
        var line = new float[n]; var outp = new float[n]; var v = new int[n]; var z = new float[n + 1];
        for (int x = 0; x < n; x++)
        {
            for (int y = 0; y < n; y++) line[y] = f[y * n + x];
            Dt1D(line, n, outp, v, z);
            for (int y = 0; y < n; y++) f[y * n + x] = outp[y];
        }
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++) line[x] = f[y * n + x];
            Dt1D(line, n, outp, v, z);
            for (int x = 0; x < n; x++) f[y * n + x] = outp[x];
        }
        return f;
    }

    static void Dt1D(float[] f, int n, float[] d, int[] v, float[] z)
    {
        const float INF = 1e20f;
        int k = 0; v[0] = 0; z[0] = -INF; z[1] = INF;
        for (int q = 1; q < n; q++)
        {
            float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
            }
            k++; v[k] = q; z[k] = s; z[k + 1] = INF;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

}
