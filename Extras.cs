// Extras.cs — v1.06 新增功能（胶片、抖动、线性 LUT、元数据、预设、外部脚本调用）
// 只被 WebViewApp.cs 调用，不修改原有流水线语义。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ---------------- 胶片质感：颗粒 / 漏光 / halation ----------------
static class FilmFx
{
    // grain: 0-100 颗粒强度；leak: 0-100 漏光；halo: 0-100 高光泛光
    public static void Apply(Bitmap bmp, double grain, double leak, double halo)
    {
        if (grain <= 0 && leak <= 0 && halo <= 0) return;
        int W = bmp.Width, H = bmp.Height;
        var rect = new Rectangle(0, 0, W, H);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] buf = new byte[stride * H];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        try
        {
            float[] glow = null;
            if (halo > 0) glow = BuildGlow(buf, W, H, stride);

            double gAmt = grain / 100.0;
            double lAmt = leak / 100.0;
            double hAmt = halo / 100.0;
            double cx = W * 0.5, cy = H * 0.5;
            double maxD = Math.Sqrt(cx * cx + cy * cy);

            int rnd = new Random(20261117).Next();
            for (int y = 0; y < H; y++)
            {
                int row = y * stride;
                double dy = y - cy;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x * 4;
                    double b = buf[i], g = buf[i + 1], r = buf[i + 2];

                    // halation：高光扩散叠加（模拟胶片乳剂层光晕）
                    if (glow != null)
                    {
                        double gv = glow[y * W + x];
                        r += gv * 0.55 * hAmt; g += gv * 0.32 * hAmt; b += gv * 0.18 * hAmt;
                    }

                    // 漏光：自左上角斜向渐变的暖色叠加
                    if (lAmt > 0)
                    {
                        double dx = x - cx;
                        double d = Math.Sqrt(dx * dx + dy * dy) / maxD;
                        double t = 1.0 - d;
                        if (t > 0)
                        {
                            double fall = t * t * lAmt;
                            r += 92 * fall; g += 34 * fall; b += 6 * fall;
                        }
                    }

                    // 颗粒：亮度越低颗粒越明显（胶片特性），带通道轻微差异
                    if (gAmt > 0)
                    {
                        double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                        double sens = 0.35 + 0.65 * (1.0 - lum);
                        rnd = rnd * 1103515245 + 12345;
                        double n1 = ((rnd >> 8) & 0xFFFF) / 65535.0 - 0.5;
                        rnd = rnd * 1103515245 + 12345;
                        double n2 = ((rnd >> 8) & 0xFFFF) / 65535.0 - 0.5;
                        rnd = rnd * 1103515245 + 12345;
                        double n3 = ((rnd >> 8) & 0xFFFF) / 65535.0 - 0.5;
                        double amp = 18.0 * gAmt * sens;
                        r += n1 * amp; g += n2 * amp; b += n3 * amp;
                    }

                    buf[i] = Clamp(b); buf[i + 1] = Clamp(g); buf[i + 2] = Clamp(r);
                }
            }
        }
        finally
        {
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            bmp.UnlockBits(data);
        }
    }

    static byte Clamp(double v) { return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)(v + 0.5); }

    // 高光掩码 -> 缩小模糊 -> 放大，得到发光层
    static float[] BuildGlow(byte[] buf, int W, int H, int stride)
    {
        int sw = Math.Max(4, W / 8), sh = Math.Max(4, H / 8);
        float[] small = new float[sw * sh];
        for (int y = 0; y < sh; y++)
        {
            int sy = y * H / sh;
            int row = sy * stride;
            for (int x = 0; x < sw; x++)
            {
                int sx = x * W / sw;
                int i = row + sx * 4;
                double r = buf[i + 2], g = buf[i + 1], b = buf[i];
                double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                double over = lum - 0.72;
                small[y * sw + x] = over > 0 ? (float)(over * over * 255.0 * 1.4) : 0f;
            }
        }
        // 两次 3x3 盒式模糊
        for (int pass = 0; pass < 2; pass++)
        {
            float[] tmp = new float[sw * sh];
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    float sum = 0; int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int ny = y + dy, nx = x + dx;
                            if (ny < 0 || ny >= sh || nx < 0 || nx >= sw) continue;
                            sum += small[ny * sw + nx]; n++;
                        }
                    tmp[y * sw + x] = sum / n;
                }
            small = tmp;
        }
        float[] full = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double fx = (double)x * (sw - 1) / Math.Max(1, W - 1);
                double fy = (double)y * (sh - 1) / Math.Max(1, H - 1);
                int x0 = (int)fx, y0 = (int)fy;
                int x1 = Math.Min(sw - 1, x0 + 1), y1 = Math.Min(sh - 1, y0 + 1);
                double tx = fx - x0, ty = fy - y0;
                double v = small[y0 * sw + x0] * (1 - tx) * (1 - ty) + small[y0 * sw + x1] * tx * (1 - ty)
                         + small[y1 * sw + x0] * (1 - tx) * ty + small[y1 * sw + x1] * tx * ty;
                full[y * W + x] = (float)v;
            }
        return full;
    }
}

