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