// ---------------- 抖动（消除 8bit 渐变断层） ----------------
static class DitherFx
{
    static readonly int[,] Bayer = {
        { 0, 32, 8, 40, 2, 34, 10, 42 }, { 48, 16, 56, 24, 50, 18, 58, 26 },
        { 12, 44, 4, 36, 14, 46, 6, 38 }, { 60, 28, 52, 20, 62, 30, 54, 22 },
        { 3, 35, 11, 43, 1, 33, 9, 41 }, { 51, 19, 59, 27, 49, 17, 57, 25 },
        { 15, 47, 7, 39, 13, 45, 5, 37 }, { 63, 31, 55, 23, 61, 29, 53, 21 }
    };

    public static void Apply(Bitmap bmp)
    {
        int W = bmp.Width, H = bmp.Height;
        var rect = new Rectangle(0, 0, W, H);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] buf = new byte[stride * H];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        try
        {
            for (int y = 0; y < H; y++)
            {
                int row = y * stride;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x * 4;
                    double t = (Bayer[y & 7, x & 7] / 64.0 - 0.5) * 0.9;
                    for (int c = 0; c < 3; c++)
                    {
                        double v = buf[i + c] + t;
                        buf[i + c] = v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)(v + 0.5);
                    }
                }
            }
        }
        finally
        {
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            bmp.UnlockBits(data);
        }
    }
}

// ---------------- 线性色彩空间（LUT 输入/输出线性化） ----------------
static class LinSpace
{
    public static readonly float[] S2L = new float[256];
    static readonly byte[] LUT_IN = new byte[4096];   // 线性 0..1 -> 0..255（带抖动查表）
    static LinSpace()
    {
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            c = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            S2L[i] = (float)c;
        }
    }

    public static byte Encode(float linear)
    {
        if (linear <= 0) return 0;
        if (linear >= 1) return 255;
        double c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)(c * 255.0 + 0.5);
    }

    public static float EncodeF(float linear)
    {
        if (linear <= 0) return 0f;
        if (linear >= 1) return 1f;
        return (float)(linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055);
    }

    public static float DecodeF(float srgb)
    {
        if (srgb <= 0) return 0f;
        if (srgb >= 1) return 1f;
        return (float)(srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4));
    }

    // 输入侧：sRGB -> 线性（保留在 8bit 容器里，配合抖动减少断层）
    public static void ToLinear(byte[] buf, int W, int H, int stride)
    {
        for (int y = 0; y < H; y++)
        {
            int row = y * stride;
            for (int x = 0; x < W; x++)
            {
                int i = row + x * 4;
                double t = ((x + y) & 1) == 0 ? 0.35 : -0.35;
                for (int c = 0; c < 3; c++)
                {
                    double v = S2L[buf[i + c]] * 255.0 + t;
                    buf[i + c] = v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)(v + 0.5);
                }
            }
        }
    }

    // 输出侧：线性 -> sRGB + 有序抖动
    public static void FromLinear(byte[] buf, int W, int H, int stride)
    {
        var map = new byte[65536];
        for (int k = 0; k < 65536; k++) map[k] = Encode(k / 65535.0f);
        var bayer = new int[] { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        for (int y = 0; y < H; y++)
        {
            int row = y * stride;
            for (int x = 0; x < W; x++)
            {
                int i = row + x * 4;
                double t = (bayer[(y & 3) * 4 + (x & 3)] / 16.0 - 0.5);
                for (int c = 0; c < 3; c++)
                {
                    int idx = (int)((buf[i + c] / 255.0) * 65535.0 + t * 1.0);
                    if (idx < 0) idx = 0; else if (idx > 65535) idx = 65535;
                    buf[i + c] = map[idx];
                }
            }
        }
    }
}

// ---------------- 元数据（EXIF / ICC / 方向） ----------------
static class MetaWriter
{
    // 把源图 EXIF/ICC 拷到目标（尺寸相关标签跳过，方向归一）
    public static void CopyMeta(string srcPath, Bitmap dst)
    {
        if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return;
        try
        {
            using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var img = Image.FromStream(fs, false, false))
            {
                var skip = new HashSet<int> { 0x0112, 0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x011A, 0x011B, 0x0128, 0xA002, 0xA003 };
                foreach (int pid in img.PropertyIdList)
                {
                    if (skip.Contains(pid)) continue;
                    try
                    {
                        var prop = img.GetPropertyItem(pid);
                        dst.SetPropertyItem(prop);
                    }
                    catch { }
                }
            }
            // 方向按已旋转后的像素写 1
            try
            {
                var p = (PropertyItem)FormatterServicesGet();
                p.Id = 0x0112; p.Type = 3; p.Len = 2; p.Value = new byte[] { 1, 0 };
                dst.SetPropertyItem(p);
            }
            catch { }
        }
        catch { }
    }

    static object FormatterServicesGet()
    {
        // PropertyItem 无公开构造函数，用反射创建
        var ci = typeof(PropertyItem).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        if (ci != null) return ci.Invoke(null);
        return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PropertyItem));
    }
}

// ---------------- 预设管理 ----------------
static class Presets
{
    public static string Dir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "图片批处理工具", "presets");
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            return d;
        }
    }

    public static void Save(string name, Dictionary<string, double> par, string lutPath, string lutMode)
    {
        if (string.IsNullOrEmpty(name)) return;
        var sb = new StringBuilder();
        sb.Append("{\"lut\":\"").Append(lutPath == null ? "" : lutPath.Replace("\\", "\\\\")).Append("\",\"mode\":\"")
          .Append(lutMode).Append("\",\"v\":{");
        bool first = true;
        foreach (var kv in par)
        {
            if (!first) sb.Append(","); first = false;
            sb.Append("\"").Append(kv.Key).Append("\":").Append(kv.Value.ToString("0.###"));
        }
        sb.Append("}}");
        File.WriteAllText(Path.Combine(Dir, Sanitize(name) + ".json"), sb.ToString(), new UTF8Encoding(false));
        // 同时存 last，启动时恢复
        File.WriteAllText(Path.Combine(Dir, "_last.json"), sb.ToString(), new UTF8Encoding(false));
    }

    public static string Load(string name)
    {
        string f = Path.Combine(Dir, Sanitize(name) + ".json");
        if (!File.Exists(f)) return null;
        return File.ReadAllText(f, Encoding.UTF8);
    }

    public static string LoadLast()
    {
        string f = Path.Combine(Dir, "_last.json");
        return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : null;
    }

    public static string List()
    {
        var sb = new StringBuilder("[");
        var files = Directory.GetFiles(Dir, "*.json");
        Array.Sort(files);
        bool first = true;
        foreach (var f in files)
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (n == "_last") continue;
            if (!first) sb.Append(","); first = false;
            sb.Append("\"").Append(n.Replace("\"", "")).Append("\"");
        }
        sb.Append("]");
        return sb.ToString();
    }

    static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 64 ? s.Substring(0, 64) : s;
    }
}

// ---------------- 编辑器扩展：曲线 / HSL / 镜头校正 ----------------
static class EditorFx
{
    static double P(Dictionary<string, double> p, string k, double d)
    {
        double v; return p.TryGetValue(k, out v) ? v : d;
    }

    static byte Clamp(double v)
    {
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)(v + 0.5);
    }

    // 单调三次插值，避免普通三次样条在控制点附近过冲
    static void BuildCurve(Dictionary<string, double> p, string prefix, byte[] lut)
    {
        double[] x = { 0, 64, 128, 191, 255 };
        double[] y = new double[5];
        for (int i = 0; i < 5; i++) {
            double v = P(p, prefix + i, i * 25.0);
            y[i] = Math.Max(0, Math.Min(255, v * 2.55));
        }
        double[] d = new double[5];
        for (int i = 0; i < 5; i++) {
            double h0 = i > 0 ? x[i] - x[i - 1] : 1;
            double h1 = i < 4 ? x[i + 1] - x[i] : 1;
            double s0 = i > 0 ? (y[i] - y[i - 1]) / h0 : (y[1] - y[0]) / (x[1] - x[0]);
            double s1 = i < 4 ? (y[i + 1] - y[i]) / h1 : (y[4] - y[3]) / (x[4] - x[3]);
            d[i] = (s0 + s1) * 0.5;
        }
        for (int i = 0; i < 256; i++) {
            int seg = 0;
            while (seg < 4 && i > x[seg + 1]) seg++;
            double t = (i - x[seg]) / (x[seg + 1] - x[seg]);
            double t2 = t * t, t3 = t2 * t;
            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;
            double v = h00 * y[seg] + h10 * d[seg] * (x[seg + 1] - x[seg]) + h01 * y[seg + 1] + h11 * d[seg + 1] * (x[seg + 1] - x[seg]);
            lut[i] = Clamp(v);
        }
    }

    static double Hue2Rgb(double p, double q, double t)
    {
        if (t < 0) t += 1; if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        r /= 255.0; g /= 255.0; b /= 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double sum = max + min;
        l = sum * 0.5;
        if (max == min) { h = 0; s = 0; return; }
        double d = max - min;
        s = l > 0.5 ? d / (2.0 - sum) : d / sum;
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
        else if (max == g) h = ((b - r) / d + 2) / 6.0;
        else h = ((r - g) / d + 4) / 6.0;
    }

    static void HslToRgb(double hh, double ss, double ll, out double r, out double g, out double b)
    {
        if (ss <= 0) { r = g = b = ll * 255.0; return; }
        double q = ll < 0.5 ? ll * (1 + ss) : ll + ss - ll * ss;
        double pp = 2 * ll - q;
        r = Hue2Rgb(pp, q, hh + 1.0 / 3) * 255.0;
        g = Hue2Rgb(pp, q, hh) * 255.0;
        b = Hue2Rgb(pp, q, hh - 1.0 / 3) * 255.0;
    }

    static byte Sample(byte[] buf, int stride, int W, int H, double x, double y, int off)
    {
        if (x < 0) x = 0; if (y < 0) y = 0;
        if (x > W - 1) x = W - 1; if (y > H - 1) y = H - 1;
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(W - 1, x0 + 1), y1 = Math.Min(H - 1, y0 + 1);
        double tx = x - x0, ty = y - y0;
        double a = buf[y0 * stride + x0 * 4 + off], b = buf[y0 * stride + x1 * 4 + off];
        double c = buf[y1 * stride + x0 * 4 + off], d = buf[y1 * stride + x1 * 4 + off];
        return Clamp((a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty);
    }

    public static void Apply(Bitmap bmp, Dictionary<string, double> p)
    {
        bool curveOn = P(p, "curve_on", 0) > 0.5;
        bool hslOn = P(p, "hsl_on", 0) > 0.5;
        bool lensOn = P(p, "lens_on", 0) > 0.5;
        if (!curveOn && !hslOn && !lensOn) return;

        int W = bmp.Width, H = bmp.Height;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] buf = new byte[stride * H];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);

        byte[] cr = null, cg = null, cb = null;
        if (curveOn) {
            byte[] m = new byte[256], r = new byte[256], g = new byte[256], b = new byte[256];
            BuildCurve(p, "curve_m", m); BuildCurve(p, "curve_r", r); BuildCurve(p, "curve_g", g); BuildCurve(p, "curve_b", b);
            cr = new byte[256]; cg = new byte[256]; cb = new byte[256];
            for (int i = 0; i < 256; i++) { cr[i] = r[m[i]]; cg[i] = g[m[i]]; cb[i] = b[m[i]]; }
        }

        double[] hslS = new double[6], hslL = new double[6];
        if (hslOn) for (int i = 0; i < 6; i++) { hslS[i] = P(p, "hsl_s" + i, 0) / 100.0; hslL[i] = P(p, "hsl_l" + i, 0) / 100.0; }

        double lensK = P(p, "lens_dist", 0) / 100.0;
        double lensCa = P(p, "lens_ca", 0) / 100.0;
        double lensVig = P(p, "lens_vig", 0) / 100.0;

        byte[] srcCopy = null;
        if (lensOn) { srcCopy = new byte[buf.Length]; Buffer.BlockCopy(buf, 0, srcCopy, 0, buf.Length); }

        double cx = W * 0.5, cy = H * 0.5;
        double maxD = Math.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < H; y++) {
            int row = y * stride;
            for (int x = 0; x < W; x++) {
                int i = row + x * 4;

                if (lensOn) {
                    double nx = (x - cx) / cx, ny = (y - cy) / cy;
                    double r2 = nx * nx + ny * ny;
                    double k = 1.0 + lensK * r2 * 0.35;
                    double sx = cx + (x - cx) * k, sy = cy + (y - cy) * k;
                    if (lensCa > 0) {
                        double ca = 1.0 + lensCa * r2 * 0.012;
                        buf[i] = Sample(srcCopy, stride, W, H, cx + (sx - cx) / ca, cy + (sy - cy) / ca, 0);
                        buf[i + 1] = Sample(srcCopy, stride, W, H, sx, sy, 1);
                        buf[i + 2] = Sample(srcCopy, stride, W, H, cx + (sx - cx) * ca, cy + (sy - cy) * ca, 2);
                    } else {
                        double dx = sx, dy = sy;
                        double nx2 = (x - cx) / cx, ny2 = (y - cy) / cy;
                        buf[i] = Sample(srcCopy, stride, W, H, dx, dy, 0);
                        buf[i + 1] = Sample(srcCopy, stride, W, H, dx, dy, 1);
                        buf[i + 2] = Sample(srcCopy, stride, W, H, dx, dy, 2);
                    }
                    if (lensVig != 0) {
                        double rad = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD;
                        double vig = 1.0 + lensVig * (rad * rad) * 0.55;
                        buf[i] = Clamp(buf[i] * vig); buf[i + 1] = Clamp(buf[i + 1] * vig); buf[i + 2] = Clamp(buf[i + 2] * vig);
                    }
                }

                double r = buf[i + 2], g = buf[i + 1], b = buf[i];
                if (cr != null) {
                    r = cr[(int)r]; g = cg[(int)g]; b = cb[(int)b];
                }

                if (hslOn && (r != g || g != b)) {
                    double hh, ss, ll;
                    RgbToHsl(r, g, b, out hh, out ss, out ll);
                    int band = (int)(hh * 6.0); if (band < 0) band = 0; if (band > 5) band = 5;
                    ss = Math.Max(0, Math.Min(1, ss * (1.0 + hslS[band])));
                    ll = Math.Max(0, Math.Min(1, ll + hslL[band] * 0.25));
                    HslToRgb(hh, ss, ll, out r, out g, out b);
                }

                buf[i] = Clamp(b); buf[i + 1] = Clamp(g); buf[i + 2] = Clamp(r);
            }
        }

        Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        bmp.UnlockBits(data);
    }
}

// ---------------- Python 脚本调用（新功能统一入口） ----------------
static class PyTools
{
    public static string Run(string pythonExe, string scriptName, string[] args, int timeoutMs, out string err)
    {
        err = "";
        if (string.IsNullOrEmpty(pythonExe)) { err = "Python not found"; return null; }
        try
        {
            string scriptDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai");
            string script = Path.Combine(scriptDir, scriptName);
            if (!File.Exists(script)) { err = "缺少脚本 " + scriptName; return null; }
            var sb = new StringBuilder("\"" + script + "\"");
            foreach (var a in args) sb.Append(" \"").Append(a).Append("\"");
            var psi = new System.Diagnostics.ProcessStartInfo(pythonExe, sb.ToString());
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.WorkingDirectory = scriptDir;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            // 子进程重定向时 Python 默认按系统 locale(cp936) 输出，会导致中文变乱码；
            // 强制 UTF-8，与上面的解码保持一致
            try {
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            } catch { }
            var proc = System.Diagnostics.Process.Start(psi);
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } err = "超时"; return null; }
            err = stderr.Length > 800 ? stderr.Substring(0, 800) : stderr;
            return stdout;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return null;
        }
    }
}
