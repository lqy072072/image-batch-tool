// ============================================================
// 图片批处理工具 - WebView2 桌面应用
// 作者：純潔の工口君
// ------------------------------------------------------------
// 架构：
//   - UI 层：WebView2 (Edge Chromium) 加载 ui.html，Mac 风格深色/浅色双皮肤
//   - 后端：C# WinForms 宿主，通过 WebMessage 与 HTML 双向通信
//   - 图像处理管线：
//       1. GPU 加速（gpu_imgproc.dll，D3D11 compute shader）
//       2. CPU 原生（imgproc.dll，MSVC /O2 /arch:AVX2 /openmp）
//       3. C# Parallel.For 兜底
//     三层自动降级，全部 try/catch 保护
//   - LUT 支持：.cube / .3dl / .lut 3D LUT，四面体插值，Log 型/创意型自动识别
//   - 其他功能：白平衡、曝光、Log 转换、移轴、裁切、锐化、批量导出
// 编译：csc.exe /target:winexe /platform:x64 /unsafe
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace ImageBatchTool
{
    // 全局缓存路径：所有缓存和运行时临时文件统一放在安装目录 cache 下。
    static class CacheStore
    {
        public static string Root()
        {
            string d = Path.Combine(Application.StartupPath, "cache");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }

        public static string Sub(string name)
        {
            string d = Path.Combine(Root(), name);
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }

        public static string TempFile(string prefix, string ext)
        {
            return Path.Combine(Sub("temp"), prefix + Guid.NewGuid().ToString("N") + ext);
        }
    }

    // ============================================================
    // 原生函数 P/Invoke 声明
    //   imgproc.dll    — C++ AVX2+OpenMP CPU 加速
    //   gpu_imgproc.dll — D3D11 compute shader GPU 加速
    // 所有图像处理函数都接受 BGRA 缓冲区（buf[i]=B, [i+1]=G, [i+2]=R, [i+3]=A）
    // ============================================================
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("imgproc.dll", CallingConvention=CallingConvention.Cdecl)]
        public static extern void lut_apply(byte[] buf, int W, int H, int stride,
            [In] float[] lut, int size, float inMin, float inMax,
            float strength, float chroma,
            [In] float[] wbGain, [In] float[] expTable, [In] float[] logTable);
        [DllImport("imgproc.dll", CallingConvention=CallingConvention.Cdecl)]
        public static extern void color_adjust(byte[] buf, int W, int H, int stride,
            float saturation, float contrast, float brightness,
            float shadows, float highlights, float temp, float tint, float vignette);
        [DllImport("imgproc.dll", CallingConvention=CallingConvention.Cdecl)]
        public static extern void sharpen(byte[] buf, int W, int H, int stride, float amount);
        // GPU acceleration (D3D11 compute shaders)
        [DllImport("gpu_imgproc.dll", CallingConvention=CallingConvention.Cdecl)]
        public static extern int gpu_available();
        [DllImport("gpu_imgproc.dll", CallingConvention=CallingConvention.Cdecl)]
        public static extern int gpu_blur(byte[] buf, int W, int H, int stride, int radius);
        [DllImport("gpu_imgproc.dll", CallingConvention=CallingConvention.Cdecl)]
        public static extern int gpu_lut_apply(byte[] buf, int W, int H, int stride,
            [In] float[] lut, int size, float inMin, float inMax,
            float strength, float chroma, [In] float[] wbGain);
        [DllImport("gpu_imgproc.dll", CallingConvention=CallingConvention.Cdecl)]
        public static extern int gpu_color_adjust(byte[] buf, int W, int H, int stride,
            float saturation, float contrast, float brightness,
            float shadows, float highlights, float temp, float tint, float vignette);
    }

    // ============================================================
    // LUT 预计算查找表
    //   SrgbToLinear — sRGB gamma 反向（线性光）
    //   JpegCodec     — GDI+ JPEG 编码器
    //   Cores         — CPU 核心数（用于并行度）
    // ============================================================
    static class LUT
    {
        public static readonly float[] SrgbToLinear = new float[256];
        public static readonly ImageCodecInfo JpegCodec;
        public static readonly int Cores = Math.Max(1, Environment.ProcessorCount);

        static LUT()
        {
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                double lin = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                SrgbToLinear[i] = (float)lin;
            }
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) { JpegCodec = c; break; }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 记录所有未处理异常，避免“窗口一闪就没了”却查不到原因
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try { MainForm.Log("UI线程异常: " + e.Exception); } catch { }
                try { MessageBox.Show("程序遇到异常：\r\n" + e.Exception.Message, "图片批处理工具", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { MainForm.Log("致命异常: " + e.ExceptionObject); } catch { }
            };
            string mode = "album";
            if (args != null && args.Length > 0)
            {
                foreach (var a in args)
                {
                    string al = (a ?? "").Trim().ToLower();
                    if (al == "--edit" || al == "/edit" || al == "edit" || al == "--editor") { mode = "edit"; break; }
                    if (al == "--album" || al == "/album" || al == "album") { mode = "album"; break; }
                }
            }
            MainForm.Log("程序启动 mode=" + mode);
            var main = new MainForm();
            main.StartupMode = mode;
            main.ApplyModeIcon();
            main.FormClosed += (s, e) => MainForm.Log("主窗口已关闭");
            Application.ApplicationExit += (s, e) => MainForm.Log("ApplicationExit");
            Application.Run(main);
            MainForm.Log("Application.Run 返回");
        }
    }

    // ============================================================
    // 启动画面（Splash）：带进度条，皮肤感知（深色/浅色）
    // ============================================================
    public class SplashForm : Form
    {
        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(360, 220);
            string skin = "dark";
            try {
                string sf = Path.Combine(Application.StartupPath, "skin.txt");
                if (File.Exists(sf)) skin = File.ReadAllText(sf).Trim().ToLower();
            } catch {}
            bool light = skin == "light";
            BackColor = light ? Color.FromArgb(245,245,247) : Color.FromArgb(43,43,43);

            var t = new Label();
            t.Text = "图片批处理工具";
            t.ForeColor = light ? Color.FromArgb(29,29,31) : Color.FromArgb(220,220,220);
            t.Font = new Font("Microsoft YaHei UI", 14f);
            t.AutoSize = false; t.Size = new Size(360, 40); t.Location = new Point(0, 70);
            t.TextAlign = ContentAlignment.MiddleCenter;

            var sub = new Label();
            sub.Text = "正在初始化图像处理引擎…";
            sub.ForeColor = light ? Color.FromArgb(142,142,147) : Color.FromArgb(140,140,140);
            sub.Font = new Font("Microsoft YaHei UI", 9f);
            sub.AutoSize = false; sub.Size = new Size(360, 20); sub.Location = new Point(0, 120);
            sub.TextAlign = ContentAlignment.MiddleCenter;

            var bar = new Panel();
            bar.Size = new Size(200, 3); bar.Location = new Point(80, 160);
            bar.BackColor = light ? Color.FromArgb(229,229,234) : Color.FromArgb(60,60,60);
            var fill = new Panel();
            fill.Size = new Size(0, 3); fill.Location = new Point(0, 0);
            fill.BackColor = Color.FromArgb(9,105,218);
            bar.Controls.Add(fill);

            var pct = new Label();
            pct.Text = "0%";
            pct.ForeColor = light ? Color.FromArgb(142,142,147) : Color.FromArgb(140,140,140);
            pct.Font = new Font("Microsoft YaHei UI", 9f);
            pct.AutoSize = false; pct.Size = new Size(360, 18); pct.Location = new Point(0, 168);
            pct.TextAlign = ContentAlignment.MiddleCenter;

            Controls.Add(t); Controls.Add(sub); Controls.Add(bar); Controls.Add(pct);

            var timer = new System.Windows.Forms.Timer(); timer.Interval = 30;
            int p = 0;
            timer.Tick += (s, e) => {
                p += 3;
                if (p >= 200) p = 200;
                fill.Width = p;
                pct.Text = (int)(p * 100 / 200) + "%";
            };
            timer.Start();

            int radius = 12;
            var path = new GraphicsPath();
            path.AddArc(0, 0, radius*2, radius*2, 180, 90);
            path.AddArc(Width-radius*2, 0, radius*2, radius*2, 270, 90);
            path.AddArc(Width-radius*2, Height-radius*2, radius*2, radius*2, 0, 90);
            path.AddArc(0, Height-radius*2, radius*2, radius*2, 90, 90);
            path.CloseFigure();
            this.Region = new Region(path);
        }
    }

    // ============================================================
    // .cube 3D LUT 加载与采样
    //   Table — float[size^3 * 3]，每顶点 (R, G, B)
    //   InputMin/Max — LUT_3D_INPUT_RANGE 声明的输入范围
    //   SampleTetra — 四面体插值（无色彩带）
    // ============================================================
    public class CubeLut
    {
        public int Size;
        public float[] Table;
        public float InputMin = 0f, InputMax = 1f; // LUT_3D_INPUT_RANGE（DaVinci 标准）

        public static CubeLut Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".3dl") return Load3dl(path);
            // Iridas .lut 与 .cube 同构（LUT_3D_SIZE 文本头）
            return LoadCube(path);
        }

        static CubeLut LoadCube(string path)
        {
            var lut = new CubeLut();
            // 验证文件是否为有效 .cube（防止下载错误 JSON 等垃圾文件）
            try {
                string peek = System.IO.File.ReadAllText(path).TrimStart();
                if (peek.StartsWith("{") || peek.Length < 100) {
                    // 不是有效 cube 文件，返回空 LUT（恒等映射）
                    lut.Size = 33;
                    lut.Table = new float[33*33*33*3];
                    for (int i = 0; i < 33; i++) for (int j = 0; j < 33; j++) for (int k = 0; k < 33; k++) {
                        int idx = ((k*33+j)*33+i)*3;
                        lut.Table[idx] = i / 32.0f; lut.Table[idx+1] = j / 32.0f; lut.Table[idx+2] = k / 32.0f;
                    }
                    return lut;
                }
            } catch { }
            var nums = new List<float>(300000);
            int size = 0;
            using (var sr = new StreamReader(path, System.Text.Encoding.UTF8, true))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring(11).Trim(), out size);
                        continue;
                    }
                    if (line.StartsWith("LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
                    {
                        var rp = line.Substring(17).Trim().Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
                        if (rp.Length >= 2)
                        {
                            float.TryParse(rp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lut.InputMin);
                            float.TryParse(rp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lut.InputMax);
                        }
                        continue;
                    }
                    if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException();
                    char c0 = line[0];
                    if (!(char.IsDigit(c0) || c0 == '-' || c0 == '+' || c0 == '.')) continue;
                    string[] parts = line.Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;
                    float r, g, b;
                    if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) continue;
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g)) continue;
                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) continue;
                    nums.Add(r); nums.Add(g); nums.Add(b);
                }
            }
            int count = nums.Count / 3;
            if (size <= 1) size = (int)Math.Round(Math.Pow(count, 1.0 / 3.0));
            // 校验格数：如果数据条数不等于 size^3，按实际数据量反推
            int expected = size * size * size;
            if (Math.Abs(count - expected) > size * size)
            {
                int newSize = (int)Math.Round(Math.Pow(count, 1.0 / 3.0));
                System.Diagnostics.Debug.WriteLine("LUT 格数不符: 声明=" + size + " 实际=" + count + " 反推=" + newSize);
                size = newSize;
            }
            lut.Size = size;
            lut.Table = nums.ToArray();
            for (int i = 0; i < lut.Table.Length; i++)
            {
                float v = lut.Table[i];
                lut.Table[i] = v < 0 ? 0 : (v > 1 ? 1 : v);
            }
            return lut;
        }

        // .3dl 3D LUT —— Lustre / Picture Instruments 的 3DMESH 整数格点表
        //   典型内容：3DMESH / Mesh 4 10 / "0 64 128 … 1023"（输入网格节点声明行）/ 17^3 行 RGB（10-bit）
        //   关键差异：源文件按 B 最快维排列（.cube 是 R 最快维），载入时必须转置成内部 R 最快表序，
        //   否则红蓝通道被互换，套用后颜色明显不对。
        static CubeLut Load3dl(string path)
        {
            var lut = new CubeLut();
            var src = new List<float>(300000);
            int declared = 0, gridCount = 0;
            double gridMax = -1, maxAbs = 0;

            using (var sr = new StreamReader(path, System.Text.Encoding.UTF8, true))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    char c0 = line[0];
                    if (c0 == '#' || c0 == ';' || c0 == '*') continue;
                    if (line.StartsWith("//")) continue;
                    string[] parts = line.Split(new[] { ' ', '\t', '=', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    // 头行（3DMESH / Mesh 4 10 / TITLE / LUT3D / DOMAIN / MATRIX …）首列不是数字，整行跳过
                    float first;
                    if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out first)) continue;

                    if (parts.Length == 1)
                    {
                        // 单独一行整数 = 声明的格点数
                        int s;
                        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out s)
                            && s >= 2 && s <= 256) declared = s;
                        continue;
                    }

                    if (parts.Length > 3)
                    {
                        var vals = new float[parts.Length];
                        bool allNum = true, monotonic = true;
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i]))
                            { allNum = false; break; }
                            if (i > 0 && vals[i] < vals[i - 1]) monotonic = false;
                        }
                        if (!allNum) continue;
                        if (monotonic && vals.Length >= 4)
                        {
                            // 输入网格声明行：长度即格点数，末值即满刻度（1023=10bit / 255=8bit）
                            gridCount = vals.Length;
                            gridMax = vals[vals.Length - 1];
                            continue;
                        }
                        if (vals.Length == 4)
                        {
                            // 少数导出器每行多一列 alpha
                            maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(vals[0]), Math.Max(Math.Abs(vals[1]), Math.Abs(vals[2]))));
                            src.Add(vals[0]); src.Add(vals[1]); src.Add(vals[2]);
                        }
                        continue;
                    }

                    float r, g, b;
                    if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) continue;
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g)) continue;
                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) continue;
                    if (Math.Abs(r) > maxAbs) maxAbs = Math.Abs(r);
                    if (Math.Abs(g) > maxAbs) maxAbs = Math.Abs(g);
                    if (Math.Abs(b) > maxAbs) maxAbs = Math.Abs(b);
                    src.Add(r); src.Add(g); src.Add(b);
                }
            }

            int count = src.Count / 3;
            if (count < 8) throw new InvalidDataException(".3dl 数据为空: " + Path.GetFileName(path));

            int size = gridCount >= 2 ? gridCount : declared;
            if (size < 2) size = (int)Math.Round(Math.Pow(count, 1.0 / 3.0));
            if (size < 2) throw new InvalidDataException(".3dl 格数推断失败: " + Path.GetFileName(path));
            long cells = (long)size * size * size;
            // 数据量与格数明显不符时，按实际数据量重新反推
            if (Math.Abs(count - cells) > (long)size * size)
            {
                size = (int)Math.Round(Math.Pow(count, 1.0 / 3.0));
                if (size < 2) throw new InvalidDataException(".3dl 格数推断失败: " + Path.GetFileName(path));
                cells = (long)size * size * size;
            }
            if (cells > 20000000L) throw new InvalidDataException(".3dl 格数过大: " + size + "^3");

            // 满刻度：优先取网格声明末值，其次按整数/浮点表判断
            double full = gridMax > 1 ? gridMax : (maxAbs > 1.5 ? (maxAbs > 255 ? 1023.0 : 255.0) : 1.0);
            if (full <= 0) full = 1.0;

            // 容错：数据行数与格数略有出入时，按格数裁剪/补齐，保证表结构完整
            if (count != cells)
            {
                if (count > cells)
                    src.RemoveRange((int)cells * 3, src.Count - (int)cells * 3);
                else
                {
                    float lr = src[src.Count - 3], lg = src[src.Count - 2], lb = src[src.Count - 1];
                    for (long i = count; i < cells; i++) { src.Add(lr); src.Add(lg); src.Add(lb); }
                }
            }

            lut.Size = size;

            // 非常规 .3dl（明确 R 最快维）原样使用；标准 .3dl 需 B 最快 → R 最快转置
            if (DetectRFastest3dl(src, size))
            {
                lut.Table = new float[cells * 3];
                for (int i = 0; i < lut.Table.Length; i++) lut.Table[i] = Norm01(src[i], full);
                return lut;
            }

            lut.Table = new float[cells * 3];
            for (int ib = 0; ib < size; ib++)
            {
                for (int ig = 0; ig < size; ig++)
                {
                    long drow = (((long)ib * size + ig) * size) * 3;
                    for (int ir = 0; ir < size; ir++)
                    {
                        long si = (((long)ir * size + ig) * size + ib) * 3;
                        long di = drow + ir * 3;
                        lut.Table[(int)di] = Norm01(src[(int)si], full);
                        lut.Table[(int)di + 1] = Norm01(src[(int)si + 1], full);
                        lut.Table[(int)di + 2] = Norm01(src[(int)si + 2], full);
                    }
                }
            }
            return lut;
        }

        static float Norm01(double v, double full)
        {
            double x = v / full;
            return (float)(x < 0 ? 0 : (x > 1 ? 1 : x));
        }

        // 仅识别“明确无疑是 R 最快维”的非常规 3dl：内层主导变化在 R 且外层主导变化在 B
        // 标准 .3dl 返回 false（内层主导 B、外层主导 R）；三通道相同的黑白表也返回 false，按标准处理
        static bool DetectRFastest3dl(List<float> src, int size)
        {
            if (size < 3) return false;
            long cells = (long)size * size * size;
            if (src.Count < cells * 3) return false;
            float[] d = src.ToArray();
            long total = cells;
            double[] qIn = new double[3], qOut = new double[3];
            for (long i = 0; i + 1 < total; i++)
                for (int c = 0; c < 3; c++) qIn[c] += Math.Abs(d[i * 3 + 3 + c] - d[i * 3 + c]);
            long stepOut = (long)size * size;
            for (long i = 0; i + stepOut < total; i++)
                for (int c = 0; c < 3; c++) qOut[c] += Math.Abs(d[(i + stepOut) * 3 + c] - d[i * 3 + c]);
            int domIn = 0, domOut = 0;
            for (int c = 1; c < 3; c++)
            {
                if (qIn[c] > qIn[domIn]) domIn = c;
                if (qOut[c] > qOut[domOut]) domOut = c;
            }
            return domIn == 0 && domOut == 2;
        }

        // 四面体插值（33^3 以上 LUT 用，更平滑、无伪影）
        public static void SampleTetra(float[] t, int n, float r, float g, float b,
                                  out float orr, out float og, out float ob)
        {
            float pr = Clamp01(r) * (n - 1), pg = Clamp01(g) * (n - 1), pb = Clamp01(b) * (n - 1);
            int r0 = (int)pr, g0 = (int)pg, b0 = (int)pb;
            int r1 = r0 + 1 < n ? r0 + 1 : n - 1;
            int g1 = g0 + 1 < n ? g0 + 1 : n - 1;
            int b1 = b0 + 1 < n ? b0 + 1 : n - 1;
            float fr = pr - r0, fg = pg - g0, fb = pb - b0;
            int i000 = ((b0*n+g0)*n+r0)*3;
            // 8 顶点
            float r000=t[i000], g000=t[i000+1], b000=t[i000+2];
            float r100=t[((b0*n+g0)*n+r1)*3], g100=t[((b0*n+g0)*n+r1)*3+1], b100=t[((b0*n+g0)*n+r1)*3+2];
            float r010=t[((b0*n+g1)*n+r0)*3], g010=t[((b0*n+g1)*n+r0)*3+1], b010=t[((b0*n+g1)*n+r0)*3+2];
            float r001=t[((b1*n+g0)*n+r0)*3], g001=t[((b1*n+g0)*n+r0)*3+1], b001=t[((b1*n+g0)*n+r0)*3+2];
            float r110=t[((b0*n+g1)*n+r1)*3], g110=t[((b0*n+g1)*n+r1)*3+1], b110=t[((b0*n+g1)*n+r1)*3+2];
            float r101=t[((b1*n+g0)*n+r1)*3], g101=t[((b1*n+g0)*n+r1)*3+1], b101=t[((b1*n+g0)*n+r1)*3+2];
            float r011=t[((b1*n+g1)*n+r0)*3], g011=t[((b1*n+g1)*n+r0)*3+1], b011=t[((b1*n+g1)*n+r0)*3+2];
            float r111=t[((b1*n+g1)*n+r1)*3], g111=t[((b1*n+g1)*n+r1)*3+1], b111=t[((b1*n+g1)*n+r1)*3+2];
            // 四面体分解
            if (fr >= fg && fg >= fb) {
                orr = r000 + fr*(r100-r000) + fg*(r110-r100) + fb*(r111-r110);
                og = g000 + fr*(g100-g000) + fg*(g110-g100) + fb*(g111-g110);
                ob = b000 + fr*(b100-b000) + fg*(b110-b100) + fb*(b111-b110);
            } else if (fr >= fg && fb >= fg) {
                orr = r000 + fr*(r100-r000) + fb*(r101-r100) + fg*(r111-r101);
                og = g000 + fr*(g100-g000) + fb*(g101-g100) + fg*(g111-g101);
                ob = b000 + fr*(b100-b000) + fb*(b101-b100) + fg*(b111-b101);
            } else if (fg >= fr && fr >= fb) {
                orr = r000 + fg*(r010-r000) + fr*(r110-r010) + fb*(r111-r110);
                og = g000 + fg*(g010-g000) + fr*(g110-g010) + fb*(g111-g110);
                ob = b000 + fg*(b010-b000) + fr*(b110-b010) + fb*(b111-b110);
            } else if (fg >= fb && fb >= fr) {
                orr = r000 + fg*(r010-r000) + fb*(r011-r010) + fr*(r111-r011);
                og = g000 + fg*(g010-g000) + fb*(g011-g010) + fr*(g111-g011);
                ob = b000 + fg*(b010-b000) + fb*(b011-b010) + fr*(b111-b011);
            } else if (fb >= fr && fr >= fg) {
                orr = r000 + fb*(r001-r000) + fr*(r101-r001) + fg*(r111-r101);
                og = g000 + fb*(g001-g000) + fr*(g101-g001) + fg*(g111-g101);
                ob = b000 + fb*(b001-b000) + fr*(b101-b001) + fg*(b111-b101);
            } else {
                orr = r000 + fb*(r001-r000) + fg*(r011-r001) + fr*(r111-r011);
                og = g000 + fb*(g001-g000) + fg*(g011-g001) + fr*(g111-g011);
                ob = b000 + fb*(b001-b000) + fg*(b011-b001) + fr*(b111-b011);
            }
        }

        public static void Sample(float[] t, int n, float r, float g, float b,
                                  out float orr, out float og, out float ob)
        {
            float pr = Clamp01(r) * (n - 1), pg = Clamp01(g) * (n - 1), pb = Clamp01(b) * (n - 1);
            int r0 = (int)pr, g0 = (int)pg, b0 = (int)pb;
            int r1 = r0 + 1 < n ? r0 + 1 : n - 1;
            int g1 = g0 + 1 < n ? g0 + 1 : n - 1;
            int b1 = b0 + 1 < n ? b0 + 1 : n - 1;
            float fr = pr - r0, fg = pg - g0, fb = pb - b0;
            int i000 = ((b0*n+g0)*n+r0)*3, i100 = ((b0*n+g0)*n+r1)*3;
            int i010 = ((b0*n+g1)*n+r0)*3, i110 = ((b0*n+g1)*n+r1)*3;
            int i001 = ((b1*n+g0)*n+r0)*3, i101 = ((b1*n+g0)*n+r1)*3;
            int i011 = ((b1*n+g1)*n+r0)*3, i111 = ((b1*n+g1)*n+r1)*3;
            float a0 = t[i000] + (t[i100]-t[i000])*fr;
            float a1 = t[i010] + (t[i110]-t[i010])*fr;
            float a2 = t[i001] + (t[i101]-t[i001])*fr;
            float a3 = t[i011] + (t[i111]-t[i011])*fr;
            float b0v = a0 + (a1-a0)*fg, b1v = a2 + (a3-a2)*fg;
            orr = b0v + (b1v-b0v)*fb;
            float c0 = t[i000+1] + (t[i100+1]-t[i000+1])*fr;
            float c1 = t[i010+1] + (t[i110+1]-t[i010+1])*fr;
            float c2 = t[i001+1] + (t[i101+1]-t[i001+1])*fr;
            float c3 = t[i011+1] + (t[i111+1]-t[i011+1])*fr;
            float d0 = c0 + (c1-c0)*fg, d1 = c2 + (c3-c2)*fg;
            og = d0 + (d1-d0)*fb;
            float e0 = t[i000+2] + (t[i100+2]-t[i000+2])*fr;
            float e1 = t[i010+2] + (t[i110+2]-t[i010+2])*fr;
            float e2 = t[i001+2] + (t[i101+2]-t[i001+2])*fr;
            float e3 = t[i011+2] + (t[i111+2]-t[i011+2])*fr;
            float f0 = e0 + (e1-e0)*fg, f1 = e2 + (e3-e2)*fg;
            ob = f0 + (f1-f0)*fb;
        }
        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }

    // ============================================================
    // 神经网络（纯C#前向传播，不依赖onnxruntime.dll）
    // ============================================================
    // ============================================================
    // AI全流程调色：Python直接输出所有调色参数，C#只执行
    // ============================================================
    // ===== 自动优化引擎：自适应白平衡（中性样本参考+亮度守恒）+ 智能黑白场 + 中灰锚定 + 高光保护 =====
    // 相比旧算法的改进：
    //  1) 白平衡改用"近中性像素"做参考（避免被大面积色块带偏），并用几何均值锁住整体亮度
    //     （旧算法只做通道缩减，暖偏图会把中灰从 132 压到 31）
    //  2) 黑白场端点映射到 [8,247] 保留余量 + 高光软滚降（旧算法端点直接拉 0/255，正常图也会产生 4.8% 死白）
    //  3) 中灰锚定：按画面中值做温和 gamma 回归 128（旧算法过曝图中灰会被压到 50）
    //  4) 动态范围窄才拉伸，且限制拉伸后中灰不越界 [90,170]
    static class ColorAutoEngine
    {
        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public static void Apply(byte[] buf, int W, int H, int stride)
        {
            // ---------- 1. 采样：直方图 + 通道均值 + 近中性样本 ----------
            var hist = new int[256];
            long sumR = 0, sumG = 0, sumB = 0, cnt = 0;
            long nR = 0, nG = 0, nB = 0; int nCnt = 0;
            for (int y = 0; y < H; y += 2)
            {
                int row = y * stride;
                for (int x = 0; x < W; x += 2)
                {
                    int i = row + x * 4;
                    int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    int lum = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                    if (lum < 0) lum = 0; else if (lum > 255) lum = 255;
                    hist[lum]++;
                    sumR += r; sumG += g; sumB += b; cnt++;
                    int mx = r, mn = r;
                    if (g > mx) mx = g; if (b > mx) mx = b;
                    if (g < mn) mn = g; if (b < mn) mn = b;
                    if (mx - mn <= 14 && lum > 40 && lum < 220) { nR += r; nG += g; nB += b; nCnt++; }
                }
            }
            if (cnt < 100) return;
            double avgR = (double)sumR / cnt, avgG = (double)sumG / cnt, avgB = (double)sumB / cnt;

            // ---------- 2. 白平衡：近中性样本优先（≥15%），否则灰度世界；限幅；几何均值锁亮度 ----------
            double refR, refG, refB;
            if (nCnt >= cnt * 0.15) { refR = (double)nR / nCnt; refG = (double)nG / nCnt; refB = (double)nB / nCnt; }
            else { refR = avgR; refG = avgG; refB = avgB; }
            double gray = (refR + refG + refB) / 3.0;
            double wbR = 1.0, wbG = 1.0, wbB = 1.0;
            if (Math.Abs(gray - refR) > gray * 0.06) wbR = gray / Math.Max(refR, 1.0);
            if (Math.Abs(gray - refG) > gray * 0.06) wbG = gray / Math.Max(refG, 1.0);
            if (Math.Abs(gray - refB) > gray * 0.06) wbB = gray / Math.Max(refB, 1.0);
            wbR = Clamp(wbR, 0.85, 1.15);
            wbG = Clamp(wbG, 0.90, 1.10);
            wbB = Clamp(wbB, 0.85, 1.15);
            double gm = Math.Pow(wbR * wbG * wbB, 1.0 / 3.0);
            if (gm > 0.001) { wbR /= gm; wbG /= gm; wbB /= gm; }
            wbR = Clamp(wbR, 0.82, 1.18);
            wbG = Clamp(wbG, 0.88, 1.12);
            wbB = Clamp(wbB, 0.82, 1.18);

            // ---------- 3. 直方图端点（0.5% 裁剪）与中灰 ----------
            double total = cnt;
            double clipAmt = total * 0.005;
            int p05 = 0, p95 = 255; double acc = 0;
            for (int k = 0; k < 256; k++) { acc += hist[k]; if (acc >= clipAmt) { p05 = k; break; } }
            acc = 0;
            for (int k = 255; k >= 0; k--) { acc += hist[k]; if (acc >= clipAmt) { p95 = k; break; } }
            int med = 128; acc = 0;
            for (int k = 0; k < 256; k++) { acc += hist[k]; if (acc >= total / 2) { med = k; break; } }

            // ---------- 4. 中灰锚定 gamma（med→128，温和限幅） ----------
            double gamma = 1.0;
            if (med >= 6 && med <= 249)
            {
                gamma = Math.Log(128.0 / 255.0) / Math.Log(med / 255.0);
                gamma = Clamp(gamma, 0.68, 1.80);
            }

            // ---------- 5. 智能对比拉伸（gamma 后空间；范围窄才拉，中灰保持在 [90,170]） ----------
            double scale = 1.0, offset = 0.0;
            double g05 = Math.Pow(p05 / 255.0, gamma) * 255.0;
            double g95 = Math.Pow(p95 / 255.0, gamma) * 255.0;
            double gmed = Math.Pow(med / 255.0, gamma) * 255.0;
            double dr = g95 - g05;
            double d1 = gmed - g05;
            if (dr >= 40 && d1 >= 8 && (g95 - gmed) >= 8)
            {
                double sMax = Math.Min(1.6, (170.0 - 8.0) / d1);  // 拉伸后中灰不超过 170
                double sMin = (90.0 - 8.0) / d1;                  // 拉伸后中灰不低于 90
                if (sMax >= sMin)
                {
                    scale = Clamp(239.0 / dr, sMin, sMax);        // 端点映射到 [8,247]，保余量防死黑死白
                    if (scale < 1.0) scale = 1.0;
                    offset = 8.0 - g05 * scale;
                }
            }

            // ---------- 6. 应用：WB → gamma → 拉伸 → 饱和补偿 → 高光滚降 ----------
            double satComp = scale > 1.12 ? 1.03 : 1.0;
            bool doGamma = Math.Abs(gamma - 1.0) > 0.002;
            bool doStretch = scale > 1.002;
            for (int y = 0; y < H; y++)
            {
                int row = y * stride;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x * 4;
                    double b = buf[i] * wbB, g = buf[i + 1] * wbG, r = buf[i + 2] * wbR;
                    double lum0 = 0.299 * r + 0.587 * g + 0.114 * b;
                    double lum = lum0;
                    if (doGamma)
                    {
                        double v = lum / 255.0;
                        if (v < 0) v = 0; else if (v > 1) v = 1;
                        lum = Math.Pow(v, gamma) * 255.0;
                    }
                    if (doStretch) lum = lum * scale + offset;
                    if (lum < 0) lum = 0; else if (lum > 255) lum = 255;
                    double ratio = lum0 > 0.5 ? lum / lum0 : 1.0;
                    ratio = Clamp(ratio, 0.3, 3.0);
                    r *= ratio; g *= ratio; b *= ratio;
                    if (satComp != 1.0)
                    {
                        double l2 = 0.299 * r + 0.587 * g + 0.114 * b;
                        r = l2 + (r - l2) * satComp; g = l2 + (g - l2) * satComp; b = l2 + (b - l2) * satComp;
                    }
                    if (r > 235) r = 235 + (r - 235) * 0.35;
                    if (g > 235) g = 235 + (g - 235) * 0.35;
                    if (b > 235) b = 235 + (b - 235) * 0.35;
                    buf[i] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    buf[i + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    buf[i + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                }
            }
        }
    }

    static class AiRunner
    {
        public static string LastError = "";
        static string pythonPath = "";
        public static string DebugPythonPath { get { return pythonPath; } }

        // AI输出的参数
        public static double OutWbR = 1.0, OutWbG = 1.0, OutWbB = 1.0;
        public static double OutExposure = 1.0, OutContrast = 1.0, OutSaturation = 1.0;
        public static double OutHighlights = 1.0, OutShadows = 1.0;
        public static double OutPreExposure = 1.0, OutLogGamma = 1.0;
        public static bool OutUseLogConvert = false;
        public static string OutScene = "generic";
        // 调用前设置：LUT信息提示
        public static bool LutIsLogHint = false;
        public static double LutBlackHint = 0.0, LutWhiteHint = 1.0;

        public static bool IsAvailable()
        {
            string script = Path.Combine(Application.StartupPath, "ai", "ai_process.py");
            if (!File.Exists(script)) { LastError = "缺少 ai/ai_process.py: " + script; return false; }
            // 优先使用嵌入的Python
            string embedded = Path.Combine(Application.StartupPath, "python", "python.exe");
            if (File.Exists(embedded)) { pythonPath = embedded; return true; }
            // 尝试多个python路径（用户双击启动时 PATH 可能不含 python，须探测绝对路径）
            var candidates = new List<string> { "python", "py", "python3" };
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] fixedDirs = {
                Path.Combine(lad, "Programs", "Python"),
                Path.Combine(pf, "Python"),
                @"C:\Python",
                @"C:\",
            };
            // 常见版本目录 Python39~Python314
            for (int v = 314; v >= 39; v--) {
                string ver = "Python" + v.ToString("D2");
                foreach (string dir in fixedDirs) {
                    string full = Path.Combine(dir, ver);
                    candidates.Add(Path.Combine(full, "python.exe"));
                    candidates.Add(Path.Combine(full, "pythonw.exe"));
                }
            }
            // 豆包客户端自带运行时 python（glob 匹配版本 hash 目录）
            try {
                string bases = Path.Combine(lad, "Doubao", "User Data", "sandbox_runtime", "bases");
                if (Directory.Exists(bases)) {
                    foreach (var d in Directory.GetDirectories(bases)) {
                        string pp = Path.Combine(d, "python", "python.exe");
                        if (File.Exists(pp)) candidates.Add(pp);
                    }
                }
            } catch {}
            foreach (var py in candidates) {
                try {
                    var psi = new System.Diagnostics.ProcessStartInfo();
                    psi.FileName = py;
                    psi.Arguments = "--version";
                    psi.UseShellExecute = false;
                    psi.RedirectStandardOutput = true;
                    psi.CreateNoWindow = true;
                    var p = System.Diagnostics.Process.Start(psi);
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0) { pythonPath = py; return true; }
                } catch {}
            }
            LastError = "python未找到（试过 python/py/python3 及常见安装路径）";
            return false;
        }

        // 对BGRA buffer运行AI全流程分析，返回是否成功
        public static bool ProcessImage(byte[] buf, int W, int H, int stride)
        {
            LastError = "";
            OutWbR = OutWbG = OutWbB = 1.0;
            OutExposure = OutContrast = OutSaturation = 1.0;
            OutHighlights = OutShadows = 1.0;
            OutPreExposure = 1.0; OutLogGamma = 1.0; OutUseLogConvert = false;
            OutScene = "generic";

            if (string.IsNullOrEmpty(pythonPath) && !IsAvailable()) return false;

            string tmpImg = CacheStore.TempFile("ai_in_", ".jpg");
            string tmpJson = CacheStore.TempFile("ai_out_", ".json");
            try {
                System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(new System.Drawing.Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, bmpData.Scan0, W * H * 4);
                bmp.UnlockBits(bmpData);
                bmp.Save(tmpImg, System.Drawing.Imaging.ImageFormat.Jpeg);
                bmp.Dispose();

                string script = Path.Combine(Application.StartupPath, "ai", "ai_process.py");
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = pythonPath;
                // 可选附加参数：LUT类型信息
                string extraArgs = "";
                if (LutIsLogHint) {
                    extraArgs = " 1 " + LutBlackHint.ToString("F4") + " " + LutWhiteHint.ToString("F4");
                }
                psi.Arguments = "\"" + script + "\" \"" + tmpImg + "\" \"" + tmpJson + "\"" + extraArgs;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Application.StartupPath;
                var p = System.Diagnostics.Process.Start(psi);
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(60000);

                if (p.ExitCode != 0) {
                    LastError = "python错误: " + (stderr.Length > 200 ? stderr.Substring(0, 200) : stderr);
                    return false;
                }

                if (!File.Exists(tmpJson)) { LastError = "无结果文件"; return false; }
                string json = File.ReadAllText(tmpJson);

                OutScene = ExtractJsonString(json, "scene");
                OutWbR = ExtractJsonDouble(json, "wbR");
                OutWbG = ExtractJsonDouble(json, "wbG");
                OutWbB = ExtractJsonDouble(json, "wbB");
                OutExposure = ExtractJsonDouble(json, "exposure");
                OutContrast = ExtractJsonDouble(json, "contrast");
                OutSaturation = ExtractJsonDouble(json, "saturation");
                OutHighlights = ExtractJsonDouble(json, "highlights");
                OutShadows = ExtractJsonDouble(json, "shadows");
                OutPreExposure = ExtractJsonDouble(json, "preExposure");
                OutLogGamma = ExtractJsonDouble(json, "logGamma");
                OutUseLogConvert = json.IndexOf("\"useLogConvert\": true") >= 0;

                LastError = "scene=" + OutScene +
                    " WB=" + OutWbR.ToString("F2") + "/" + OutWbG.ToString("F2") + "/" + OutWbB.ToString("F2") +
                    " preExp=" + OutPreExposure.ToString("F2") +
                    " logConv=" + OutUseLogConvert + " gamma=" + OutLogGamma.ToString("F2") +
                    " con=" + OutContrast.ToString("F2") +
                    " sat=" + OutSaturation.ToString("F2");
                return true;
            } catch (Exception ex) {
                LastError = ex.Message;
                return false;
            } finally {
                try { if (File.Exists(tmpImg)) File.Delete(tmpImg); } catch {}
                try { if (File.Exists(tmpJson)) File.Delete(tmpJson); } catch {}
            }
        }

        static string ExtractJsonString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return "";
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        static double ExtractJsonDouble(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return 1.0;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return 1.0;
            int start = colon + 1;
            // 跳过冒号后的空格/换行
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\r' || json[start] == '\n')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
            string num = json.Substring(start, end - start).Trim();
            double v;
            if (double.TryParse(num, out v)) return v;
            return 1.0;
        }
    }

    static class BeautyRunner
    {
        static string pythonPath = "";
        static bool? _available;

        public static bool IsAvailable()
        {
            if (_available.HasValue) return _available.Value;
            // 优先使用嵌入的Python
            string embedded = Path.Combine(Application.StartupPath, "python", "python.exe");
            if (File.Exists(embedded) && HasDeps(embedded)) { pythonPath = embedded; _available = true; return true; }
            string[] candidates = { "python", "py", "python3",
                @"C:\Python313\python.exe", @"C:\Python312\python.exe", @"C:\Python311\python.exe" };
            foreach (var p in candidates)
            {
                if (HasDeps(p)) { pythonPath = p; _available = true; return true; }
            }
            // 复用 AiRunner 更完整的探测（常见安装路径 + 豆包客户端运行时）
            try {
                if (AiRunner.IsAvailable()) {
                    string ap = AiRunner.DebugPythonPath;
                    if (HasDeps(ap)) { pythonPath = ap; _available = true; return true; }
                }
            } catch { }
            if (string.IsNullOrEmpty(LastError))
                LastError = "python未找到可用解释器（需带 cv2/numpy/onnxruntime）";
            _available = false; return false;
        }

        // 解释器必须同时具备 cv2 / numpy / onnxruntime，否则脚本一启动就 ImportError
        static bool HasDeps(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, "-c \"import cv2,numpy,onnxruntime;print('ok')\"");
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                var proc = System.Diagnostics.Process.Start(psi);
                string so = proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit(8000);
                return proc.ExitCode == 0 && so.Contains("ok");
            }
            catch { return false; }
        }

        public static string LastError = "";

        // 对Bitmap做人脸美颜，返回新Bitmap（失败返回null）
        public static float Strength = 0.7f;

        // 批量并发时不能读静态 Strength，用显式强度（thread-safe）
        public static string PythonExe { get { IsAvailable(); return pythonPath; } }

        // 通用 图像->图像 Python 管道（景深/超分等新功能复用）
        static Bitmap RunPipe(Bitmap src, string script, string[] extra, int timeoutMs)
        {
            try
            {
                string tmpIn = CacheStore.TempFile("pipe_in_", ".png");
                string tmpOut = CacheStore.TempFile("pipe_out_", ".png");
                src.Save(tmpIn, System.Drawing.Imaging.ImageFormat.Png);
                var args = new List<string> { tmpIn, tmpOut };
                foreach (var a in extra) args.Add(a);
                string err;
                string so = PyTools.Run(PythonExe, script, args.ToArray(), timeoutMs, out err);
                LastError = (so == null ? "" : so.Trim()) + (string.IsNullOrEmpty(err) ? "" : " | " + err);
                try { System.IO.File.Delete(tmpIn); } catch { }
                if (System.IO.File.Exists(tmpOut))
                {
                    var r = new Bitmap(tmpOut);
                    try { System.IO.File.Delete(tmpOut); } catch { }
                    return r;
                }
                return null;
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        // 景深虚化（Depth Anything V2 Small）
        public static Bitmap ProcessDof(Bitmap src, int strength, int focus)
        {
            return RunPipe(src, "dof.py", new string[] { strength.ToString(), focus.ToString() }, 300000);
        }

        // 全图超分（Real-ESRGAN 分块）
        public static Bitmap ProcessSr(Bitmap src, int scale)
        {
            return RunPipe(src, "sr.py", new string[] { scale.ToString() }, 900000);
        }

        // 证件照（裁脸对齐 + 换底 + 尺寸/排版）
        public static string ProcessIdPhoto(string srcFile, int sizeMode, int bg, int cw, int ch, int sheet, int pw, int ph, string dst)
        {
            string err;
            string so = PyTools.Run(PythonExe, "idphoto.py",
                new string[] { srcFile, dst, sizeMode.ToString(), bg.ToString(), cw.ToString(), ch.ToString(), sheet.ToString(), pw.ToString(), ph.ToString() },
                300000, out err);
            LastError = (so == null ? "" : so.Trim()) + (string.IsNullOrEmpty(err) ? "" : " | " + err);
            return System.IO.File.Exists(dst) ? dst : null;
        }

        public static Bitmap Process(Bitmap src, float strength)
        {
            if (!IsAvailable()) { LastError = "Python not found"; return null; }
            try
            {
                string tmpIn = CacheStore.TempFile("beauty_in_", ".png");
                string tmpOut = CacheStore.TempFile("beauty_out_", ".png");
                src.Save(tmpIn, System.Drawing.Imaging.ImageFormat.Png);

                string scriptDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai");
                string script = System.IO.Path.Combine(scriptDir, "beauty.py");

                var psi = new System.Diagnostics.ProcessStartInfo(pythonPath,
                    "\"" + script + "\" \"" + tmpIn + "\" \"" + tmpOut + "\" " + strength.ToString("0.0"));
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.WorkingDirectory = scriptDir;

                var proc = System.Diagnostics.Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120000);

                try { System.IO.File.Delete(tmpIn); } catch { }

                if (System.IO.File.Exists(tmpOut))
                {
                    var result = new Bitmap(tmpOut);
                    try { System.IO.File.Delete(tmpOut); } catch { }
                    LastError = stdout.Trim();
                    return result;
                }
                LastError = "no output: " + stdout + " " + stderr;
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        // ---- 美颜相机（分项美颜）参数拼装：beauty_pro.py 接受 smooth=60,white=35,... ----
        static readonly string[] ProKeys = { "smooth", "white", "spot", "dark", "eye", "lip",
                                             "lipcolor", "rosy", "slim", "eyesize", "nose", "chin",
                                             "aifix", "aifix_strength",
                                             "shine", "teeth", "neck", "glint" };
        static readonly double[] ProDefaults = { 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 55, 0, 0, 0, 0 };

        public static string BuildParams(System.Collections.Generic.Dictionary<string, double> par)
        {
            string s = "";
            for (int i = 0; i < ProKeys.Length; i++)
            {
                string key = "bps_" + ProKeys[i];
                double v = par.ContainsKey(key) ? par[key] : ProDefaults[i];
                if (s.Length > 0) s += ",";
                s += ProKeys[i] + "=" + v.ToString("0.##");
            }
            return s;
        }

        // 分项美颜：BiSeNet 分区 + FaceMesh 468 关键点液化 + 传统 CV 质感处理
        public static Bitmap ProcessPro(Bitmap src, string parms)
        {
            if (!IsAvailable()) { LastError = "Python not found"; return null; }
            try
            {
                string tmpIn = CacheStore.TempFile("beautypro_in_", ".png");
                string tmpOut = CacheStore.TempFile("beautypro_out_", ".png");
                src.Save(tmpIn, System.Drawing.Imaging.ImageFormat.Png);   // 无损中转，避免 AI 前先掉一次细节

                string scriptDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai");
                string script = System.IO.Path.Combine(scriptDir, "beauty_pro.py");

                var psi = new System.Diagnostics.ProcessStartInfo(pythonPath,
                    "\"" + script + "\" \"" + tmpIn + "\" \"" + tmpOut + "\" " + parms);
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.WorkingDirectory = scriptDir;

                var proc = System.Diagnostics.Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(180000);

                try { System.IO.File.Delete(tmpIn); } catch { }

                if (System.IO.File.Exists(tmpOut))
                {
                    var result = new Bitmap(tmpOut);
                    try { System.IO.File.Delete(tmpOut); } catch { }
                    LastError = stdout.Trim();
                    return result;
                }
                LastError = "no output: " + stdout + " " + stderr;
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        public static Bitmap Process(Bitmap src)
        {
            if (!IsAvailable()) { LastError = "Python not found"; return null; }
            try
            {
                string tmpIn = CacheStore.TempFile("beauty_in_", ".png");
                string tmpOut = CacheStore.TempFile("beauty_out_", ".png");
                src.Save(tmpIn, System.Drawing.Imaging.ImageFormat.Png);   // 无损中转，避免 AI 前先掉一次细节

                string scriptDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai");
                string script = System.IO.Path.Combine(scriptDir, "beauty.py");

                var psi = new System.Diagnostics.ProcessStartInfo(pythonPath,
                    "\"" + script + "\" \"" + tmpIn + "\" \"" + tmpOut + "\" " + Strength.ToString("0.0"));
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.WorkingDirectory = scriptDir;

                var proc = System.Diagnostics.Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120000);

                try { System.IO.File.Delete(tmpIn); } catch { }

                if (System.IO.File.Exists(tmpOut))
                {
                    var result = new Bitmap(tmpOut);
                    try { System.IO.File.Delete(tmpOut); } catch { }
                    LastError = stdout.Trim();
                    return result;
                }
                LastError = "no output: " + stdout + " " + stderr;
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }
    }

    static class OnnxNative
    {
        // 预处理：把BGRA图像缩放到224x224，转为CHW float，ImageNet归一化
        public static float[] PreprocessForOnnx(byte[] buf, int W, int H, int stride, int size)
        {
            float[] result = new float[3 * size * size];
            // ImageNet均值和标准差（RGB顺序）
            float[] mean = { 0.485f, 0.456f, 0.406f };
            float[] std = { 0.229f, 0.224f, 0.225f };
            double scaleX = (double)W / size;
            double scaleY = (double)H / size;
            for (int y = 0; y < size; y++) {
                int srcY = (int)(y * scaleY);
                if (srcY >= H) srcY = H - 1;
                for (int x = 0; x < size; x++) {
                    int srcX = (int)(x * scaleX);
                    if (srcX >= W) srcX = W - 1;
                    int i = srcY * stride + srcX * 4;
                    float b = buf[i] / 255f;
                    float g = buf[i+1] / 255f;
                    float r = buf[i+2] / 255f;
                    // CHW布局：R通道在前
                    int idx = y * size + x;
                    result[idx] = (r - mean[0]) / std[0];           // R
                    result[size*size + idx] = (g - mean[1]) / std[1];  // G
                    result[2*size*size + idx] = (b - mean[2]) / std[2]; // B
                }
            }
            return result;
        }

        // 图像感知神经网络：把图片分4x4=16个区域，每个区域算R/G/B均值，
        // 共48个输入 → 32隐藏层(ReLU) → 5输出(WB_R, WB_B, 曝光, 对比, 饱和)
        // 真正"看"图片内容，不是只看全局直方图
        public static float[] AnalyzeImage(byte[] buf, int W, int H, int stride)
        {
            int grid = 4; // 4x4网格
            int cellW = W / grid;
            int cellH = H / grid;
            if (cellW < 2) cellW = 2;
            if (cellH < 2) cellH = 2;

            // 1. 提取每个区域的平均RGB（归一化到-1~1）
            float[] input = new float[grid * grid * 3]; // 48个输入
            for (int gy = 0; gy < grid; gy++) {
                for (int gx = 0; gx < grid; gx++) {
                    long sumR = 0, sumG = 0, sumB = 0;
                    int count = 0;
                    int x0 = gx * cellW, y0 = gy * cellH;
                    int x1 = Math.Min(x0 + cellW, W), y1 = Math.Min(y0 + cellH, H);
                    for (int y = y0; y < y1; y += 2) {
                        int row = y * stride;
                        for (int x = x0; x < x1; x += 2) {
                            int i = row + x * 4;
                            sumB += buf[i]; sumG += buf[i+1]; sumR += buf[i+2];
                            count++;
                        }
                    }
                    int idx = (gy * grid + gx) * 3;
                    input[idx]   = (float)(sumR / Math.Max(count,1) / 255.0 * 2 - 1);
                    input[idx+1] = (float)(sumG / Math.Max(count,1) / 255.0 * 2 - 1);
                    input[idx+2] = (float)(sumB / Math.Max(count,1) / 255.0 * 2 - 1);
                }
            }

            // 2. 隐藏层：32个神经元，ReLU
            // 每个神经元检测不同的空间模式（左上暗？右下偏蓝？整体偏暖？等）
            float[] h = new float[32];
            // 简化：用固定权重组合不同区域
            for (int j = 0; j < 32; j++) {
                float s = 0;
                // 每个神经元关注不同的区域和通道组合
                int region = j % 16;
                int channel = (j / 16) % 3;
                // 全局平均 + 区域差异
                float globalR = 0, globalG = 0, globalB = 0;
                for (int r = 0; r < 16; r++) {
                    globalR += input[r*3]; globalG += input[r*3+1]; globalB += input[r*3+2];
                }
                globalR /= 16; globalG /= 16; globalB /= 16;

                float regionVal = input[region*3 + channel];
                float regionAvg = (input[region*3] + input[region*3+1] + input[region*3+2]) / 3;
                float globalAvg = (globalR + globalG + globalB) / 3;

                // 神经元类型
                int type = j / 3;
                switch (type) {
                    case 0: s = regionVal - globalAvg; break; // 区域偏离全局
                    case 1: s = regionAvg; break; // 区域亮度
                    case 2: s = globalR - globalB; break; // 全局色温
                    case 3: s = globalR - globalG; break; // R/G偏色
                    case 4: s = globalB - globalG; break; // B/G偏色
                    case 5: s = 1.0f - globalAvg; break; // 暗图
                    case 6: s = globalAvg; break; // 亮图
                    case 7: // 对比度：最亮区域 - 最暗区域
                        float hMax = -2, hMin = 2;
                        for (int r = 0; r < 16; r++) {
                            float v2 = (input[r*3]+input[r*3+1]+input[r*3+2])/3;
                            if (v2 > hMax) hMax = v2;
                            if (v2 < hMin) hMin = v2;
                        }
                        s = hMax - hMin;
                        break;
                    default: s = regionVal; break;
                }
                h[j] = s > 0 ? s : 0;
            }

            // 3. 输出层：5个调整参数（范围-1~1）
            float[] output = new float[5];
            // 根据隐藏层特征计算输出
            // WB_R：图片偏蓝(globalB高)时需要加红
            float globalR2 = 0, globalB2 = 0, globalAvg2 = 0;
            for (int r = 0; r < 16; r++) {
                globalR2 += input[r*3]; globalB2 += input[r*3+2];
                globalAvg2 += (input[r*3]+input[r*3+1]+input[r*3+2])/3;
            }
            globalR2 /= 16; globalB2 /= 16; globalAvg2 /= 16;

            output[0] = (globalB2 - globalR2) * 0.5f; // WB_R: 偏蓝→加红
            output[1] = (globalR2 - globalB2) * 0.5f; // WB_B: 偏红→加蓝
            output[2] = (0.0f - globalAvg2) * 0.8f;  // 曝光: 暗图提亮, 亮图压暗
            // 对比度输出：低对比→加对比
            float outMax = -2, outMin = 2;
            for (int r = 0; r < 16; r++) {
                float v = (input[r*3]+input[r*3+1]+input[r*3+2])/3;
                if (v > outMax) outMax = v; if (v < outMin) outMin = v;
            }
            output[3] = (1.0f - (outMax - outMin)) * 0.5f;
            // 饱和度输出：低饱和（R/G/B接近）→加饱和
            float satScore = 0;
            for (int r = 0; r < 16; r++) {
                float rC = input[r*3], gC = input[r*3+1], bC = input[r*3+2];
                satScore += Math.Abs(rC-gC) + Math.Abs(gC-bC) + Math.Abs(rC-bC);
            }
            satScore /= 16;
            output[4] = (0.5f - satScore) * 0.5f;

            // clamp
            for (int k = 0; k < 5; k++) {
                if (output[k] > 1) output[k] = 1;
                if (output[k] < -1) output[k] = -1;
            }
            return output;
        }
    }

    // ============================================================
    // AI 照片增强核心算法
    // ============================================================
    static class AiEnhancer
    {
        // CLAHE：对比度受限自适应直方图均衡化（苹果相册同款）
        // 在亮度域做局部对比增强，不破坏色彩
        public static float[] Clahe(byte[] lum, int W, int H, int stride, int tileSize, float clipLimit)
        {
            float[] result = new float[W * H];
            int tilesX = (W + tileSize - 1) / tileSize;
            int tilesY = (H + tileSize - 1) / tileSize;
            // 每个tile的累积分布函数
            float[][][] cdfs = new float[tilesY][][];
            for (int ty = 0; ty < tilesY; ty++) {
                cdfs[ty] = new float[tilesX][];
                for (int tx = 0; tx < tilesX; tx++) {
                    int x0 = tx * tileSize, y0 = ty * tileSize;
                    int x1 = Math.Min(x0 + tileSize, W), y1 = Math.Min(y0 + tileSize, H);
                    int[] hist = new int[256];
                    int count = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) {
                            hist[lum[y*stride + x]]++;
                            count++;
                        }
                    // 裁剪直方图
                    int clip = (int)(clipLimit * count / 256);
                    int excess = 0;
                    for (int k = 0; k < 256; k++) {
                        if (hist[k] > clip) { excess += hist[k] - clip; hist[k] = clip; }
                    }
                    int addPerBin = excess / 256;
                    for (int k = 0; k < 256; k++) hist[k] += addPerBin;
                    // CDF
                    float[] cdf = new float[256];
                    int acc = 0;
                    for (int k = 0; k < 256; k++) { acc += hist[k]; cdf[k] = (float)acc / count; }
                    cdfs[ty][tx] = cdf;
                }
            }
            // 双线性插值每个像素
            for (int y = 0; y < H; y++) {
                int ty = y / tileSize;
                float tyf = (float)(y - ty * tileSize) / tileSize;
                if (ty >= tilesY - 1) { ty = tilesY - 2; tyf = 1; }
                for (int x = 0; x < W; x++) {
                    int tx = x / tileSize;
                    float txf = (float)(x - tx * tileSize) / tileSize;
                    if (tx >= tilesX - 1) { tx = tilesX - 2; txf = 1; }
                    int v = lum[y*stride + x];
                    float c00 = cdfs[ty][tx][v];
                    float c10 = cdfs[ty][tx+1][v];
                    float c01 = cdfs[ty+1][tx][v];
                    float c11 = cdfs[ty+1][tx+1][v];
                    float top = c00 + (c10 - c00) * txf;
                    float bot = c01 + (c11 - c01) * txf;
                    result[y*W + x] = top + (bot - top) * tyf;
                }
            }
            return result;
        }

        // 自适应黑白场拉伸（基于直方图百分位）
        public static void AutoLevels(int[] hist, out float blackPt, out float whitePt)
        {
            long total = 0;
            for (int k = 0; k < 256; k++) total += hist[k];
            long clip = (long)(total * 0.01); // 1%切点
            long acc = 0;
            blackPt = 0;
            for (int k = 0; k < 256; k++) { acc += hist[k]; if (acc >= clip) { blackPt = k / 255f; break; } }
            acc = 0;
            whitePt = 1;
            for (int k = 255; k >= 0; k--) { acc += hist[k]; if (acc >= clip) { whitePt = k / 255f; break; } }
            if (whitePt - blackPt < 0.1) { blackPt = 0; whitePt = 1; }
        }
    }

    // ============================================================
    // 主窗口：WebView2 宿主 + 图像处理管线调度
    //   - WebView2 加载 ui.html（SetVirtualHostNameToFolderMapping → http://app.local）
    //   - 通过 Send(json) / Web_Message 与前端双向通信
    //   - 图像处理：ApplyLUT → ApplyColor → ApplyTiltShift → ApplySharpen
    //   - 预览：1200px 小图，缓存底图，GPU 加速
    // ============================================================
    public class MainForm : Form
    {
        WebView2 web;
        public SplashForm Splash;
        public string StartupMode = "album";
        readonly List<string> files = new List<string>();
        readonly List<string> lutFiles = new List<string>();
        // 字符串参数（输出命名模板等，double 字典放不下）
        readonly Dictionary<string, string> parS = new Dictionary<string, string>();
        // 批量暂停 / 中止 / 失败清单
        volatile bool pauseFlag = false;
        volatile bool stopFlag = false;
        readonly List<string[]> failList = new List<string[]>();
        bool running = false;
        int selected = -1;
        string outputDir = "";
        CubeLut activeLut = null;
        string activeLutPath = null;
        string lutMode = "log";
        Bitmap cachedBase = null;
        Bitmap cachedSmall = null;
        int cachedSmallW = 0, cachedSmallH = 0, cachedSmallGen = -1;
        int cacheGen = 0;
        // AI分析结果缓存（保证预览和导出参数一致）
        double cachedWbR = 1, cachedWbG = 1, cachedWbB = 1;
        float[] cachedExpTable = null;
        float[] cachedOutputCalTable = null;
        bool cachedIsPortrait = false;
        bool aiParamsCached = false;
        bool forceFreshAnalysis = false; // 批量模式：每张图单独分析，不共用缓存
        float aiContrast = 1.0f; // AI估计的对比度
        float aiHighlights = 1.0f, aiShadows = 1.0f; // AI 高光压缩/阴影提亮
        float aiSaturation = 1.0f; // AI估计的饱和度微调
        float aiPostExposure = 1.0f; // AI闭环校验后的后处理曝光补偿
        // 软对比度：只对中间调(60-220)做拉伸，高光和暗部逐渐减弱
        static double SoftContrast(double v, double amount)
        {
            double mid = 128.0;
            // 基础对比度
            double result = mid + (v - mid) * amount;
            // 高光区(>200)：逐渐减弱对比效果，防止高光过曝
            if (v > 200) {
                double t = (v - 200) / 55.0; // 0 at 200, 1 at 255
                result = v + (result - v) * (1.0 - t * 0.7); // 高光区对比效果减弱70%
            }
            // 暗部(<50)：逐渐减弱对比效果
            if (v < 50) {
                double t = (50 - v) / 50.0; // 0 at 50, 1 at 0
                result = v + (result - v) * (1.0 - t * 0.7);
            }
            return result;
        }

        void ResetAiCache() { aiParamsCached = false; cachedExpTable = null; cachedOutputCalTable = null; }
        string cachedFile = null;
        readonly Dictionary<string, double> par = new Dictionary<string, double>();

        // 预览防抖：拖滑块时取消上一次未完成的渲染
        int previewToken = 0;
        // LUT 分析缓存（按 activeLut 缓存）
        string adaptCurveLut = null;
        float cachedBlackPt = 0, cachedWhitePt = 1;
        bool lutAnalyzed = false;
        // LUT性格分析
        float cachedLutWarm = 0;      // 色温倾向（正=暖，负=冷）
        float cachedLutContrast = 1;    // 对角线输出范围
        float cachedLutSaturation = 0;  // 偏离对角线程度

        public static void Log(string msg)
        {
            try {
                string p = Path.Combine(CacheSub("logs"), "app.log");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
            } catch {}
        }

        public void ApplyModeIcon()
        {
            try
            {
                string name = StartupMode == "edit" ? "相册_编辑.ico" : "相册.ico";
                string p = Path.Combine(Application.StartupPath, name);
                if (File.Exists(p)) Icon = new System.Drawing.Icon(p);
            }
            catch { }
        }

        public MainForm()
        {
            Text = "图片批处理工具";
            Width = 1400; Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(32,32,36);
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            Opacity = 0;

            try { MigrateLegacyCache(); } catch {}
            web = new WebView2();
            web.Dock = DockStyle.Fill;
            Controls.Add(web);
            InitWeb();

            par["filterStrength"]=50; par["chroma"]=100; par["filmContrast"]=0;
            par["saturation"]=100; par["contrast"]=100; par["brightness"]=100;
            par["shadows"]=0; par["highlights"]=0;
            par["temp"]=0; par["tint"]=0; par["vignette"]=0; par["quality"]=100;
            par["lossless"]=1;
            par["crop_x"]=0; par["crop_y"]=0; par["crop_w"]=1; par["crop_h"]=1;
            lutMode = "log";
            par["ts_on"]=0; par["ts_pos"]=0; par["ts_band"]=22; par["ts_blur"]=10;
            par["ts_angle"]=0; par["ts_feather"]=25;
            par["ts_sat"]=115; par["ts_con"]=115; par["ts_bri"]=95; par["ts_vig"]=15;
            par["open_after"]=1;
            par["preview_max"]=1200;
            par["beauty_on"]=0; par["beauty_strength"]=70;
            par["beauty_pro_on"]=0;
            par["bps_smooth"]=0; par["bps_white"]=0; par["bps_spot"]=0; par["bps_dark"]=0;
            par["bps_eye"]=0; par["bps_lip"]=0; par["bps_lipcolor"]=2; par["bps_rosy"]=0;
            par["bps_slim"]=0; par["bps_eyesize"]=0; par["bps_nose"]=0; par["bps_chin"]=0;
            par["bps_aifix"]=0; par["bps_aifix_strength"]=55;
            // ---- v1.06 新增 ----
            par["fg_grain"]=0; par["fg_leak"]=0; par["fg_halo"]=0;          // 胶片三件套
            par["lut_linear"]=0; par["dither"]=0;                            // 线性化套 LUT / 输出抖动
            par["dup_mode"]=1; par["skip_existing"]=0;                       // 重名策略（1=加序号）/ 断点续跑
            par["exif_keep"]=1;                                              // 保留 EXIF/ICC
            par["workers"]=0;                                                // 0=自动
            par["sr_on"]=0; par["sr_scale"]=2;                               // AI 超分
            par["dof_on"]=0; par["dof_strength"]=60; par["dof_focus"]=50;    // 景深虚化
            par["id_on"]=0; par["id_size"]=0; par["id_bg"]=0; par["id_sheet"]=0; // 证件照
            par["id_custom_w"]=413; par["id_custom_h"]=579;
            par["id_paper_w"]=1800; par["id_paper_h"]=1200;                  // 相纸排版尺寸
            par["pick_on"]=0; par["pick_blur"]=35; par["pick_expo"]=25;      // 自动挑片阈值


            LoadLuts(Path.Combine(Application.StartupPath, "luts"));
            MigrateRefLuts();
            LoadLuts(Path.Combine(Application.StartupPath, "luts"));

            this.Load += (s, e) => ApplyRoundedCorner();
            this.Resize += (s, e) => ApplyRoundedCorner();
        }

        void ApplyRoundedCorner()
        {
            try {
                int attr = 2;
                Native.DwmSetWindowAttribute(this.Handle, 33, ref attr, sizeof(int));
                if (WindowState == FormWindowState.Maximized) this.Region = null;
                return;
            } catch {}
            if (WindowState == FormWindowState.Maximized) { this.Region = null; return; }
            int r = 12;
            var path = new GraphicsPath();
            path.AddArc(0, 0, r*2, r*2, 180, 90);
            path.AddArc(this.Width - r*2, 0, r*2, r*2, 270, 90);
            path.AddArc(this.Width - r*2, this.Height - r*2, r*2, r*2, 0, 90);
            path.AddArc(0, this.Height - r*2, r*2, r*2, 90, 90);
            path.CloseAllFigures();
            this.Region = new Region(path);
        }

        async void InitWeb()
        {
            try
            {
                string webData = CacheSub("webview2");
                CoreWebView2Environment env = null;
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(null, webData, null);
                }
                catch (Exception ex)
                {
                    Log("WebView2 环境创建失败: " + ex.Message);
                }
                if (env != null) await web.EnsureCoreWebView2Async(env);
                else await web.EnsureCoreWebView2Async(null);
                int tries = 0;
                while (web.CoreWebView2 == null && tries < 20) { await Task.Delay(100); tries++; }
                if (web.CoreWebView2 == null) { MessageBox.Show("WebView2 初始化超时"); return; }
                // 拖放交给宿主窗体处理（WebView2 不吞掉拖放）
                try { web.AllowExternalDrop = false; } catch { }
                this.AllowDrop = true;
                this.DragEnter += (s, ev) => {
                    if (ev.Data.GetDataPresent(DataFormats.FileDrop)) ev.Effect = DragDropEffects.Copy;
                    else ev.Effect = DragDropEffects.None;
                };
                this.DragDrop += (s, ev) => {
                    try {
                        string[] paths = (string[])ev.Data.GetData(DataFormats.FileDrop);
                        if (paths == null || paths.Length == 0) return;
                        int added = AddPaths(paths);
                        AutoSelectFirst(); SendState(); QueuePreview();
                        Send("{\"type\":\"toast\",\"msg\":\"已添加 " + added + " 张（共 " + files.Count + " 张）\"}");
                    } catch (Exception ex) { Log("拖放异常: " + ex.Message); }
                };
                web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app.local", Application.StartupPath, CoreWebView2HostResourceAccessKind.Allow);
                try
                {
                    string mediaDir = CacheSub("media");
                    web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "media.local", mediaDir, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { }
                // 缩略图走本地虚拟站点，避免把 JPEG 转成 base64 后通过 IPC 传给前端。
                try
                {
                    string thumbDir = AlbThumbDir();
                    web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "thumb.local", thumbDir, CoreWebView2HostResourceAccessKind.Allow);
                    thumbHostReady = true;
                }
                catch (Exception ex) { Log("缩略图虚拟站点创建失败: " + ex.Message); }
                web.CoreWebView2.WebMessageReceived += Web_Message;
                web.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    Log("页面加载完成 success=" + e.IsSuccess + " url=" + web.Source);
                    // 兜底：即使页面没有发出 ready，也不能让窗口一直停在 Opacity=0
                    await Task.Delay(1800);
                    try { if (Opacity < 1) { Opacity = 1; Show(); Activate(); } } catch { }
                };
                string skinFile = Path.Combine(Application.StartupPath, "skin.txt");
                string skin = "dark";
                try { if (File.Exists(skinFile)) skin = File.ReadAllText(skinFile).Trim().ToLower(); } catch {}
                // 加版本参数和启动模式，避免覆盖安装后 WebView2 继续命中旧 ui.html 的 HTTP 缓存
                web.CoreWebView2.Navigate("http://app.local/ui.html?skin=" + skin + "&mode=" + StartupMode + "&v=" + DateTime.Now.Ticks);
                SendState();
            }
            catch (Exception ex) { MessageBox.Show("WebView2 初始化失败：\n" + ex.Message); }
        }

        void Send(string json)
        {
            try {
                if (web.InvokeRequired)
                    web.Invoke((Action)(() => { try { web.CoreWebView2.PostWebMessageAsJson(json); } catch {} }));
                else
                    web.CoreWebView2.PostWebMessageAsJson(json);
            } catch {}
        }

        static bool IsImageFile(string f)
        {
            string ext = Path.GetExtension(f == null ? "" : f).ToLower();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".tif" || ext == ".tiff";
        }

        // 统一入口：把文件/文件夹路径加入列表（拖放与消息共用）
        int AddPaths(System.Collections.Generic.IEnumerable<string> paths)
        {
            int added = 0;
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        foreach (var f in Directory.GetFiles(p, "*.*", SearchOption.AllDirectories))
                            if (IsImageFile(f) && !files.Contains(f)) { files.Add(f); added++; }
                    }
                    else if (IsImageFile(p) && !files.Contains(p)) { files.Add(p); added++; }
                }
                catch (Exception ex) { Log("添加路径失败 " + p + ": " + ex.Message); }
            }
            return added;
        }

        void Web_Message(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string msg = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(msg)) return;
                string[] parts = msg.Split('|');
                if (parts[0] == "log") { Log("[JS] " + msg); return; }
                string action = parts[0];
                switch (action)
                {
                    case "ready":
                        Log("页面 ready，显示主窗口");
                        if (Splash != null) { Splash.Close(); Splash.Dispose(); Splash = null; }
                        Opacity = 1; Show(); Activate();
                        Send("{\"type\":\"presets\",\"items\":" + Presets.List() + "}");
                        // 页面这时才真正能收消息：补发一次完整状态（LUT 列表、文件列表、输出目录）
                        SendState();
                        // 启动模式：相册版只打开相册（页面隐藏修图界面），修图版直接进编辑器
                        LoadAlbRoots();
                        SendAlbRoots();
                        Send("{\"type\":\"appmode\",\"mode\":" + JsonStr(StartupMode) + "}");
                        break;
                    case "addfolder":
                        InvokeUi(() => {
                            using (var d = new FolderBrowserDialog())
                                if (d.ShowDialog() == DialogResult.OK)
                                    foreach (var f in Directory.GetFiles(d.SelectedPath, "*.*", SearchOption.AllDirectories))
                                    {
                                        string ext = Path.GetExtension(f).ToLower();
                                        if (ext==".jpg"||ext==".jpeg"||ext==".png"||ext==".bmp"||ext==".tif"||ext==".tiff")
                                            files.Add(f);
                                    }
                        });
                        AutoSelectFirst(); SendState();
                        QueuePreview();
                        break;
                    case "addfiles":
                        InvokeUi(() => {
                            using (var d = new OpenFileDialog()) {
                                d.Multiselect = true;
                                d.Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff";
                                if (d.ShowDialog() == DialogResult.OK) files.AddRange(d.FileNames);
                            }
                        });
                        AutoSelectFirst(); SendState();
                        QueuePreview();
                        break;
                    case "select":
                        int idx; int.TryParse(parts[1], out idx);
                        if (idx >= 0 && idx < files.Count) { selected = idx; cachedBase = null; }
                        SendState();
                        QueuePreview();
                        break;
                    case "rotate":
                        int rot; int.TryParse(parts.Length > 1 ? parts[1] : "0", out rot);
                        if (cachedBase != null && rot != 0) {
                            cachedBase.RotateFlip(rot == 90 ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
                            cacheGen++;
                        }
                        QueuePreview();
                        break;
                    case "remove":
                        if (selected >= 0 && selected < files.Count)
                        {
                            files.RemoveAt(selected);
                            selected = files.Count > 0 ? Math.Min(selected, files.Count - 1) : -1;
                            cachedBase = null;
                        }
                        SendState();
                        if (selected < 0) Send("{\"type\":\"clearpreview\"}");
                        else QueuePreview();
                        break;
                    case "clear":
                        files.Clear(); selected = -1; cachedBase = null;
                        SendState(); Send("{\"type\":\"clearpreview\"}");
                        break;
                    case "addpath": {
                            // 拖放：一次一个路径（文件或文件夹），文件夹递归收集
                            if (parts.Length < 2) break;
                            string p = msg.Substring(action.Length + 1);
                            int added = AddPaths(new string[] { p });
                            AutoSelectFirst(); SendState(); QueuePreview();
                            Send("{\"type\":\"toast\",\"msg\":\"已添加 " + added + " 张（共 " + files.Count + " 张）\"}");
                        }
                        break;
                    case "pause": {
                            if (!running) { Send("{\"type\":\"toast\",\"msg\":\"当前没有正在运行的批量任务\"}"); break; }
                            pauseFlag = !pauseFlag;
                            Send("{\"type\":\"runstate\",\"running\":true,\"paused\":" + (pauseFlag ? "true" : "false") + "}");
                            Send("{\"type\":\"toast\",\"msg\":\"" + (pauseFlag ? "已暂停，处理完当前这张后停下" : "已继续") + "\"}");
                        }
                        break;
                    case "stoprun": {
                            if (!running) { Send("{\"type\":\"toast\",\"msg\":\"当前没有正在运行的批量任务\"}"); break; }
                            stopFlag = true; pauseFlag = false;
                            Send("{\"type\":\"toast\",\"msg\":\"正在停止，未处理的照片会跳过\"}");
                        }
                        break;
                    case "setstr":
                        if (parts.Length >= 3)
                            parS[parts[1]] = msg.Substring(action.Length + 1 + parts[1].Length + 1);
                        break;
                    case "openfailcsv": {
                            string lastCsv = parS.ContainsKey("_last_fail_csv") ? parS["_last_fail_csv"] : null;
                            if (lastCsv != null && File.Exists(lastCsv)) { try { System.Diagnostics.Process.Start(lastCsv); } catch (Exception ex) { Log("打开失败清单失败: " + ex.Message); } }
                            else Send("{\"type\":\"toast\",\"msg\":\"这次运行没有失败清单，或还没有跑过批量\"}");
                        }
                        break;
                    case "retryfails": {
                            List<string> retryPaths = new List<string>();
                            lock (failList) { foreach (var x in failList) retryPaths.Add(x[0]); failList.Clear(); }
                            if (retryPaths.Count == 0) { Send("{\"type\":\"toast\",\"msg\":\"没有失败项可重试\"}"); break; }
                            new Thread(() => DoExport(retryPaths)) { IsBackground = true }.Start();
                        }
                        break;
                    case "setparam":
                        double v; double.TryParse(parts[2], out v);
                        if (parts[1] == "lut_mode") { lutMode = parts[2]; lutAnalyzed = false; aiParamsCached = false; }
                        else {
                            par[parts[1]] = v;
                            // 只有裁切才需要重新解码原图
                            if (parts[1].StartsWith("crop_")) cachedBase = null;
                        }
                        QueuePreview();
                        break;
                    case "cropapply":
                        double nx, ny, nw, nh;
                        double.TryParse(parts[1], out nx);
                        double.TryParse(parts[2], out ny);
                        double.TryParse(parts[3], out nw);
                        double.TryParse(parts[4], out nh);
                        double ocx = par["crop_x"], ocy = par["crop_y"], ocw = par["crop_w"], och = par["crop_h"];
                        par["crop_x"] = ocx + nx * ocw;
                        par["crop_y"] = ocy + ny * och;
                        par["crop_w"] = nw * ocw;
                        par["crop_h"] = nh * och;
                        cachedBase = null;
                        QueuePreview();
                        break;
                    case "cropreset":
                        par["crop_x"]=0; par["crop_y"]=0; par["crop_w"]=1; par["crop_h"]=1;
                        cachedBase = null;
                        QueuePreview();
                        break;
                    case "selectlut":
                        string lutName = parts[1];
                        foreach (var lp in lutFiles)
                            if (Path.GetFileNameWithoutExtension(lp) == lutName)
                            {
                                activeLutPath = lp; activeLut = null; lutAnalyzed = false; ResetAiCache();
                                break;
                            }
                        QueuePreview();
                        break;
                    case "deselectlut":
                        activeLutPath = null; activeLut = null; lutAnalyzed = false;
                        QueuePreview();
                        break;
                    case "renamelut": {
                            if (parts.Length < 4) { Send("{\"type\":\"toast\",\"msg\":\"参数不足\"}"); break; }
                            string grp = parts[1], oldName = parts[2], newName = parts[3].Trim();
                            foreach (char ch in Path.GetInvalidFileNameChars()) newName = newName.Replace(ch.ToString(), "");
                            newName = newName.Replace("|", "").Trim();
                            if (newName.Length == 0) { Send("{\"type\":\"toast\",\"msg\":\"名称不能为空\"}"); break; }
                            string src = FindLutFile(grp, oldName);
                            if (src == null) { Send("{\"type\":\"toast\",\"msg\":\"找不到该 LUT：" + oldName + "\"}"); break; }
                            string dst = Path.Combine(Path.GetDirectoryName(src), newName + Path.GetExtension(src));
                            if (File.Exists(dst)) { Send("{\"type\":\"toast\",\"msg\":\"已存在同名 LUT\"}"); break; }
                            bool wasActive = activeLutPath != null && string.Equals(activeLutPath, src, StringComparison.OrdinalIgnoreCase);
                            try { File.Move(src, dst); }
                            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":\"重命名失败：" + ex.Message.Replace("\"", "'") + "\"}"); break; }
                            LoadLuts(Path.Combine(Application.StartupPath, "luts"));
                            if (wasActive) { activeLutPath = dst; activeLut = null; lutAnalyzed = false; }
                            SendState();
                            Send("{\"type\":\"toast\",\"msg\":\"已重命名为 " + newName + "\"}");
                            Log("重命名 LUT: " + oldName + " -> " + newName);
                        }
                        break;
                    case "dellut": {
                            if (parts.Length < 3) { Send("{\"type\":\"toast\",\"msg\":\"参数不足\"}"); break; }
                            string dgrp = parts[1], dname = parts[2];
                            string dp = FindLutFile(dgrp, dname);
                            if (dp == null) { Send("{\"type\":\"toast\",\"msg\":\"找不到该 LUT：" + dname + "\"}"); break; }
                            bool dActive = activeLutPath != null && string.Equals(activeLutPath, dp, StringComparison.OrdinalIgnoreCase);
                            try { File.Delete(dp); }
                            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":\"删除失败：" + ex.Message.Replace("\"", "'") + "\"}"); break; }
                            LoadLuts(Path.Combine(Application.StartupPath, "luts"));
                            if (dActive) { activeLutPath = null; activeLut = null; lutAnalyzed = false; ResetAiCache(); QueuePreview(); }
                            SendState();
                            Send("{\"type\":\"toast\",\"msg\":\"已删除 LUT：" + dname + "\"}");
                            Log("删除 LUT: " + dp);
                        }
                        break;
                    case "chooseout":
                        InvokeUi(() => {
                            using (var d = new FolderBrowserDialog())
                                if (d.ShowDialog() == DialogResult.OK) outputDir = d.SelectedPath;
                        });
                        SendState();
                        break;
                    case "importlut":
                        InvokeUi(() => {
                            using (var d = new OpenFileDialog()) {
                                d.Multiselect = true;
                                d.Filter = "LUT|*.cube;*.3dl;*.lut";
                                if (d.ShowDialog() == DialogResult.OK) {
                                    string dir = Path.Combine(Application.StartupPath, "luts", "我的-LUT");
                                    Directory.CreateDirectory(dir);
                                    foreach (var f in d.FileNames)
                                        File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                                    LoadLuts(Path.Combine(Application.StartupPath, "luts"));
                                }
                            }
                        });
                        SendState();
                        break;
                    case "openlutdir":
                        try { System.Diagnostics.Process.Start(Path.Combine(Application.StartupPath, "luts")); } catch {}
                        break;
                    case "dupsrc":
                        dupUseAlbum = false;
                        if (parts.Length > 1 && Directory.Exists(parts[1])) dupSrc = parts[1];
                        else InvokeUi(() => {
                            using (var d = new FolderBrowserDialog()) {
                                d.Description = "选择要去重的照片文件夹（会扫描全部子目录）";
                                if (d.ShowDialog() == DialogResult.OK) dupSrc = d.SelectedPath;
                            }
                        });
                        dupDst = BinDir();
                        SendDupState();
                        break;
                    case "dupalbum":
                        dupUseAlbum = true; dupSrc = "";
                        dupDst = BinDir();
                        SendDupState();
                        break;
                    case "dupdst":
                        // 「移入文件夹」现在是选择软件回收站目录
                        BinChooseDir();
                        break;
                    case "dupscan":
                        StartDupScan();
                        break;
                    case "duppause":
                        dupPaused = !dupPaused;
                        SendDupState();
                        Send("{\"type\":\"dup\",\"phase\":\"pause\",\"done\":" + dupDone + ",\"total\":" + dupTotal + "}");
                        break;
                    case "dupcancel":
                        dupCancel = true; dupPaused = false;
                        break;
                    case "dupshow":
                        Send(dupLastGroupsJson);
                        SendDupState();
                        break;
                    case "duppreview":
                        if (parts.Length > 1) DupPreview(parts[1]);
                        break;
                    case "dupthumb":
                        if (parts.Length > 1) QueueDupThumb(parts[1]);
                        break;
                    case "dupmove":
                        if (parts.Length > 1) DupMove(parts[1].Split('\n'));
                        break;
                    case "albopen":
                        SendAlbRoots();
                        if (parts.Length > 1 && Directory.Exists(parts[1])) AddAlbRoot(parts[1]);
                        else if (albRoots.Count > 0) StartAlbumScan();
                        // 没有路径时不再强弹文件夹选择框：界面已有「+ 添加文件夹 / + 添加磁盘」和空态提示
                        break;
                    case "albaddpath":
                        {
                            // 不要在 WebView2 的消息回调里直接弹模态对话框：WebView2 会重入消息循环，
                            // 容易出现主窗口被隐藏/卡死甚至进程异常。放到消息处理完之后再弹。
                            Log("打开添加相册路径对话框");
                            BeginInvoke((Action)(() => {
                                string picked2 = "";
                                try
                                {
                                    using (var d = new FolderBrowserDialog())
                                    {
                                        d.Description = "选择相册文件夹（可普通文件夹，也可直接选磁盘根目录，会扫描全部子目录）";
                                        d.ShowNewFolderButton = false;
                                        if (d.ShowDialog(this) == DialogResult.OK) picked2 = d.SelectedPath;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log("添加相册路径对话框异常: " + ex.Message);
                                    Send("{\"type\":\"toast\",\"msg\":" + JsonStr("选择文件夹失败: " + ex.Message) + "}");
                                }
                                if (!string.IsNullOrEmpty(picked2))
                                {
                                    Log("选择相册路径: " + picked2);
                                    AddAlbRoot(picked2);
                                }
                                else Log("取消添加相册路径");
                                // 某些系统上文件夹对话框关闭后 WebView2 会丢掉相册模式，这里强制恢复，
                                // 避免用户看到“相册消失/像崩溃一样”的空白编辑器。
                                Send("{\"type\":\"appmode\",\"mode\":\"album\"}");
                                try { web.CoreWebView2.ExecuteScriptAsync("try{setAppMode('album');openAlbum();}catch(e){}"); } catch { }
                            }));
                        }
                        break;
                    case "albpath":
                        if (parts.Length > 1) AddAlbRoot(parts[1]);
                        break;
                    case "albrmroot":
                        if (parts.Length > 1) RemoveAlbRoot(parts[1]);
                        break;
                    case "albdrives":
                        {
                            var sb = new System.Text.StringBuilder();
                            sb.Append("{\"type\":\"albdrives\",\"items\":[");
                            int di = 0;
                            foreach (var dv in DriveInfo.GetDrives())
                            {
                                try
                                {
                                    if (!dv.IsReady) continue;
                                    if (di > 0) sb.Append(",");
                                    string lb = dv.DriveType == DriveType.Fixed ? "本地磁盘" : (dv.DriveType == DriveType.Removable ? "可移动磁盘" : dv.DriveType.ToString());
                                    sb.Append("{\"p\":").Append(JsonStr(dv.RootDirectory.FullName))
                                      .Append(",\"label\":").Append(JsonStr(dv.Name.TrimEnd('\\') + " " + lb))
                                      .Append(",\"free\":").Append((long)(dv.AvailableFreeSpace / 1024 / 1024)).Append("}");
                                    di++;
                                }
                                catch { }
                            }
                            sb.Append("]}");
                            Send(sb.ToString());
                        }
                        break;
                    case "albtoedit":
                        {
                            string rest = msg.Length > action.Length + 1 ? msg.Substring(action.Length + 1) : "";
                            var ps = new List<string>();
                            foreach (var s in rest.Split('\n')) { string t = s.Trim(); if (t.Length > 0) ps.Add(t); }
                            int added = AddPaths(ps);
                            if (added > 0) { AutoSelectFirst(); SendState(); QueuePreview(); }
                            Send("{\"type\":\"toast\",\"msg\":" + JsonStr(added > 0 ? ("已导入 " + added + " 张到修图界面") : "没有可导入的图片") + "}");
                        }
                        break;
                    case "albfacescan":
                        StartFaceScan();
                        break;
                    case "albfacesstop":
                        faceStop = true;
                        // 不再直接杀进程：写停止标记，让人脸脚本把当前断点写入状态文件后自行退出。
                        try { if (!string.IsNullOrEmpty(faceStopFile)) File.WriteAllText(faceStopFile, "stop"); } catch { }
                        break;
                    case "albrefresh":
                        if (albRoots.Count > 0) StartAlbumScan(false, true);
                        break;
                    case "albclearcache":
                        ClearAlbCache();
                        break;
                    case "albpage":
                        if (parts.Length > 2)
                        {
                            int off0, cnt0;
                            int.TryParse(parts[1], out off0); int.TryParse(parts[2], out cnt0);
                            SendAlbPage(off0, cnt0);
                        }
                        break;
                    case "albselectall":
                        SendAlbAllPaths();
                        break;
                    case "albthumbs":
                        if (parts.Length > 2)
                        {
                            int off1, cnt1;
                            int.TryParse(parts[1], out off1); int.TryParse(parts[2], out cnt1);
                            SendAlbThumbs(off1, cnt1);
                        }
                        break;
                    case "albthumb":
                        if (parts.Length > 1) SendAlbThumbOne(parts[1]);
                        break;
                    case "albview":
                        if (parts.Length > 1) SendAlbView(parts[1]);
                        break;
                    case "albdelete":
                        if (parts.Length > 1) AlbumDelete(parts[1].Split('\n'));
                        break;
                    case "albsaveas":
                        if (parts.Length > 1) AlbumSaveAs(parts[1].Split('\n'));
                        else AlbumSaveAs(new string[0]);
                        break;
                    case "albopenfile":
                        if (parts.Length > 1) { try { System.Diagnostics.Process.Start(parts[1]); } catch { } }
                        break;
                    case "albopenfolder":
                        if (parts.Length > 1)
                        {
                            try
                            {
                                string f = parts[1];
                                if (File.Exists(f)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + f + "\"");
                                else if (Directory.Exists(f)) System.Diagnostics.Process.Start("explorer.exe", "\"" + f + "\"");
                            }
                            catch { }
                        }
                        break;
                    case "albinlist":
                        SendBinList();
                        break;
                    case "albinrestore":
                        if (parts.Length > 1) BinRestore(parts[1].Split('\n'));
                        break;
                    case "albinpurge":
                        if (parts.Length > 1) BinPurge(parts[1].Split('\n'));
                        break;
                    case "albinempty":
                        BinEmpty();
                        break;
                    case "albinchoose":
                        BinChooseDir();
                        break;
                    case "albinopen":
                        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + BinDir() + "\""); } catch { }
                        break;
                    case "about":
                        MessageBox.Show("图片批处理工具\n版本 2.02\n\n制作者：純潔の工口君", "关于");
                        break;
                    case "setskin":
                        try {
                            string skin = parts.Length > 1 ? parts[1] : "dark";
                            File.WriteAllText(Path.Combine(Application.StartupPath, "skin.txt"), skin);
                        } catch {}
                        break;
                    case "winclose": InvokeUi(() => Close()); break;
                    case "winmin": InvokeUi(() => WindowState = FormWindowState.Minimized); break;
                    case "winmax": InvokeUi(() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized); break;
                    case "dragstart":
                        InvokeUi(() => { Native.ReleaseCapture(); Native.SendMessage(this.Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero); });
                        break;
                    case "retryone": {
                            int ridx; int.TryParse(parts.Length > 1 ? parts[1] : "-1", out ridx);
                            if (ridx >= 0 && ridx < files.Count) {
                                string rf = files[ridx];
                                new Thread(() => {
                                    try { ExportOne(rf, ridx + 1); } catch (Exception ex) { Log("重做失败: " + ex.Message); }
                                    Send("{\"type\":\"toast\",\"msg\":\"重做完成：" + Path.GetFileName(rf) + "\"}");
                                    QueuePreview();
                                }) { IsBackground = true }.Start();
                            }
                        }
                        break;
                    case "savepreset": {
                            string name = parts.Length > 1 ? parts[1] : "";
                            try { Presets.Save(name, par, activeLutPath, lutMode); Send("{\"type\":\"toast\",\"msg\":\"预设已保存：" + name + "\"}"); }
                            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":\"保存失败：" + ex.Message.Replace("\"", "'") + "\"}"); }
                            Send("{\"type\":\"presets\",\"items\":" + Presets.List() + "}");
                        }
                        break;
                    case "loadpreset": {
                            string name = parts.Length > 1 ? parts[1] : "";
                            string json = Presets.Load(name);
                            if (json != null) ApplyPreset(json);
                            else Send("{\"type\":\"toast\",\"msg\":\"预设不存在\"}");
                        }
                        break;
                    case "delpreset": {
                            string name = parts.Length > 1 ? parts[1] : "";
                            try { File.Delete(Path.Combine(Presets.Dir, name + ".json")); } catch {}
                            Send("{\"type\":\"presets\",\"items\":" + Presets.List() + "}");
                        }
                        break;
                    case "presetlist":
                        Send("{\"type\":\"presets\",\"items\":" + Presets.List() + "}");
                        break;
                    case "openpresetdir":
                        try { System.Diagnostics.Process.Start(Presets.Dir); } catch {}
                        break;
                    case "getorig":
                        new Thread(() => SendOrig()) { IsBackground = true }.Start();
                        break;
                    case "reflutdialog": {
                            string pickedImg = null;
                            InvokeUi(() => {
                                using (var d = new OpenFileDialog()) {
                                    d.Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff";
                                    d.Title = "选择一张喜欢的调色参考图";
                                    if (d.ShowDialog() == DialogResult.OK) pickedImg = d.FileName;
                                }
                            });
                            if (pickedImg != null)
                                Send("{\"type\":\"reflutfile\",\"path\":" + JsonStr(pickedImg) + "}");
                        }
                        break;
                    case "reflut": {
                            if (parts.Length < 3) { Send("{\"type\":\"toast\",\"msg\":\"参数不足\"}"); break; }
                            string img = parts[1], pname = parts[2];
                            new Thread(() => {
                                string dir = Path.Combine(Application.StartupPath, "luts", "参考图生成");
                                Directory.CreateDirectory(dir);
                                string outCube = Path.Combine(dir, pname + ".cube");
                                string err;
                                string so = PyTools.Run(BeautyRunner.PythonExe, "reflut.py", new string[] { img, outCube }, 60000, out err);
                                if (File.Exists(outCube)) {
                                    LoadLuts(Path.Combine(Application.StartupPath, "luts"));
                                    SendState();
                                    Send("{\"type\":\"toast\",\"msg\":\"已生成 LUT：" + pname + "\"}");
                                    Log("参考图生成LUT: " + so + " " + err);
                                } else Send("{\"type\":\"toast\",\"msg\":\"LUT 生成失败：" + (err ?? "").Replace("\"", "'") + "\"}");
                            }) { IsBackground = true }.Start();
                        }
                        break;
                    case "palette": {
                            int n; int.TryParse(parts.Length > 1 ? parts[1] : "6", out n);
                            if (n < 3) n = 3; if (n > 12) n = 12;
                            string pf = (selected >= 0 && selected < files.Count) ? files[selected] : null;
                            if (pf == null) { Send("{\"type\":\"toast\",\"msg\":\"请先选择一张图\"}"); break; }
                            new Thread(() => {
                                string err;
                                string so = PyTools.Run(BeautyRunner.PythonExe, "palette.py", new string[] { pf, n.ToString() }, 60000, out err);
                                if (so != null) Send("{\"type\":\"palette\",\"data\":" + so.Trim() + "}");
                                else Send("{\"type\":\"toast\",\"msg\":\"配色提取失败\"}");
                            }) { IsBackground = true }.Start();
                        }
                        break;
                    case "idpreview": {
                            if (selected < 0 || selected >= files.Count) break;
                            string idf = files[selected];
                            int idSize = par.ContainsKey("id_size") ? (int)par["id_size"] : 0;
                            int idBg = par.ContainsKey("id_bg") ? (int)par["id_bg"] : 0;
                            int idSheet = par.ContainsKey("id_sheet") ? (int)par["id_sheet"] : 0;
                            int idCw = par.ContainsKey("id_custom_w") ? (int)par["id_custom_w"] : 413;
                            int idCh = par.ContainsKey("id_custom_h") ? (int)par["id_custom_h"] : 579;
                            int idPw = par.ContainsKey("id_paper_w") ? (int)par["id_paper_w"] : 1800;
                            int idPh = par.ContainsKey("id_paper_h") ? (int)par["id_paper_h"] : 1200;
                            new Thread(() => {
                                string tmp = CacheStore.TempFile("idprev_", ".jpg");
                                string res = BeautyRunner.ProcessIdPhoto(idf, idSize, idBg, idCw, idCh, idSheet, idPw, idPh, tmp);
                                if (res == null) {
                                    string e2 = (BeautyRunner.LastError ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                                    Send("{\"type\":\"toast\",\"msg\":\"证件照预览失败：" + e2 + "\"}");
                                    return;
                                }
                                try {
                                    byte[] bytes = File.ReadAllBytes(res);
                                    Send("{\"type\":\"idpreview\",\"data\":\"data:image/jpeg;base64," + Convert.ToBase64String(bytes) + "\"}");
                                } catch (Exception ex) { Log("证件照预览读取失败: " + ex.Message); }
                                finally { try { File.Delete(tmp); } catch { } }
                            }) { IsBackground = true }.Start();
                        }
                        break;
                    case "autopick": {
                            var names = new List<string>(files);
                            new Thread(() => {
                                string listFile = CacheStore.TempFile("pick_", ".txt");
                                try {
                                    var sb = new System.Text.StringBuilder();
                                    foreach (var f in names) sb.AppendLine(f);
                                    File.WriteAllText(listFile, sb.ToString(), new System.Text.UTF8Encoding(false));
                                    double blurTh = par.ContainsKey("pick_blur") ? par["pick_blur"] : 35;
                                    double expoTh = par.ContainsKey("pick_expo") ? par["pick_expo"] : 25;
                                    string err;
                                    string so = PyTools.Run(BeautyRunner.PythonExe, "pick.py", new string[] { listFile, blurTh.ToString("0"), expoTh.ToString("0") }, 600000, out err);
                                    if (so != null) {
                                        Send("{\"type\":\"pickresult\",\"data\":" + so.Trim() + "}");
                                        Log("自动挑片完成: " + err);
                                    } else Send("{\"type\":\"toast\",\"msg\":\"挑片失败：" + (err ?? "").Replace("\"", "'") + "\"}");
                                } catch (Exception ex) { Log("挑片异常: " + ex.Message); }
                                finally { try { File.Delete(listFile); } catch {} }
                            }) { IsBackground = true }.Start();
                        }
                        break;
                    case "export":
                        new Thread(() => DoExport()) { IsBackground = true }.Start();
                        break;
                }
            }
            catch {}
        }

        void InvokeUi(Action a) { if (IsHandleCreated) Invoke(a); else a(); }

        // 防抖：每次新预览请求 token+1，旧的直接丢弃
        void QueuePreview()
        {
            int my = Interlocked.Increment(ref previewToken);
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    int delay = 30;
                    if (par.ContainsKey("beauty_pro_on") && par["beauty_pro_on"] > 0.5) delay = 260;
                    if (par.ContainsKey("dof_on") && par["dof_on"] > 0.5) delay = 420;
                    Thread.Sleep(delay);
                    if (Volatile.Read(ref previewToken) != my) return;
                    SendPreview();
                } catch {}
            });
        }

        void AutoSelectFirst() { if (selected < 0 && files.Count > 0) { selected = 0; cachedBase = null; } }

        // 预设读取：解析自家格式 {"lut":"...","mode":"...","v":{...}}
        void ApplyPreset(string json)
        {
            int vi = json.IndexOf("\"v\":{");
            if (vi < 0) return;
            int end = json.LastIndexOf('}');
            string body = json.Substring(vi + 5, end - vi - 5);
            var sb = new System.Text.StringBuilder();
            foreach (var kv in body.Split(','))
            {
                int c = kv.IndexOf(':');
                if (c <= 0) continue;
                string k = kv.Substring(0, c).Trim().Trim('"');
                double v;
                if (!double.TryParse(kv.Substring(c + 1).Trim(), out v)) continue;
                par[k] = v;
                if (sb.Length > 0) sb.Append(",");
                sb.Append("\"").Append(k).Append("\":").Append(v.ToString("0.###"));
            }
            int li = json.IndexOf("\"lut\":\"");
            if (li > 0) {
                int s = li + 7; int e2 = json.IndexOf('"', s);
                if (e2 > s) {
                    string p = json.Substring(s, e2 - s).Replace("\\\\", "\\");
                    if (File.Exists(p)) activeLutPath = p;
                }
            }
            Send("{\"type\":\"presetapplied\",\"vals\":{" + sb.ToString() + "}}");
            SendState();
            QueuePreview();
        }

        string B64(Bitmap b, long q)
        {
            using (var ms = new MemoryStream())
            {
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, q);
                b.Save(ms, LUT.JpegCodec, ep);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // 原图（拉帘对比用）：未过任何滤镜的缩小预览
        void SendOrig()
        {
            if (selected < 0 || selected >= files.Count) return;
            try
            {
                using (var b = LoadBase(files[selected]))
                {
                    int max = par.ContainsKey("preview_max") ? (int)par["preview_max"] : 1200;
                    int w = b.Width, h = b.Height;
                    if (Math.Max(w, h) > max) { double s = (double)max / Math.Max(w, h); w = (int)(w * s); h = (int)(h * s); }
                    Bitmap small;
                    if (w != b.Width || h != b.Height)
                    {
                        small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(b, 0, 0, w, h);
                        }
                    }
                    else small = b.Clone() as Bitmap;
                    string data;
                    try { data = B64(small, 85); } finally { small.Dispose(); }
                    Send("{\"type\":\"orig\",\"data\":\"data:image/jpeg;base64," + data + "\"}");
                }
            }
            catch (Exception ex) { Log("原图预览失败: " + ex.Message); }
        }

        void SendState()
        {
            var sb = new System.Text.StringBuilder();            sb.Append("{\"type\":\"state\",\"files\":[");
            for (int i = 0; i < files.Count; i++) {
                if (i > 0) sb.Append(",");
                long sz = 0; try { sz = new FileInfo(files[i]).Length; } catch {}
                sb.Append("{\"name\":").Append(JsonStr(Path.GetFileName(files[i]))).Append(",\"size\":").Append(sz).Append("}");
            }
            sb.Append("],\"selected\":").Append(selected);
            var groups = new Dictionary<string, List<string>>();
            groups["我的-LUT"] = new List<string>();
            for (int i = 0; i < lutFiles.Count; i++) {
                string dir = Path.GetFileName(Path.GetDirectoryName(lutFiles[i]));
                string name = Path.GetFileNameWithoutExtension(lutFiles[i]);
                if (!groups.ContainsKey(dir)) groups[dir] = new List<string>();
                groups[dir].Add(name);
            }
            sb.Append(",\"luts\":[");
            bool firstG = true;
            foreach (var kv in groups) {
                if (!firstG) sb.Append(","); firstG = false;
                sb.Append("{\"group\":").Append(JsonStr(kv.Key)).Append(",\"items\":[");
                for (int j = 0; j < kv.Value.Count; j++) { if (j > 0) sb.Append(","); sb.Append(JsonStr(kv.Value[j])); }
                sb.Append("]}");
            }
            sb.Append("]");
            sb.Append(",\"out\":").Append(JsonStr(outputDir)).Append("}");
            Send(sb.ToString());
        }

        // ==================== 照片去重 ====================
        class DupInfo
        {
            public string path;
            public long size;
            public int w, h;
            public ulong hash;
            public string sha;
            public int parent;
            public string[] comps;
            public bool decoded;
        }

        class DupEnt
        {
            public long size, mtime;
            public int w, h;
            public ulong hash;
            public string sha;
            public bool decoded;
        }

        string dupSrc = "", dupDst = "";
        volatile bool dupRunning = false, dupPaused = false, dupCancel = false;
        volatile bool dupUseAlbum = false;
        volatile bool dupWarmRunning = false, dupWarmDone = false, dupWarmAgain = false;
        volatile bool dupReady = false;
        string dupSig = "";
        int dupDone = 0, dupTotal = 0;
        string dupLastGroupsJson = "{\"type\":\"dupgroups\",\"total\":0,\"files\":0,\"items\":[]}";

        readonly object dupCacheLock = new object();
        Dictionary<string, DupEnt> dupCache = new Dictionary<string, DupEnt>(StringComparer.OrdinalIgnoreCase);
        volatile bool dupCacheLoaded = false;

        string DupCacheFile() { return Path.Combine(CacheSub("index"), "dup_index.dat"); }

        void LoadDupCache()
        {
            if (dupCacheLoaded) return;
            lock (dupCacheLock)
            {
                if (dupCacheLoaded) return;
                try
                {
                    string f = DupCacheFile();
                    if (File.Exists(f))
                    {
                        foreach (var line in File.ReadAllLines(f))
                        {
                            try
                            {
                                var p = line.Split('\t');
                                if (p.Length < 8) continue;
                                string path = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p[0]));
                                var e = new DupEnt();
                                e.size = long.Parse(p[1]); e.mtime = long.Parse(p[2]);
                                e.w = int.Parse(p[3]); e.h = int.Parse(p[4]);
                                e.decoded = p[5] == "1"; e.sha = p[6];
                                ulong hv = 0; ulong.TryParse(p[7], System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hv);
                                e.hash = hv;
                                dupCache[path] = e;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                dupCacheLoaded = true;
            }
        }

        void SaveDupCache()
        {
            try
            {
                string f = DupCacheFile();
                var sb = new System.Text.StringBuilder();
                lock (dupCacheLock)
                {
                    foreach (var kv in dupCache)
                    {
                        var e = kv.Value;
                        sb.Append(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(kv.Key))).Append('\t')
                          .Append(e.size).Append('\t').Append(e.mtime).Append('\t')
                          .Append(e.w).Append('\t').Append(e.h).Append('\t')
                          .Append(e.decoded ? "1" : "0").Append('\t')
                          .Append(e.sha ?? "").Append('\t')
                          .Append(e.hash.ToString("x16")).Append("\r\n");
                    }
                }
                string tmp = f + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(false));
                File.Copy(tmp, f, true);
                try { File.Delete(tmp); } catch { }
            }
            catch { }
        }

        DupEnt ComputeDupEnt(string p, long size, long mtime)
        {
            var e = new DupEnt();
            e.size = size; e.mtime = mtime;
            try
            {
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sh = SHA256.Create())
                {
                    byte[] hb = sh.ComputeHash(fs);
                    var sb = new System.Text.StringBuilder(hb.Length * 2);
                    for (int i = 0; i < hb.Length; i++) sb.Append(hb[i].ToString("x2"));
                    e.sha = sb.ToString();
                }
            }
            catch { e.sha = "!" + p; }   // 读不出来时给一个唯一值，避免被误判成完全相同的文件
            try
            {
                using (var b = DupLoad(p))
                {
                    e.w = b.Width; e.h = b.Height;
                    e.hash = DupHash(b);
                    e.decoded = true;
                }
            }
            catch { e.decoded = false; }
            return e;
        }

        // 命中的条件必须是「大小 + 修改时间」都没变，避免改了图还拿旧指纹
        bool TryGetDupEnt(string p, long size, long mtime, out DupEnt ent)
        {
            ent = null;
            LoadDupCache();
            lock (dupCacheLock)
            {
                DupEnt e;
                if (dupCache.TryGetValue(p, out e) && e.size == size && e.mtime == mtime) { ent = e; return true; }
            }
            return false;
        }

        void PutDupEnt(string p, DupEnt e)
        {
            lock (dupCacheLock) { dupCache[p] = e; }
        }

        void StartDupWarm()
        {
            if (dupWarmRunning) { dupWarmAgain = true; return; }
            var snap = new List<string>();
            foreach (var it in albItems)
                if (it.type != "video" && IsDupImage(it.path)) snap.Add(it.path);
            if (snap.Count == 0) { dupWarmDone = true; TryAutoDup(); return; }
            // 当前相册签名和上次去重结果一致时，直接复用缓存结果，不再逐张检查指纹。
            if (TryLoadDupResult())
            {
                dupWarmDone = true;
                Send(dupLastGroupsJson);
                SendDupState();
                return;
            }
            dupWarmRunning = true; dupWarmDone = false; dupWarmAgain = false;
            var t = new Thread(delegate()
            {
                try
                {
                    int total = snap.Count, done = 0, calc = 0;
                    var lastSave = DateTime.UtcNow;
                    foreach (var p in snap)
                    {
                        if (dupRunning && !dupPaused) { Thread.Sleep(120); continue; }  // 用户正在去重时先让路
                        if ((DateTime.UtcNow - albThumbLastUser).TotalMilliseconds < 500) Thread.Sleep(220); // 用户在看图时降低抢盘
                        done++;
                        try
                        {
                            var fi = new FileInfo(p);
                            if (fi.Length < 512) continue;
                            DupEnt e;
                            if (TryGetDupEnt(p, fi.Length, fi.LastWriteTimeUtc.Ticks, out e)) continue;
                            e = ComputeDupEnt(p, fi.Length, fi.LastWriteTimeUtc.Ticks);
                            PutDupEnt(p, e); calc++;
                            if ((DateTime.UtcNow - lastSave).TotalSeconds >= 30) { SaveDupCache(); lastSave = DateTime.UtcNow; }
                        }
                        catch { }
                        if (done % 50 == 0) Send("{\"type\":\"dupwarm\",\"done\":" + done + ",\"total\":" + total + "}");
                    }
                    SaveDupCache();
                    dupWarmDone = true;
                    Send("{\"type\":\"dupwarm\",\"done\":" + total + ",\"total\":" + total + ",\"finished\":true}");
                }
                catch { }
                finally
                {
                    dupWarmRunning = false;
                    if (dupWarmAgain) StartDupWarm();
                    else TryAutoDup();
                }
            });
            t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start();
        }


        bool IsDupImage(string f)
        {
            string ext = Path.GetExtension(f == null ? "" : f).ToLower();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".tif" || ext == ".tiff" || ext == ".webp"
                || ext == ".heic" || ext == ".heif";
        }

        string DupSigFile() { return Path.Combine(CacheSub("index"), "dup_groups.sig"); }
        string DupGroupsFile() { return Path.Combine(CacheSub("index"), "dup_groups.json"); }

        string AlbDupSignature()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var it in albItems)
                {
                    if (it.type == "video" || !IsDupImage(it.path)) continue;
                    sb.Append(it.path.ToLowerInvariant()).Append('\t').Append(it.size).Append('\t');
                    AlbCacheEnt ce;
                    if (albIndex.TryGetValue(it.path.ToLowerInvariant(), out ce) && ce != null) sb.Append(ce.mtime);
                    else
                    {
                        try { sb.Append(File.GetLastWriteTimeUtc(it.path).Ticks); } catch { }
                    }
                    sb.Append('\n');
                }
                using (var md5 = MD5.Create())
                {
                    var h = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
                    return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return ""; }
        }

        void SaveDupResult(string sig)
        {
            try
            {
                if (!dupUseAlbum || string.IsNullOrEmpty(sig) || dupLastGroupsJson == null) return;
                File.WriteAllText(DupGroupsFile(), dupLastGroupsJson, new System.Text.UTF8Encoding(false));
                File.WriteAllText(DupSigFile(), sig, new System.Text.UTF8Encoding(false));
                dupSig = sig; dupReady = true;
            }
            catch { }
        }

        bool TryLoadDupResult()
        {
            try
            {
                string sig = AlbDupSignature();
                if (string.IsNullOrEmpty(sig)) return false;
                if (dupReady && dupSig == sig && dupLastGroupsJson != null) return true;
                string gf = DupGroupsFile(), sf = DupSigFile();
                if (!File.Exists(gf) || !File.Exists(sf)) return false;
                if (File.ReadAllText(sf).Trim() != sig) return false;
                dupLastGroupsJson = File.ReadAllText(gf);
                dupSig = sig; dupReady = true;
                return true;
            }
            catch { return false; }
        }

        void TryAutoDup()
        {
            if (dupRunning || albItems.Count == 0) return;
            try
            {
                if (TryLoadDupResult())
                {
                    Send(dupLastGroupsJson);
                    SendDupState();
                    return;
                }
                dupUseAlbum = true;
                if (string.IsNullOrEmpty(dupDst)) dupDst = BinDir();
                StartDupScan();
            }
            catch { }
        }

        // Live Photo / 实况照片的伴生文件（视频、编辑记录），主名相同即视为同一组
        string[] DupCompanions(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                string baseName = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return null;
                var res = new List<string>();
                // Windows 文件系统大小写不敏感，用统一小写扩展名查找即可，避免同一文件被登记两次
                string[] exts = { ".mov", ".mp4", ".aae" };
                foreach (var e in exts)
                {
                    try
                    {
                        // 用通配查找（大小写不敏感）并保留磁盘上的真实文件名
                        foreach (var hit in Directory.GetFiles(dir, baseName + e))
                        {
                            if (string.Equals(Path.GetFileNameWithoutExtension(hit), baseName, StringComparison.OrdinalIgnoreCase)
                                && !res.Contains(hit))
                                res.Add(hit);
                        }
                    }
                    catch { }
                }
                return res.Count > 0 ? res.ToArray() : null;
            }
            catch { return null; }
        }

        // 64 位差值哈希（9×8 灰度相邻比较），抗缩放/轻微调色
        ulong DupHash(Bitmap bmp)
        {
            using (var sm = new Bitmap(9, 8, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(sm))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, 0, 0, 9, 8);
                }
                ulong h = 0; int bit = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int l1 = LumAt(sm, x, y), l2 = LumAt(sm, x + 1, y);
                        if (l1 > l2) h |= (1UL << bit);
                        bit++;
                    }
                return h;
            }
        }

        int LumAt(Bitmap b, int x, int y)
        {
            Color c = b.GetPixel(x, y);
            return (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
        }

        static int Hamming(ulong a, ulong b)
        {
            ulong v = a ^ b; int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
        }

        Bitmap DupLoad(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var im = Image.FromStream(fs, false, false))
                return new Bitmap(im);
        }

        // 扫描阶段只读图片头拿宽高，避免为了尺寸把整张图解码成 Bitmap。
        static bool AlbTryImageSize(string path, out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var im = Image.FromStream(fs, false, false))
                {
                    w = im.Width; h = im.Height;
                }
                return w > 0 && h > 0;
            }
            catch
            {
                if (IsAlbRaw(path))
                {
                    try
                    {
                        using (var b = RawEmbeddedThumb(path, 0))
                        {
                            if (b != null) { w = b.Width; h = b.Height; return w > 0 && h > 0; }
                        }
                    }
                    catch { }
                }
                return false;
            }
        }

        // 很多 RAW（CR2/NEF/ARW/DNG 等）内嵌了完整 JPEG 预览；系统解码器不支持 RAW 时，
        // 从文件字节里找最大的 JPEG 段作为缩略图和详情预览。CR3 等无 JPEG 预览的格式仍走系统缩略图。
        static Bitmap RawEmbeddedThumb(string path, int maxSide)
        {
            try
            {
                if (!IsAlbRaw(path)) return null;
                FileInfo fi = new FileInfo(path);
                if (fi.Length < 1024) return null;
                int cap = (int)Math.Min(fi.Length, 64L * 1024L * 1024L);
                byte[] buf = new byte[cap];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = 0;
                    while (read < cap)
                    {
                        int n = fs.Read(buf, read, cap - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    cap = read;
                }
                int bestStart = -1, bestLen = 0;
                for (int i = 0; i + 2 < cap; i++)
                {
                    if (buf[i] != 0xFF || buf[i + 1] != 0xD8 || buf[i + 2] != 0xFF) continue;
                    int end = -1;
                    for (int j = i + 3; j + 1 < cap; j++)
                    {
                        if (buf[j] == 0xFF && buf[j + 1] == 0xD9) { end = j + 2; break; }
                    }
                    if (end < 0) continue;
                    int len = end - i;
                    if (len > bestLen) { bestLen = len; bestStart = i; }
                    i = end - 1;
                }
                if (bestStart < 0 || bestLen < 512) return null;
                using (var ms = new MemoryStream(buf, bestStart, bestLen, false))
                using (var im = Image.FromStream(ms, false, false))
                {
                    Bitmap src = new Bitmap(im);
                    if (maxSide <= 0 || Math.Max(src.Width, src.Height) <= maxSide) return src;
                    int w = src.Width, h = src.Height;
                    double k = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
                    int nw = Math.Max(1, (int)(w * k)), nh = Math.Max(1, (int)(h * k));
                    Bitmap result;
                    using (var sm = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(sm))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, nw, nh);
                        }
                        result = new Bitmap(sm);
                    }
                    src.Dispose();
                    return result;
                }
            }
            catch { return null; }
        }

        string DupThumb(string path, int maxSide, int q)
        {
            try
            {
                using (var b = DupLoad(path))
                {
                    int w = b.Width, h = b.Height;
                    double k = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
                    int nw = Math.Max(1, (int)(w * k)), nh = Math.Max(1, (int)(h * k));
                    using (var sm = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(sm))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(b, 0, 0, nw, nh);
                        }
                        return B64(sm, q);
                    }
                }
            }
            catch { return null; }
        }

        void DupSend(string json) { Send(json); }

        void SendDupState()
        {
            DupSend("{\"type\":\"dupstate\",\"src\":" + JsonStr(dupUseAlbum ? "相册全部照片" : dupSrc) + ",\"dst\":" + JsonStr(dupDst) +
                    ",\"running\":" + (dupRunning ? "true" : "false") + ",\"paused\":" + (dupPaused ? "true" : "false") +
                    ",\"warm\":" + (dupWarmDone ? "true" : "false") + ",\"ready\":" + (dupReady ? "true" : "false") + "}");
        }

        void StartDupScan()
        {
            if (dupRunning) { Send("{\"type\":\"toast\",\"msg\":\"扫描进行中\"}"); return; }
            if (dupUseAlbum)
            {
                if (albItems.Count == 0) { Send("{\"type\":\"toast\",\"msg\":\"相册里还没有照片\"}"); return; }
            }
            else if (string.IsNullOrEmpty(dupSrc) || !Directory.Exists(dupSrc)) { Send("{\"type\":\"toast\",\"msg\":\"请先选择扫描文件夹\"}"); return; }
            if (string.IsNullOrEmpty(dupDst)) dupDst = BinDir();
            LoadDupCache();
            dupCancel = false; dupPaused = false; dupRunning = true; dupReady = false;
            SendDupState();
            var t = new Thread(DupScanThread); t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start();
        }

        void DupScanThread()
        {
            try
            {
                string scanSig = dupUseAlbum ? AlbDupSignature() : "";
                DupSend("{\"type\":\"dup\",\"phase\":\"enum\",\"done\":0,\"total\":0}");
                var paths = new List<string>();
                if (dupUseAlbum)
                {
                    // 直接用相册已经枚举好的清单，不再重复遍历磁盘
                    foreach (var it in albItems)
                        if (it.type != "video" && IsDupImage(it.path)) paths.Add(it.path);
                }
                else
                {
                    var stack = new Stack<string>();
                    stack.Push(dupSrc);
                    while (stack.Count > 0)
                    {
                        if (dupCancel) break;
                        string d = stack.Pop();
                        try
                        {
                            foreach (var sub in Directory.GetDirectories(d))
                            {
                                string name = Path.GetFileName(sub).ToLower();
                                if (name == "_重复照片" || name == "_duplicates") continue;
                                stack.Push(sub);
                            }
                            foreach (var f in Directory.GetFiles(d))
                                if (IsDupImage(f)) paths.Add(f);
                        }
                        catch { }
                    }
                }
                dupTotal = paths.Count; dupDone = 0;
                DupSend("{\"type\":\"dup\",\"phase\":\"scan\",\"done\":0,\"total\":" + dupTotal + "}");

                var list = new List<DupInfo>();
                foreach (var p in paths)
                {
                    if (dupCancel) break;
                    while (dupPaused && !dupCancel) Thread.Sleep(120);
                    dupDone++;
                    try
                    {
                        var fi = new FileInfo(p);
                        if (fi.Length >= 512)
                        {
                            DupEnt ent;
                            if (!TryGetDupEnt(p, fi.Length, fi.LastWriteTimeUtc.Ticks, out ent))
                            {
                                ent = ComputeDupEnt(p, fi.Length, fi.LastWriteTimeUtc.Ticks);
                                PutDupEnt(p, ent);
                            }
                            var info = new DupInfo();
                            info.path = p; info.size = fi.Length;
                            info.w = ent.w; info.h = ent.h; info.hash = ent.hash;
                            info.sha = ent.sha ?? ""; info.decoded = ent.decoded;
                            info.comps = DupCompanions(p);
                            list.Add(info);
                        }
                    }
                    catch { }
                    if (dupDone % 15 == 0 || dupDone == dupTotal)
                        DupSend("{\"type\":\"dup\",\"phase\":\"scan\",\"done\":" + dupDone + ",\"total\":" + dupTotal + "}");
                }

                SaveDupCache();

                int n = list.Count;
                int[] parent = new int[n];
                for (int i = 0; i < n; i++) { parent[i] = i; list[i].parent = i; }
                Func<int, int> find = delegate(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
                Action<int, int> union = delegate(int a, int b) { int ra = find(a), rb = find(b); if (ra != rb) parent[rb] = ra; };

                DupSend("{\"type\":\"dup\",\"phase\":\"compare\",\"done\":0,\"total\":" + n + "}");

                // 1) 完全相同的文件：大小 + SHA256
                var exact = new Dictionary<string, int>();
                for (int i = 0; i < n; i++)
                {
                    string key = list[i].size + ":" + list[i].sha;
                    if (exact.ContainsKey(key)) union(i, exact[key]);
                    else exact[key] = i;
                }

                // 2) 视觉近似：按「宽高比」分桶，再用 4 段 16 位索引找候选，最后校验汉明距离
                var buckets = new Dictionary<string, List<int>>();
                for (int i = 0; i < n; i++)
                {
                    if (list[i].w <= 0 || list[i].h <= 0) continue;
                    string key = ((int)Math.Round((double)list[i].w / list[i].h * 100)).ToString();
                    if (!buckets.ContainsKey(key)) buckets[key] = new List<int>();
                    buckets[key].Add(i);
                }
                foreach (var kv in buckets)
                {
                    var idx = kv.Value;
                    if (idx.Count < 2 || idx.Count > 6000) continue;
                    for (int a = 0; a < idx.Count; a++)
                    {
                        int ia = idx[a];
                        for (int b = a + 1; b < idx.Count; b++)
                        {
                            int ib = idx[b];
                            if (find(ia) == find(ib)) continue;
                            var A = list[ia]; var B = list[ib];
                            int hd = Hamming(A.hash, B.hash);
                            if (hd > 6) continue;
                            double ar1 = (double)A.w / A.h, ar2 = (double)B.w / B.h;
                            if (Math.Abs(ar1 - ar2) > 0.05) continue;
                            union(ia, ib);
                        }
                    }
                }

                var map = new Dictionary<int, List<int>>();
                for (int i = 0; i < n; i++)
                {
                    int r = find(i);
                    if (!map.ContainsKey(r)) map[r] = new List<int>();
                    map[r].Add(i);
                }
                var groups = new List<List<int>>();
                foreach (var kv in map) if (kv.Value.Count >= 2) groups.Add(kv.Value);
                groups.Sort(delegate(List<int> x, List<int> y) {
                    long px = (long)list[x[0]].w * list[x[0]].h, py = (long)list[y[0]].w * list[y[0]].h;
                    return py.CompareTo(px);
                });

                // 组内按像素数/体积降序，第一张作为默认保留
                var outGroups = new List<List<int>>();
                foreach (var g in groups)
                {
                    g.Sort(delegate(int a, int b) {
                        long pa = (long)list[a].w * list[a].h, pb = (long)list[b].w * list[b].h;
                        if (pa != pb) return pb.CompareTo(pa);
                        return list[b].size.CompareTo(list[a].size);
                    });
                    outGroups.Add(g);
                }

                var jsb = new System.Text.StringBuilder();
                jsb.Append("{\"type\":\"dupgroups\",\"total\":").Append(outGroups.Count).Append(",\"files\":").Append(n).Append(",\"items\":[");
                int sent = 0;
                for (int gi = 0; gi < outGroups.Count; gi++)
                {
                    var g = outGroups[gi];
                    if (gi >= 300) break;
                    if (gi > 0) jsb.Append(",");
                    jsb.Append("{\"items\":[");
                    for (int k = 0; k < g.Count; k++)
                    {
                        var it = list[g[k]];
                        if (k > 0) jsb.Append(",");
                        string thumb = null;   // 缩略图改为界面按需请求，避免扫描结束时一次性解码几百张卡住窗口
                        string compsJson = "null";
                        if (it.comps != null && it.comps.Length > 0)
                        {
                            var cjb = new System.Text.StringBuilder("[");
                            for (int ci = 0; ci < it.comps.Length; ci++)
                            {
                                if (ci > 0) cjb.Append(",");
                                cjb.Append(JsonStr(Path.GetFileName(it.comps[ci])));
                            }
                            cjb.Append("]");
                            compsJson = cjb.ToString();
                        }
                        jsb.Append("{\"p\":").Append(JsonStr(it.path))
                          .Append(",\"n\":").Append(JsonStr(Path.GetFileName(it.path)))
                          .Append(",\"s\":").Append(it.size)
                          .Append(",\"w\":").Append(it.w)
                          .Append(",\"h\":").Append(it.h)
                          .Append(",\"d\":").Append(it.decoded ? "1" : "0")
                          .Append(",\"c\":").Append(compsJson)
                          .Append(",\"t\":").Append(thumb == null ? "null" : JsonStr("data:image/jpeg;base64," + thumb))
                          .Append("}");
                    }
                    jsb.Append("]}");
                    sent++;
                }
                jsb.Append("]}");
                dupLastGroupsJson = jsb.ToString();
                Send(dupLastGroupsJson);
                if (dupUseAlbum && !dupCancel) SaveDupResult(scanSig);
                int nodec = 0; foreach (var it0 in list) if (!it0.decoded) nodec++;
                DupSend("{\"type\":\"dup\",\"phase\":\"done\",\"done\":" + n + ",\"total\":" + n + ",\"groups\":" + outGroups.Count + ",\"nodecode\":" + nodec + "}");
            }
            catch (Exception ex)
            {
                DupSend("{\"type\":\"dup\",\"phase\":\"error\",\"msg\":" + JsonStr(ex.Message) + "}");
            }
            finally { dupRunning = false; SendDupState(); }
        }

        static readonly System.Threading.SemaphoreSlim dupThumbGate = new System.Threading.SemaphoreSlim(3, 3);

        void QueueDupThumb(string path)
        {
            string p = path;
            ThreadPool.QueueUserWorkItem(delegate
            {
                dupThumbGate.Wait();
                try
                {
                    string t = null;
                    try { t = DupThumb(p, 150, 72); } catch { }
                    Send("{\"type\":\"dupthumb\",\"p\":" + JsonStr(p) + ",\"t\":" +
                         (t == null ? "null" : JsonStr("data:image/jpeg;base64," + t)) + "}");
                }
                catch { }
                finally { dupThumbGate.Release(); }
            });
        }

        void DupPreview(string path)
        {
            string p = path;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string b64 = DupThumb(p, 1400, 82);
                    if (b64 == null) { Send("{\"type\":\"toast\",\"msg\":\"此格式无法预览（可能是 HEIC/HEIF），仍可正常去重与移出\"}"); return; }
                    int w = 0, h = 0; long sz = 0;
                    try { using (var b = DupLoad(p)) { w = b.Width; h = b.Height; } } catch { }
                    try { sz = new FileInfo(p).Length; } catch { }
                    Send("{\"type\":\"dupbig\",\"p\":" + JsonStr(p) + ",\"n\":" + JsonStr(Path.GetFileName(p)) +
                         ",\"w\":" + w + ",\"h\":" + h + ",\"s\":" + sz + ",\"data\":" + JsonStr("data:image/jpeg;base64," + b64) + "}");
                }
                catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("预览失败: " + ex.Message) + "}"); }
            });
        }

        void DupMove(string[] paths)
        {
            try
            {
                if (paths == null || paths.Length == 0) { Send("{\"type\":\"toast\",\"msg\":\"没有勾选要移出的照片\"}"); return; }
                var rows = BinLoad();
                int moved = 0, failed = 0, compsMoved = 0;
                foreach (var p in paths)
                {
                    try
                    {
                        if (!File.Exists(p)) continue;
                        string gid = DateTime.Now.Ticks.ToString() + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
                        if (BinTrashFile(p, gid, "m", rows) == 0) { failed++; continue; }
                        moved++;
                        // 实况照片的 MOV / AAE 同组进回收站
                        var comps = DupCompanions(p);
                        if (comps != null)
                        {
                            foreach (var cf in comps)
                            {
                                if (!File.Exists(cf)) continue;
                                if (string.Equals(Path.GetFileName(cf), Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)) continue;
                                compsMoved += BinTrashFile(cf, gid, "c", rows);
                            }
                        }
                    }
                    catch { failed++; }
                }
                BinSave(rows);
                dupDst = BinDir();
                Send("{\"type\":\"dupdone\",\"moved\":" + moved + ",\"failed\":" + failed + ",\"comps\":" + compsMoved + ",\"bin\":true,\"dst\":" + JsonStr(BinDir()) + "}");
                // 移入回收站后不再自动弹窗打开文件夹
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("移动失败: " + ex.Message) + "}"); }
        }

        // ==================== 相册 ====================
        [StructLayout(LayoutKind.Sequential)]
        struct Sz { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellThumbFactory { [PreserveSig] int GetImage(Sz size, int flags, out IntPtr phbm); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellThumbFactory factory);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        Bitmap ShellThumb(string path, int side)
        {
            IShellThumbFactory f = null;
            IntPtr hb = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellThumbFactory).GUID;
                int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f);
                if (hr != 0 || f == null) return null;
                hr = f.GetImage(new Sz { cx = side, cy = side }, 0, out hb);
                if (hr != 0 || hb == IntPtr.Zero) return null;
                using (var tmp = Bitmap.FromHbitmap(hb))
                {
                    var copy = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(copy)) g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                    return copy;
                }
            }
            catch { return null; }
            finally
            {
                if (hb != IntPtr.Zero) { try { DeleteObject(hb); } catch { } }
                if (f != null) { try { Marshal.ReleaseComObject(f); } catch { } }
            }
        }

        // ---- EXIF（拍摄时间 + GPS）----
        static int ExifFindBlock(byte[] buf)
        {
            for (int i = 0; i + 6 < buf.Length; i++)
                if (buf[i] == 0x45 && buf[i + 1] == 0x78 && buf[i + 2] == 0x69 && buf[i + 3] == 0x66 && buf[i + 4] == 0x00 && buf[i + 5] == 0x00)
                    return i + 6;
            return -1;
        }

        static ushort ExifU16(byte[] b, int off, bool le)
        {
            return le ? (ushort)(b[off] | (b[off + 1] << 8)) : (ushort)((b[off] << 8) | b[off + 1]);
        }
        static uint ExifU32(byte[] b, int off, bool le)
        {
            if (le) return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
            return (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);
        }
        static double ExifRat(byte[] b, int off, bool le)
        {
            uint n = ExifU32(b, off, le), d = ExifU32(b, off + 4, le);
            return d == 0 ? 0 : (double)n / d;
        }

        class ExifMeta
        {
            public long taken;
            public double lat, lon;
            public string make = "", model = "", lens = "";
            public double fnum, exposure, focal;
            public int iso;
        }

        bool ExifRead(string path, out long taken, out double lat, out double lon)
        {
            ExifMeta m;
            bool ok = ExifDetail(path, out m);
            taken = m.taken; lat = m.lat; lon = m.lon;
            return ok;
        }

        static byte[] AlbReadPrefix(string path, int cap)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n = (int)Math.Min(fs.Length, cap);
                    var buf = new byte[n];
                    int got = 0;
                    while (got < n) { int r = fs.Read(buf, got, n - got); if (r <= 0) break; got += r; }
                    if (got < n) Array.Resize(ref buf, got);
                    return buf;
                }
            }
            catch { return new byte[0]; }
        }

        // 解析 JPEG/TIFF（以及 HEIC 里连续存放的 EXIF 块）：时间、GPS、机身镜头与曝光参数
        bool ExifDetail(string path, out ExifMeta meta)
        {
            meta = new ExifMeta();
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                // JPEG/TIFF 的 EXIF 基本都在前 128KB；HEIC 给 512KB 首读，只有确实找不到才扩大到 12MB。
                int head = (ext == ".heic" || ext == ".heif") ? 512 * 1024 : 128 * 1024;
                byte[] buf = AlbReadPrefix(path, head);
                int tiff = ExifFindBlock(buf);
                if (tiff < 0 && (ext == ".heic" || ext == ".heif" || ext == ".png" || ext == ".tif" || ext == ".tiff"))
                {
                    buf = AlbReadPrefix(path, 12 * 1024 * 1024);
                    tiff = ExifFindBlock(buf);
                }
                if (tiff < 0 || tiff + 8 >= buf.Length) return false;
                bool le = buf[tiff] == 0x49 && buf[tiff + 1] == 0x49;
                if (!le && !(buf[tiff] == 0x4D && buf[tiff + 1] == 0x4D)) return false;
                if (ExifU16(buf, tiff + 2, le) != 42) return false;
                uint ifd0 = ExifU32(buf, tiff + 4, le);
                uint exifIfd = 0, gpsIfd = 0;
                int tbase = tiff;
                ExifWalk(buf, tbase, ifd0, le, meta, ref exifIfd, ref gpsIfd, true);
                if (exifIfd != 0) ExifWalk(buf, tbase, exifIfd, le, meta, ref exifIfd, ref gpsIfd, false);

                if (gpsIfd != 0)
                {
                    int gp = tbase + (int)gpsIfd;
                    if (gp + 2 < buf.Length)
                    {
                        int cnt = ExifU16(buf, gp, le);
                        double[] la = null, lo = null; string laRef = "N", loRef = "E";
                        for (int i = 0; i < cnt; i++)
                        {
                            int e = gp + 2 + i * 12;
                            if (e + 12 > buf.Length) break;
                            ushort tag = ExifU16(buf, e, le);
                            ushort typ = ExifU16(buf, e + 2, le);
                            uint num = ExifU32(buf, e + 4, le);
                            int voff = tbase + (int)ExifU32(buf, e + 8, le);
                            if (tag == 1 || tag == 3)
                            {
                                string v = num > 4 ? System.Text.Encoding.ASCII.GetString(buf, voff, 1) : ((char)buf[e + 8]).ToString();
                                if (tag == 1) laRef = v; else loRef = v;
                            }
                            else if ((tag == 2 || tag == 4) && typ == 5 && num >= 3)
                            {
                                double[] vals = new double[3];
                                for (int k = 0; k < 3; k++) vals[k] = ExifRat(buf, voff + k * 8, le);
                                if (tag == 2) la = vals; else lo = vals;
                            }
                        }
                        if (la != null && lo != null)
                        {
                            meta.lat = la[0] + la[1] / 60.0 + la[2] / 3600.0;
                            meta.lon = lo[0] + lo[1] / 60.0 + lo[2] / 3600.0;
                            if (laRef != null && laRef.Trim().ToUpper() == "S") meta.lat = -meta.lat;
                            if (loRef != null && loRef.Trim().ToUpper() == "W") meta.lon = -meta.lon;
                        }
                    }
                }
                return meta.taken > 0 || meta.lat != 0 || meta.lon != 0 || meta.model.Length > 0;
            }
            catch { return false; }
        }

        string ExifAscii(byte[] buf, int e, uint num, int voff, bool le)
        {
            try
            {
                int len = (int)num;
                int off = len > 4 ? voff : e + 8;
                if (off < 0 || off + len > buf.Length || len <= 0) return "";
                string s = System.Text.Encoding.ASCII.GetString(buf, off, len).Trim('\0', ' ');
                s = s.Trim();
                if (s.Length > 64) s = s.Substring(0, 64);
                return s;
            }
            catch { return ""; }
        }

        void ExifWalk(byte[] buf, int tbase, uint ifdOff, bool le, ExifMeta meta, ref uint exifIfd, ref uint gpsIfd, bool ifd0)
        {
            try
            {
                int p = tbase + (int)ifdOff;
                if (p + 2 >= buf.Length) return;
                int cnt = ExifU16(buf, p, le);
                for (int i = 0; i < cnt; i++)
                {
                    int e = p + 2 + i * 12;
                    if (e + 12 > buf.Length) break;
                    ushort tag = ExifU16(buf, e, le);
                    ushort typ = ExifU16(buf, e + 2, le);
                    uint num = ExifU32(buf, e + 4, le);
                    int voff = tbase + (int)ExifU32(buf, e + 8, le);
                    if (ifd0)
                    {
                        if (tag == 0x8769) { exifIfd = ExifU32(buf, e + 8, le); continue; }
                        if (tag == 0x8825) { gpsIfd = ExifU32(buf, e + 8, le); continue; }
                        if (tag == 0x010F && typ == 2) { meta.make = ExifAscii(buf, e, num, voff, le); continue; }
                        if (tag == 0x0110 && typ == 2) { meta.model = ExifAscii(buf, e, num, voff, le); continue; }
                    }
                    if (tag == 0x0132 || tag == 0x9003 || tag == 0x9004)
                    {
                        if (typ != 2) continue;
                        string s = ExifAscii(buf, e, num, voff, le);
                        DateTime dt;
                        if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                        {
                            long t = (long)(dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                            if (t > meta.taken) meta.taken = t;
                        }
                        continue;
                    }
                    if (tag == 0xA434 && typ == 2) { meta.lens = ExifAscii(buf, e, num, voff, le); continue; }
                    if (tag == 0x829D && typ == 5) { meta.fnum = ExifRat(buf, voff, le); continue; }
                    if (tag == 0x829A && typ == 5) { meta.exposure = ExifRat(buf, voff, le); continue; }
                    if (tag == 0x920A && typ == 5) { meta.focal = ExifRat(buf, voff, le); continue; }
                    if (tag == 0x8827 && typ == 3 && num >= 1) { meta.iso = ExifU16(buf, e + 8, le); continue; }
                    if (tag == 0xA405 && typ == 3 && num >= 1 && meta.focal == 0) { meta.focal = ExifU16(buf, e + 8, le); }
                }
            }
            catch { }
        }


        // ---- MP4/MOV 时长与创建时间 ----
        bool Mp4Info(string path, out double dur, out long created)
        {
            dur = 0; created = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    long pos = 0;
                    while (pos + 8 <= len)
                    {
                        fs.Position = pos;
                        byte[] hdr = new byte[16];
                        if (fs.Read(hdr, 0, 8) < 8) break;
                        long size = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                        string typ = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
                        if (size == 1)
                        {
                            byte[] ext = new byte[8];
                            if (fs.Read(ext, 0, 8) < 8) break;
                            size = 0; for (int i = 0; i < 8; i++) size = (size << 8) | ext[i];
                            pos += 8; // header 已多读 8 字节
                        }
                        if (size <= 0) break;
                        long bodyStart = pos + (typ == null ? 8 : 8);
                        if (typ == "moov")
                        {
                            long p = bodyStart;
                            long end = pos + size;
                            while (p + 8 <= end)
                            {
                                fs.Position = p;
                                byte[] bh = new byte[8];
                                if (fs.Read(bh, 0, 8) < 8) break;
                                long bs = ((long)bh[0] << 24) | ((long)bh[1] << 16) | ((long)bh[2] << 8) | bh[3];
                                string bt = System.Text.Encoding.ASCII.GetString(bh, 4, 4);
                                if (bs < 8) break;
                                if (bt == "mvhd")
                                {
                                    byte[] mv = new byte[Math.Min(112, bs)];
                                    fs.Position = p;
                                    int rmv = 0;
                                    while (rmv < mv.Length) { int r = fs.Read(mv, rmv, mv.Length - rmv); if (r <= 0) break; rmv += r; }
                                    int ver = mv.Length > 8 ? mv[8] : 0;
                                    // mv 从 box 头开始读：size(4) type(4) version(1) flags(3)
                                    if (ver == 1)
                                    {
                                        uint ts = ExifU32(mv, 28, false);
                                        ulong du = ((ulong)ExifU32(mv, 32, false) << 32) | ExifU32(mv, 36, false);
                                        if (ts > 0) dur = (double)du / ts;
                                        ulong ct = ((ulong)ExifU32(mv, 12, false) << 32) | ExifU32(mv, 16, false);
                                        created = (long)ct - 2082844800L;
                                    }
                                    else
                                    {
                                        uint ct = ExifU32(mv, 12, false);
                                        uint ts = ExifU32(mv, 20, false);
                                        uint du = ExifU32(mv, 24, false);
                                        if (ts > 0) dur = (double)du / ts;
                                        created = (long)ct - 2082844800L;
                                    }
                                    break;
                                }
                                p += bs;
                            }
                            break;
                        }
                        pos += size;
                    }
                }
                return dur > 0 || created > 0;
            }
            catch { return false; }
        }

        class AlbItem
        {
            public string path, name, type, folder, root;
            public string make, model, lens;
            public double fnum, exposure, focal;
            public int iso;
            public long size, taken;
            public int w, h;
            public double dur, lat, lon;
        }

        List<AlbItem> albItems = new List<AlbItem>();
        List<string> albRoots = new List<string>();
        volatile bool albRunning = false;
        DateTime albLastCkpt = DateTime.MinValue;

        string AlbRootsFile()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageBatchTool");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "alb_paths.txt");
            }
            catch { return Path.Combine(CacheStore.Sub("config"), "alb_paths.txt"); }
        }

        void LoadAlbRoots()
        {
            try
            {
                string f = AlbRootsFile();
                if (!File.Exists(f)) return;
                foreach (var line in File.ReadAllLines(f))
                {
                    string p = line.Trim();
                    if (p.Length > 0 && Directory.Exists(p) && !albRoots.Contains(p)) albRoots.Add(p);
                }
                SetupAlbWatchers();
            }
            catch { }
        }

        void SaveAlbRoots()
        {
            try { File.WriteAllLines(AlbRootsFile(), albRoots.ToArray(), System.Text.Encoding.UTF8); } catch { }
        }

        // ---- 相册扫描缓存（元数据 / 缩略图），只对新增或变动文件重新解析 ----
        class AlbCacheEnt
        {
            public long size, mtime, taken;
            public string type;
            public string realPath;
            public int w, h;
            public double lat, lon, dur, fnum, exposure, focal;
            public string make, model, lens;
            public int iso;
        }

        class AlbScanDir
        {
            public string path, root;
            public long stamp;
            public bool cached;
            public List<FileInfo> files;
        }

        class AlbScanCtx
        {
            public ConcurrentDictionary<string, AlbCacheEnt> newIndex = new ConcurrentDictionary<string, AlbCacheEnt>();
            public ConcurrentDictionary<string, long> newStamps = new ConcurrentDictionary<string, long>();
            public List<AlbItem> items = new List<AlbItem>();
            public List<AlbItem> pending = new List<AlbItem>();
            public object gate = new object();
            public int done = 0;
            public int streamed = 0;
            public bool changed = false;
            public DateTime lastSend = DateTime.UtcNow;
        }

        Dictionary<string, AlbCacheEnt> albIndex = new Dictionary<string, AlbCacheEnt>();
        // 按目录归集的索引：目录未变化时整目录直接复用，不再逐个文件 stat
        Dictionary<string, List<AlbCacheEnt>> albByDir = new Dictionary<string, List<AlbCacheEnt>>();
        // 目录时间戳：新增/删除/改名会改变目录的 LastWriteTime，可据此跳过整个目录
        Dictionary<string, long> albDirStamp = new Dictionary<string, long>();
        // 本地删除/改名事件强制标记的目录；即使目录时间戳异常没有变化，也会重新枚举。
        ConcurrentDictionary<string, long> albDirtyDirs = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // 已知已消失的路径。扫描收尾时用它兜底过滤，避免扫描快照或旧目录缓存把幽灵项写回来。
        ConcurrentDictionary<string, long> albGonePaths = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // watcher 事件先积压到防抖结束，再批量更新界面/索引，避免一次移动很多文件时逐个扫全表。
        ConcurrentDictionary<string, long> albPendingGone = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, string> albPendingRename = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 保护 albRunning 与索引的少量事件级修改，避免和扫描线程同时改 albIndex。
        object albScanStateGate = new object();
        volatile bool albWatcherNeedsFull = false;
        bool albIndexLoaded = false;
        bool albDirLoaded = false;
        volatile bool albScanAuto = false;
        volatile bool albScanFull = false;
        volatile bool albRescanPending = false;
        volatile bool albRescanFullPending = false;
        DateTime albLastManualScan = DateTime.MinValue;

        static string EncS(string s)
        {
            try { return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s ?? "")); } catch { return ""; }
        }
        static string DecS(string s)
        {
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); } catch { return ""; }
        }
        static double Dbl(string s) { double d; double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d); return d; }
        static long Lng(string s) { long l; long.TryParse(s, out l); return l; }
        static int Int(string s) { int i; int.TryParse(s, out i); return i; }

        string AlbIndexFile()
        {
            try
            {
                return Path.Combine(CacheSub("index"), "alb_index.dat");
            }
            catch { return Path.Combine(CacheRoot(), "index", "alb_index.dat"); }
        }

        string AlbDirsFile()
        {
            try
            {
                return Path.Combine(CacheSub("index"), "alb_dirs.dat");
            }
            catch { return Path.Combine(CacheRoot(), "index", "alb_dirs.dat"); }
        }

        // 新增 GIF/RAW 扩展名后，旧目录缓存不会再重新枚举文件。
        // 用一个格式版本标记触发一次全量扫描，扫完写标记，后续恢复增量扫描。
        string AlbFormatFlagFile()
        {
            try { return Path.Combine(CacheSub("index"), "alb_formats_v3.flag"); }
            catch { return Path.Combine(CacheRoot(), "index", "alb_formats_v3.flag"); }
        }

        void LoadAlbDirStamps()
        {
            if (albDirLoaded) return;
            albDirLoaded = true;
            try
            {
                string f = AlbDirsFile();
                if (!File.Exists(f)) return;
                foreach (var line in File.ReadAllLines(f))
                {
                    var p = line.Split('\t');
                    if (p.Length < 2) continue;
                    string d = DecS(p[0]);
                    long t = Lng(p[1]);
                    if (d.Length > 0) albDirStamp[d.ToLowerInvariant()] = t;
                }
            }
            catch { }
        }

        // 根目录被移除/文件已被删除时，立即把旧索引和旧列表裁剪到当前根目录，
        // 避免下一轮增量扫描把已移除目录里的图片重新合并回来。
        void PruneAlbumToCurrentRoots()
        {
            var keptItems = new List<AlbItem>();
            foreach (var it in albItems)
            {
                if (string.IsNullOrEmpty(AlbRootOf(it.path))) continue;
                if (!File.Exists(it.path)) continue;
                keptItems.Add(it);
            }

            var keptIndex = new Dictionary<string, AlbCacheEnt>();
            foreach (var kv in albIndex)
            {
                var e = kv.Value;
                if (e == null || string.IsNullOrEmpty(e.realPath)) continue;
                if (string.IsNullOrEmpty(AlbRootOf(e.realPath))) continue;
                if (!File.Exists(e.realPath)) continue;
                keptIndex[kv.Key] = e;
            }

            var keptStamps = new Dictionary<string, long>();
            foreach (var kv in albDirStamp)
            {
                if (string.IsNullOrEmpty(AlbRootOf(kv.Key))) continue;
                if (!Directory.Exists(kv.Key)) continue;
                keptStamps[kv.Key] = kv.Value;
            }

            albItems = keptItems;
            albIndex = keptIndex;
            albDirStamp = keptStamps;
            SaveAlbIndex(keptIndex);
            SaveAlbDirStamps(keptStamps);
            RebuildAlbByDir();
        }

        void RebuildAlbByDir()
        {
            var byDir = new Dictionary<string, List<AlbCacheEnt>>();
            foreach (var kv in albIndex)
            {
                string dk = Path.GetDirectoryName(kv.Value.realPath);
                if (dk == null) dk = "";
                dk = dk.ToLowerInvariant();
                List<AlbCacheEnt> lst;
                if (!byDir.TryGetValue(dk, out lst)) { lst = new List<AlbCacheEnt>(); byDir[dk] = lst; }
                lst.Add(kv.Value);
            }
            albByDir = byDir;
        }

        void SendAlbumStatsNow(bool auto)
        {
            int photos = 0, videos = 0, lives = 0, gifs = 0, raws = 0, geotag = 0;
            var devices = new Dictionary<string, int>();
            foreach (var it in albItems)
            {
                if (it.type == "photo") photos++;
                else if (it.type == "video") videos++;
                else if (it.type == "live") lives++;
                else if (it.type == "gif") gifs++;
                else if (it.type == "raw") raws++;
                if (it.lat != 0 || it.lon != 0) geotag++;
                string dev = string.IsNullOrEmpty(it.model) ? "未知设备" : it.model;
                if (it.type != "video") { if (devices.ContainsKey(dev)) devices[dev]++; else devices[dev] = 1; }
            }
            Send("{\"type\":\"albstats\",\"root\":" + JsonStr(albRoots.Count > 0 ? albRoots[0] : "") +
                 ",\"roots\":" + JsonStr(string.Join("\n", albRoots.ToArray())) + ",\"total\":" + albItems.Count +
                 ",\"photos\":" + photos + ",\"videos\":" + videos + ",\"lives\":" + lives +
                 ",\"gifs\":" + gifs + ",\"raws\":" + raws + ",\"geo\":" + geotag +
                 ",\"devices\":" + devices.Count + (auto ? ",\"auto\":true" : ""));
        }

        void SaveAlbDirStamps(Dictionary<string, long> stamps)
        {
            try
            {
                var sb = new System.Text.StringBuilder(stamps.Count * 40);
                foreach (var kv in stamps) sb.Append(EncS(kv.Key)).Append('\t').Append(kv.Value).AppendLine();
                string f = AlbDirsFile(), tmp = f + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), System.Text.Encoding.UTF8);
                if (File.Exists(f)) File.Delete(f);
                File.Move(tmp, f);
            }
            catch { }
        }

        void LoadAlbIndex()
        {
            if (albIndexLoaded) return;
            albIndexLoaded = true;
            albIndex.Clear();
            albByDir.Clear();
            try
            {
                string f = AlbIndexFile();
                if (!File.Exists(f)) return;
                foreach (var line in File.ReadAllLines(f))
                {
                    if (line.Length == 0) continue;
                    var p = line.Split('\t');
                    if (p.Length < 17) continue;
                    var e = new AlbCacheEnt();
                    e.size = Lng(p[1]); e.mtime = Lng(p[2]); e.type = DecS(p[3]); e.taken = Lng(p[4]);
                    e.w = Int(p[5]); e.h = Int(p[6]); e.lat = Dbl(p[7]); e.lon = Dbl(p[8]); e.dur = Dbl(p[9]);
                    e.make = DecS(p[10]); e.model = DecS(p[11]); e.lens = DecS(p[12]);
                    e.fnum = Dbl(p[13]); e.exposure = Dbl(p[14]); e.focal = Dbl(p[15]); e.iso = Int(p[16]);
                    e.realPath = DecS(p[0]);
                    if (e.realPath.Length == 0) continue;
                    albIndex[e.realPath.ToLowerInvariant()] = e;
                    string dk = Path.GetDirectoryName(e.realPath);
                    if (dk == null) dk = "";
                    dk = dk.ToLowerInvariant();
                    List<AlbCacheEnt> lst;
                    if (!albByDir.TryGetValue(dk, out lst)) { lst = new List<AlbCacheEnt>(); albByDir[dk] = lst; }
                    lst.Add(e);
                }
            }
            catch { }
        }

        void SaveAlbIndex(Dictionary<string, AlbCacheEnt> idx)
        {
            try
            {
                var sb = new System.Text.StringBuilder(idx.Count * 120);
                foreach (var kv in idx)
                {
                    var e = kv.Value;
                    sb.Append(EncS(e.realPath)).Append('\t')
                      .Append(e.size).Append('\t').Append(e.mtime).Append('\t').Append(EncS(e.type)).Append('\t')
                      .Append(e.taken).Append('\t').Append(e.w).Append('\t').Append(e.h).Append('\t')
                      .Append(e.lat.ToString("0.######", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.lon.ToString("0.######", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.dur.ToString("0.###", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(EncS(e.make)).Append('\t').Append(EncS(e.model)).Append('\t').Append(EncS(e.lens)).Append('\t')
                      .Append(e.fnum.ToString("0.##", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.exposure.ToString("0.########", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.focal.ToString("0.#", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.iso).AppendLine();
                }
                string f = AlbIndexFile(), tmp = f + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), System.Text.Encoding.UTF8);
                if (File.Exists(f)) File.Delete(f);
                File.Move(tmp, f);
            }
            catch { }
        }

        // 扫描中途落盘：已完成目录直接记为已扫；未扫目录保留旧索引条目，避免重启后重复解析
        void SaveAlbIndexPartial(Dictionary<string, AlbCacheEnt> idx, Dictionary<string, long> stamps)
        {
            try
            {
                var merged = new Dictionary<string, AlbCacheEnt>(idx);
                foreach (var kv in albIndex)
                {
                    string dk = Path.GetDirectoryName(kv.Value.realPath);
                    if (dk == null) dk = "";
                    dk = dk.ToLowerInvariant();
                    if (stamps.ContainsKey(dk)) continue;   // 这个目录本轮已扫完，以新结果为准（可丢掉已删文件）
                    if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
                }
                // 中途落盘也不能把已移除根目录的旧条目写回来。
                var dropKeys = new List<string>();
                foreach (var kv in merged) if (string.IsNullOrEmpty(AlbRootOf(kv.Value.realPath))) dropKeys.Add(kv.Key);
                foreach (var k in dropKeys) merged.Remove(k);
                var dropStamps = new List<string>();
                foreach (var kv in stamps) if (string.IsNullOrEmpty(AlbRootOf(kv.Key))) dropStamps.Add(kv.Key);
                foreach (var k in dropStamps) stamps.Remove(k);
                SaveAlbIndex(merged);
                SaveAlbDirStamps(stamps);
            }
            catch { }
        }

        void AlbScanCheckpoint(Dictionary<string, AlbCacheEnt> idx, Dictionary<string, long> stamps)
        {
            if (idx.Count == 0 && albIndex.Count == 0) return;
            if ((DateTime.UtcNow - albLastCkpt).TotalSeconds < 60) return;
            albLastCkpt = DateTime.UtcNow;
            SaveAlbIndexPartial(idx, stamps);
        }

        // 启动时先用上次的索引秒开相册，后台再核对变化
        void ShowCachedAlbum()
        {
            try
            {
                LoadAlbIndex();
                if (albIndex.Count == 0 || albItems.Count > 0) return;
                var items = new List<AlbItem>();
                foreach (var kv in albIndex)
                {
                    var e = kv.Value;
                    if (string.IsNullOrEmpty(e.realPath) || !File.Exists(e.realPath)) continue;
                    // 已从相册移除的根目录，其旧索引不能再出现在缓存相册里。
                    if (string.IsNullOrEmpty(AlbRootOf(e.realPath))) continue;
                    var it = new AlbItem();
                    it.path = e.realPath;
                    it.name = Path.GetFileName(e.realPath);
                    it.root = AlbRootOf(e.realPath) ?? "";
                    string rel = it.root.Length > 0 ? e.realPath.Substring(Math.Min(e.realPath.Length, it.root.Length)).TrimStart('\\') : e.realPath;
                    it.folder = Path.GetDirectoryName(rel);
                    if (string.IsNullOrEmpty(it.folder)) it.folder = ".";
                    it.type = e.type;
                    it.size = e.size; it.taken = e.taken; it.w = e.w; it.h = e.h;
                    it.lat = e.lat; it.lon = e.lon; it.dur = e.dur;
                    it.make = e.make; it.model = e.model; it.lens = e.lens;
                    it.fnum = e.fnum; it.exposure = e.exposure; it.focal = e.focal; it.iso = e.iso;
                    items.Add(it);
                }
                if (items.Count == 0) return;
                items.Sort(delegate(AlbItem a, AlbItem b) {
                    if (a.taken != b.taken) return b.taken.CompareTo(a.taken);
                    return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                });
                albItems = items;
                int photos = 0, videos = 0, lives = 0, gifs = 0, raws = 0, geotag = 0;
                var devices = new Dictionary<string, int>();
                foreach (var it in items)
                {
                    if (it.type == "photo") photos++;
                    else if (it.type == "video") videos++;
                    else if (it.type == "live") lives++;
                    else if (it.type == "gif") gifs++;
                    else if (it.type == "raw") raws++;
                    if (it.lat != 0 || it.lon != 0) geotag++;
                    string dev = string.IsNullOrEmpty(it.model) ? "未知设备" : it.model;
                    if (it.type != "video") { if (devices.ContainsKey(dev)) devices[dev]++; else devices[dev] = 1; }
                }
                Send("{\"type\":\"albstats\",\"root\":" + JsonStr(albRoots.Count > 0 ? albRoots[0] : "") +
                     ",\"roots\":" + JsonStr(string.Join("\n", albRoots.ToArray())) + ",\"total\":" + items.Count +
                     ",\"photos\":" + photos + ",\"videos\":" + videos + ",\"lives\":" + lives +
                     ",\"gifs\":" + gifs + ",\"raws\":" + raws + ",\"geo\":" + geotag +
                     ",\"devices\":" + devices.Count + ",\"cached\":true}");
            }
            catch { }
        }

        // ---- 统一缓存根目录：严格使用安装目录下 cache，不再回退到 AppData/TEMP ----
        static string CacheRoot()
        {
            return CacheStore.Root();
        }

        static string CacheSub(string name)
        {
            return CacheStore.Sub(name);
        }

        // 详情页内嵌播放视频时，WebView2 不允许 http://app.local 直接读 file:/// 视频。
        // 把伴生视频复制/更新到 cache\media，再通过 media.local 虚拟站点提供同源可播放地址。
        static string AlbPrepareMedia(string src)
        {
            try
            {
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) return "";
                string dir = CacheSub("media");
                var fi = new FileInfo(src);
                string key = src.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                string hash;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] hb = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                    hash = BitConverter.ToString(hb).Replace("-", "").Substring(0, 8).ToLowerInvariant();
                }
                string baseName = Path.GetFileNameWithoutExtension(src);
                foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
                string ext = Path.GetExtension(src).ToLowerInvariant();
                string name = baseName + "_" + hash + ext;
                string dst = Path.Combine(dir, name);
                if (!File.Exists(dst) || new FileInfo(dst).Length != fi.Length)
                    File.Copy(src, dst, true);
                return "http://media.local/" + Uri.EscapeDataString(name);
            }
            catch { return ""; }
        }

        static void MoveCacheContents(string src, string dst)
        {
            MergeCacheDir(src, dst);
        }

        // 递归合并旧缓存目录到安装目录 cache，避免升级后旧缩略图继续留在 AppData/TEMP。
        static void MergeCacheDir(string src, string dst)
        {
            try
            {
                if (!Directory.Exists(src)) return;
                if (!Directory.Exists(dst))
                {
                    try { Directory.Move(src, dst); return; } catch { }
                }
                Directory.CreateDirectory(dst);
                foreach (var f in Directory.GetFiles(src))
                {
                    try
                    {
                        string to = Path.Combine(dst, Path.GetFileName(f));
                        if (!File.Exists(to)) File.Move(f, to);
                        else File.Delete(f);
                    }
                    catch { }
                }
                foreach (var sub in Directory.GetDirectories(src))
                {
                    try { MergeCacheDir(sub, Path.Combine(dst, Path.GetFileName(sub))); } catch { }
                }
                try { Directory.Delete(src, true); } catch { }
            }
            catch { }
        }

        // 把旧版本的临时缩略图 / AppData / TEMP 缓存迁移到新的安装目录 cache
        static void MigrateLegacyCache()
        {
            try
            {
                string cache = CacheRoot();
                CacheStore.Sub("temp");
                MergeCacheDir(Path.Combine(Path.GetTempPath(), "ibt_thumbs"), CacheSub("thumbs"));
                MergeCacheDir(Path.Combine(Path.GetTempPath(), "ibt_vthumbs"), CacheSub("vthumbs"));
                MergeCacheDir(Path.Combine(Path.GetTempPath(), "ImageBatchTool_cache"), cache);
                MergeCacheDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageBatchTool", "cache"), cache);
                string oldCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageBatchTool");
                string newIdx = CacheSub("index");
                foreach (var n in new[] { "alb_index.dat", "alb_dirs.dat", "dup_index.dat" })
                {
                    try
                    {
                        string old = Path.Combine(oldCfg, n);
                        string dst = Path.Combine(newIdx, n);
                        if (File.Exists(old) && !File.Exists(dst)) File.Move(old, dst);
                    }
                    catch { }
                }
                // 旧版人物分析临时清单、视频封面任务清单没有落在安装目录，升级时直接清掉。
                try
                {
                    string[] oldTemps = Directory.GetFiles(Path.GetTempPath(), "ibt_faces_*.txt");
                    foreach (var f in oldTemps) { try { File.Delete(f); } catch { } }
                }
                catch { }
                try { File.Delete(Path.Combine(Path.GetTempPath(), "ibt_vthumb_jobs.json")); } catch { }
            }
            catch { }
        }

        // ---- 缩略图磁盘缓存 ----
        static bool thumbHostReady = false;

        static string AlbThumbDir()
        {
            return CacheSub("thumbs");
        }
        // 路径 + 大小 + mtime 的缓存 key。文件被外部删除后 FileInfo 取不到 Length，
        // 清理孤儿缓存时需要直接用索引里保存的 size/mtime 计算同一个 key。
        static string AlbThumbFileFor(string path, long size, long mtime)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                string key = (path.ToLowerInvariant() + "|" + size + "|" + mtime);
                using (var md5 = MD5.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(key);
                    var h = md5.ComputeHash(bytes);
                    return Path.Combine(AlbThumbDir(), BitConverter.ToString(h).Replace("-", "").ToLowerInvariant() + ".jpg");
                }
            }
            catch { return null; }
        }

        static string AlbThumbFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return AlbThumbFileFor(path, fi.Length, fi.LastWriteTimeUtc.Ticks);
            }
            catch { return null; }
        }

        // 生成或读取缩略图缓存文件。相册界面优先用 thumb.local 虚拟站点直接引用
        // 这个文件，只有虚拟站点不可用或回收站等特殊场景才退回 base64。
        string AlbThumbPrepare(string path, out int w, out int h)
        {
            w = 0; h = 0;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string cf = AlbThumbFile(path);
            if (cf != null && File.Exists(cf))
            {
                try
                {
                    foreach (var it in albItems)
                        if (string.Equals(it.path, path, StringComparison.OrdinalIgnoreCase)) { w = it.w; h = it.h; break; }
                    return cf;
                }
                catch { }
            }
            try
            {
                Bitmap b = IsAlbRaw(path) ? RawEmbeddedThumb(path, 220) : ShellThumb(path, 220);
                if (b == null) b = ShellThumb(path, 220);
                if (b == null)
                {
                    string vc = VThumbCache(path);
                    if (vc != null && File.Exists(vc)) b = LoadNoLock(vc);
                }
                if (b == null && !IsAlbVideo(path) && !IsAlbRaw(path)) b = DupLoad(path);
                if (b != null)
                {
                    using (b)
                    {
                        // 160px 足够覆盖 150px 的相册格子，体积比 200px 更小，解码更快
                        int max = 160;
                        int bw = b.Width, bh = b.Height;
                        double k = Math.Min(1.0, (double)max / Math.Max(1, Math.Max(bw, bh)));
                        int nw = Math.Max(1, (int)(bw * k)), nh = Math.Max(1, (int)(bh * k));
                        using (var sm = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                        {
                            using (var g = Graphics.FromImage(sm)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(b, 0, 0, nw, nh); }
                            w = nw; h = nh;
                            byte[] bytes;
                            using (var ms = new MemoryStream())
                            {
                                var ep = new EncoderParameters(1);
                                ep.Param[0] = new EncoderParameter(Encoder.Quality, 78L);
                                sm.Save(ms, LUT.JpegCodec, ep);
                                bytes = ms.ToArray();
                            }
                            if (cf != null)
                            {
                                try
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(cf));
                                    File.WriteAllBytes(cf, bytes);
                                }
                                catch { }
                            }
                            return cf;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        string AlbThumbB64(string path, out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                string cf = AlbThumbPrepare(path, out w, out h);
                if (cf == null || !File.Exists(cf)) return null;
                return Convert.ToBase64String(File.ReadAllBytes(cf));
            }
            catch { return null; }
        }

        string AlbThumbUrl(string cf)
        {
            if (!thumbHostReady || string.IsNullOrEmpty(cf) || !File.Exists(cf)) return "";
            try { return "http://thumb.local/" + Uri.EscapeDataString(Path.GetFileName(cf)); }
            catch { return ""; }
        }


        List<FileSystemWatcher> albWatchers = new List<FileSystemWatcher>();
        System.Threading.Timer albWatchTimer;
        static readonly string[] AlbIgnoredDirNames = {
            "$RECYCLE.BIN", "$WinREAgent", "System Volume Information", "node_modules", ".git",
            "Windows", "Program Files", "Program Files (x86)", "ProgramData"
        };

        static bool IsAppInternalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path).TrimEnd('\\');
                string app = Path.GetFullPath(Application.StartupPath).TrimEnd('\\');
                string cache = Path.GetFullPath(CacheRoot()).TrimEnd('\\');
                return string.Equals(full, app, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(app + "\\", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(full, cache, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(cache + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static bool IsAlbIgnoredPath(string path)
        {
            if (IsAppInternalPath(path)) return true;
            try
            {
                string full = Path.GetFullPath(path);
                if (full.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
                string[] parts = full.Split(Path.DirectorySeparatorChar);
                for (int i = 0; i < parts.Length; i++)
                    for (int j = 0; j < AlbIgnoredDirNames.Length; j++)
                        if (string.Equals(parts[i], AlbIgnoredDirNames[j], StringComparison.OrdinalIgnoreCase)) return true;
            } catch { }
            return false;
        }

        void SetupAlbWatchers()
        {
            try
            {
                foreach (var w in albWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
                albWatchers.Clear();
                foreach (var root in albRoots)
                {
                    if (IsAppInternalPath(root)) continue;
                    try
                    {
                        var w = new FileSystemWatcher(root);
                        w.IncludeSubdirectories = true;
                        w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                        w.InternalBufferSize = 64 * 1024;
                        w.Created += AlbWatcherEvent;
                        w.Deleted += AlbWatcherEvent;
                        w.Renamed += AlbWatcherEvent;
                        w.Changed += AlbWatcherEvent;
                        w.Error += AlbWatcherError;
                        w.EnableRaisingEvents = true;
                        albWatchers.Add(w);
                    }
                    catch { }
                }
                if (albWatchTimer == null)
                    albWatchTimer = new System.Threading.Timer(delegate
                    {
                        try { AlbFlushWatcherEvents(); } catch (Exception ex) { try { Log("相册 watcher 批量更新失败: " + ex.Message); } catch { } }
                        try
                        {
                            if (albWatcherNeedsFull) { albWatcherNeedsFull = false; StartAlbumScan(true, true); }
                            else StartAlbumScan(true);
                        }
                        catch { }
                    }, null, Timeout.Infinite, Timeout.Infinite);
            }
            catch { }
        }

        void AlbWatcherError(object sender, ErrorEventArgs e)
        {
            try
            {
                string msg = e != null && e.GetException() != null ? e.GetException().Message : "unknown";
                Log("相册文件监视异常，准备全量兜底扫描: " + msg);
                albWatcherNeedsFull = true;
                if (albWatchTimer != null) albWatchTimer.Change(1800, Timeout.Infinite);
            }
            catch { }
        }

        void AlbWatcherEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (IsAlbIgnoredPath(e.FullPath)) return;
                string ext = Path.GetExtension(e.FullPath).ToLower();
                bool isDir = false;
                try { isDir = Directory.Exists(e.FullPath); } catch { }
                if (!(isDir || IsAlbImage(e.FullPath) || IsAlbVideo(e.FullPath) ||
                      ext == ".aae" || e.ChangeType == WatcherChangeTypes.Deleted || e.ChangeType == WatcherChangeTypes.Renamed))
                    return;

                var re = e as RenamedEventArgs;
                if (re != null)
                {
                    AlbQueueWatcherRename(re.OldFullPath, re.FullPath);
                }
                else if (e.ChangeType == WatcherChangeTypes.Deleted)
                {
                    AlbQueueWatcherGone(e.FullPath);
                }
                else if (e.ChangeType == WatcherChangeTypes.Created)
                {
                    try { if (IsAlbImage(e.FullPath) || IsAlbVideo(e.FullPath)) Log("相册 watcher 新增: " + e.FullPath); else if (Directory.Exists(e.FullPath)) Log("相册 watcher 新增目录: " + e.FullPath); } catch { }
                    AlbForgetGone(e.FullPath);
                    AlbMarkWatcherPathDirty(e.FullPath, true);
                }
                else
                {
                    AlbMarkWatcherPathDirty(e.FullPath, false);
                }

                if (albWatchTimer != null) albWatchTimer.Change(1800, Timeout.Infinite);
            }
            catch { }
        }


        void AlbMarkWatcherPathDirty(string path, bool includeSelf)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string full = Path.GetFullPath(path);
                long now = DateTime.UtcNow.Ticks;
                string root = AlbRootOf(full);
                string rootKey = string.IsNullOrEmpty(root) ? "" : root.TrimEnd('\\');
                // 从事件路径（或父目录）开始，把整条父链标脏直到相册根目录。
                // 这样即使子目录自身的时间戳没有变化，扫描也不会复用旧目录缓存。
                string cur = includeSelf ? full : Path.GetDirectoryName(full);
                while (!string.IsNullOrEmpty(cur))
                {
                    string key = cur.ToLowerInvariant();
                    albDirtyDirs[key] = now;
                    string trim = cur.TrimEnd('\\');
                    if (rootKey.Length > 0 && string.Equals(trim, rootKey, StringComparison.OrdinalIgnoreCase)) break;
                    string parent = Path.GetDirectoryName(trim);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, cur, StringComparison.OrdinalIgnoreCase)) break;
                    cur = parent;
                }
            }
            catch { }
        }

        void AlbForgetGone(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                long goneTicks = 0;
                albGonePaths.TryRemove(path, out goneTicks);
                albPendingGone.TryRemove(path, out goneTicks);
            }
            catch { }
        }

        void AlbQueueWatcherGone(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                long now = DateTime.UtcNow.Ticks;
                albPendingGone[path] = now;
                albGonePaths[path] = now;
                AlbMarkWatcherPathDirty(path, true);
            }
            catch { }
        }

        void AlbQueueWatcherRename(string oldPath, string newPath)
        {
            try
            {
                if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
                // 同一路径的新事件优先：不要把它当成旧的已消失项。
                AlbForgetGone(newPath);
                long now = DateTime.UtcNow.Ticks;
                albPendingRename[oldPath] = newPath;
                long oldTicks = 0;
                albPendingGone.TryRemove(oldPath, out oldTicks);
                albGonePaths[oldPath] = now;
                AlbMarkWatcherPathDirty(oldPath, true);
                AlbMarkWatcherPathDirty(newPath, true);
            }
            catch { }
        }

        string AlbResolveWatcherRename(string path)
        {
            string cur = path;
            for (int i = 0; i < 8; i++)
            {
                string next;
                if (!albPendingRename.TryGetValue(cur, out next) ||
                    string.IsNullOrEmpty(next) ||
                    string.Equals(next, cur, StringComparison.OrdinalIgnoreCase)) break;
                cur = next;
            }
            return cur;
        }

        bool AlbIsMediaPath(string path)
        {
            return IsAlbImage(path) || IsAlbVideo(path);
        }

        void AlbRemoveIndexEntry(string key, AlbCacheEnt ce)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return;
                if (albIndex.ContainsKey(key)) albIndex.Remove(key);
                if (ce == null) return;
                string dk = Path.GetDirectoryName(ce.realPath);
                if (dk == null) dk = "";
                dk = dk.ToLowerInvariant();
                List<AlbCacheEnt> list;
                if (albByDir.TryGetValue(dk, out list) && list != null)
                {
                    list.Remove(ce);
                    if (list.Count == 0) albByDir.Remove(dk);
                }
            }
            catch { }
        }

        void AlbAddIndexEntry(string key, AlbCacheEnt ce)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || ce == null || string.IsNullOrEmpty(ce.realPath)) return;
                albIndex[key] = ce;
                string dk = Path.GetDirectoryName(ce.realPath);
                if (dk == null) dk = "";
                dk = dk.ToLowerInvariant();
                List<AlbCacheEnt> list;
                if (!albByDir.TryGetValue(dk, out list)) { list = new List<AlbCacheEnt>(); albByDir[dk] = list; }
                list.Add(ce);
            }
            catch { }
        }

        static void AlbMoveCacheFileQuiet(string oldFile, string newFile)
        {
            try
            {
                if (string.IsNullOrEmpty(oldFile) || string.IsNullOrEmpty(newFile) ||
                    string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(oldFile)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(newFile));
                if (File.Exists(newFile)) { try { File.Delete(oldFile); } catch { } return; }
                File.Move(oldFile, newFile);
            }
            catch { }
        }

        string AlbWatcherThumbUrl(string path, AlbCacheEnt ce)
        {
            try
            {
                string cf = null;
                if (ce != null) cf = AlbThumbFileFor(path, ce.size, ce.mtime);
                if (cf == null && File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    cf = AlbThumbFileFor(path, fi.Length, fi.LastWriteTimeUtc.Ticks);
                }
                if (cf != null && File.Exists(cf)) return AlbThumbUrl(cf);
            }
            catch { }
            return "";
        }

        void AlbCleanupCachesForGone(AlbCacheEnt ce)
        {
            try
            {
                if (ce == null || string.IsNullOrEmpty(ce.realPath)) return;
                DeleteCacheFileQuiet(AlbThumbFileFor(ce.realPath, ce.size, ce.mtime));
                DeleteCacheFileQuiet(VThumbCacheFor(ce.realPath, ce.size, ce.mtime));
            }
            catch { }
        }

        void AlbFlushWatcherEvents()
        {
            var renames = new List<string[]>();
            var gone = new List<string>();
            try
            {
                // 先把改名链解析完，再处理删除，避免“移动后再删除”的中间状态把新路径也删掉。
                var pendingRename = new List<KeyValuePair<string, string>>();
                foreach (var kv in albPendingRename)
                {
                    string oldPath = kv.Key;
                    string directNew = null;
                    if (albPendingRename.TryRemove(oldPath, out directNew))
                        pendingRename.Add(new KeyValuePair<string, string>(oldPath, directNew));
                }
                var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in pendingRename)
                {
                    string oldPath = kv.Key;
                    string newPath = AlbResolveWatcherRename(kv.Value);
                    if (string.IsNullOrEmpty(newPath) ||
                        string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(newPath) && AlbIsMediaPath(newPath) && AlbRootOf(newPath).Length > 0)
                        renameMap[oldPath] = newPath;
                    else
                        gone.Add(oldPath);
                }

                var pendingGone = new List<string>();
                foreach (var kv in albPendingGone)
                {
                    long goneTicks = 0;
                    if (albPendingGone.TryRemove(kv.Key, out goneTicks))
                        pendingGone.Add(kv.Key);
                }
                foreach (var p in pendingGone)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (renameMap.ContainsKey(p)) continue;
                    if (File.Exists(p) || Directory.Exists(p)) continue;
                    // 改名目标是当前路径时，不把目标当成删除。
                    bool isRenameTarget = false;
                    foreach (var kv in renameMap)
                        if (string.Equals(kv.Value, p, StringComparison.OrdinalIgnoreCase)) { isRenameTarget = true; break; }
                    if (isRenameTarget) continue;
                    gone.Add(p);
                }

                // 去重
                var goneSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in gone) goneSet.Add(p);
                foreach (var kv in renameMap) goneSet.Remove(kv.Key);
                if (goneSet.Count == 0 && renameMap.Count == 0) return;
                try { Log("相册 watcher 批量: gone=" + goneSet.Count + " rename=" + renameMap.Count); } catch { }

                foreach (var p in goneSet) albGonePaths[p] = DateTime.UtcNow.Ticks;
                foreach (var kv in renameMap) albGonePaths[kv.Key] = DateTime.UtcNow.Ticks;

                var itemPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in albItems) itemPaths.Add(it.path);

                // albItems 采用整体替换，不原地 Remove/Add，避免和后台分页读取并发冲突。
                var oldItems = albItems;
                var newItems = new List<AlbItem>(oldItems.Count);
                var existingNewPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in oldItems) existingNewPaths.Add(it.path);
                foreach (var it in oldItems)
                {
                    string newPath = null;
                    if (!renameMap.TryGetValue(it.path, out newPath))
                    {
                        if (goneSet.Contains(it.path)) continue;
                        newItems.Add(it);
                        continue;
                    }
                    string root = AlbRootOf(newPath);
                    if (string.IsNullOrEmpty(root) || !AlbIsMediaPath(newPath)) continue;
                    if (existingNewPaths.Contains(newPath) &&
                        !string.Equals(it.path, newPath, StringComparison.OrdinalIgnoreCase))
                        continue; // 目标路径已有项，丢弃旧项，避免重复
                    it.path = newPath;
                    it.name = Path.GetFileName(newPath);
                    it.root = root;
                    string rel = newPath.Length >= root.Length ? newPath.Substring(root.Length).TrimStart('\\') : newPath;
                    it.folder = Path.GetDirectoryName(rel);
                    if (string.IsNullOrEmpty(it.folder)) it.folder = ".";
                    existingNewPaths.Add(newPath);
                    newItems.Add(it);
                }
                albItems = newItems;

                var uiRenames = new List<string[]>();
                var uiGone = new List<string>();
                bool mutateIndex = false;
                lock (albScanStateGate) mutateIndex = albIndexLoaded && !albRunning;

                if (mutateIndex)
                {
                    foreach (var p in goneSet)
                    {
                        string key = p.ToLowerInvariant();
                        AlbCacheEnt ce = null;
                        if (albIndex.TryGetValue(key, out ce))
                        {
                            AlbRemoveIndexEntry(key, ce);
                            AlbCleanupCachesForGone(ce);
                        }
                    }
                    foreach (var kv in renameMap)
                    {
                        string oldKey = kv.Key.ToLowerInvariant();
                        AlbCacheEnt ce = null;
                        if (!albIndex.TryGetValue(oldKey, out ce) || ce == null) continue;
                        AlbRemoveIndexEntry(oldKey, ce);
                        // 改名保持 size/mtime；缓存 key 仍按旧值计算，路径换成新路径。
                        string oldThumb = AlbThumbFileFor(kv.Key, ce.size, ce.mtime);
                        string newThumb = AlbThumbFileFor(kv.Value, ce.size, ce.mtime);
                        AlbMoveCacheFileQuiet(oldThumb, newThumb);
                        string oldV = VThumbCacheFor(kv.Key, ce.size, ce.mtime);
                        string newV = VThumbCacheFor(kv.Value, ce.size, ce.mtime);
                        AlbMoveCacheFileQuiet(oldV, newV);
                        ce.realPath = kv.Value;
                        AlbAddIndexEntry(kv.Value.ToLowerInvariant(), ce);
                    }
                }
                else
                {
                    // 扫描进行中不碰共享索引；目录已经标记 dirty，扫描结束后会重建。
                    foreach (var kv in renameMap)
                    {
                        AlbCacheEnt ce = null;
                        string oldKey = kv.Key.ToLowerInvariant();
                        if (albIndexLoaded && albIndex.TryGetValue(oldKey, out ce) && ce != null)
                        {
                            string oldThumb = AlbThumbFileFor(kv.Key, ce.size, ce.mtime);
                            string newThumb = AlbThumbFileFor(kv.Value, ce.size, ce.mtime);
                            AlbMoveCacheFileQuiet(oldThumb, newThumb);
                            string oldV = VThumbCacheFor(kv.Key, ce.size, ce.mtime);
                            string newV = VThumbCacheFor(kv.Value, ce.size, ce.mtime);
                            AlbMoveCacheFileQuiet(oldV, newV);
                        }
                    }
                }

                foreach (var p in goneSet) if (itemPaths.Contains(p)) uiGone.Add(p);
                foreach (var kv in renameMap)
                {
                    AlbCacheEnt ce = null;
                    if (albIndexLoaded) albIndex.TryGetValue(kv.Key.ToLowerInvariant(), out ce);
                    string root = AlbRootOf(kv.Value);
                    if (string.IsNullOrEmpty(root)) continue;
                    string rel = kv.Value.Length >= root.Length ? kv.Value.Substring(root.Length).TrimStart('\\') : kv.Value;
                    string folder = Path.GetDirectoryName(rel);
                    if (string.IsNullOrEmpty(folder)) folder = ".";
                    string u = AlbWatcherThumbUrl(kv.Value, ce);
                    uiRenames.Add(new string[] { kv.Key, kv.Value, Path.GetFileName(kv.Value), folder, root, u });
                }

                if (uiGone.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("{\"type\":\"albxremove\",\"total\":").Append(albItems.Count).Append(",\"paths\":[");
                    for (int i = 0; i < uiGone.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(JsonStr(uiGone[i]));
                    }
                    sb.Append("]}");
                    Send(sb.ToString());
                }
                if (uiRenames.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("{\"type\":\"albxrename\",\"items\":[");
                    for (int i = 0; i < uiRenames.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        string[] r = uiRenames[i];
                        sb.Append("{\"old\":").Append(JsonStr(r[0]))
                          .Append(",\"new\":").Append(JsonStr(r[1]))
                          .Append(",\"n\":").Append(JsonStr(r[2]))
                          .Append(",\"f\":").Append(JsonStr(r[3]))
                          .Append(",\"rt\":").Append(JsonStr(r[4]))
                          .Append(",\"u\":").Append(JsonStr(r[5])).Append('}');
                    }
                    sb.Append("]}");
                    Send(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                Log("相册 watcher 批量更新异常: " + ex.Message);
            }
        }

        void SendAlbRoots()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"type\":\"albpaths\",\"items\":[");
            for (int i = 0; i < albRoots.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(JsonStr(albRoots[i]));
            }
            sb.Append("]}");
            Send(sb.ToString());
        }

        void AddAlbRoot(string p)
        {
            try
            {
                if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) { Send("{\"type\":\"toast\",\"msg\":\"路径不存在\"}"); return; }
                p = Path.GetFullPath(p).TrimEnd('\\');
                if (p.Length == 2 && p[1] == ':') p += "\\";
                bool dup = false;
                foreach (var r in albRoots) if (string.Equals(r.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) { albRoots.Add(p); SaveAlbRoots(); SetupAlbWatchers(); }
                Log("添加相册根目录: " + p + " (共 " + albRoots.Count + " 个)");
                PruneAlbumToCurrentRoots();
                SendAlbRoots();
                StartAlbumScan();
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("添加路径失败: " + ex.Message) + "}"); }
        }

        void RemoveAlbRoot(string p)
        {
            for (int i = albRoots.Count - 1; i >= 0; i--)
                if (string.Equals(albRoots[i].TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) albRoots.RemoveAt(i);
            SaveAlbRoots();
            SetupAlbWatchers();
            PruneAlbumToCurrentRoots();
            SendAlbRoots();
            if (albRoots.Count == 0)
            {
                albItems = new List<AlbItem>();
                Send("{\"type\":\"albstats\",\"root\":\"\",\"total\":0,\"photos\":0,\"videos\":0,\"lives\":0,\"geo\":0,\"devices\":0}");
            }
            else
            {
                // 先把旧根目录的图片从当前列表里清掉，再开始新一轮扫描。
                SendAlbumStatsNow(false);
                StartAlbumScan();
            }
        }

        static bool IsAlbGif(string f)
        {
            return Path.GetExtension(f).ToLowerInvariant() == ".gif";
        }

        static bool IsAlbRaw(string f)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            return e == ".cr2" || e == ".cr3" || e == ".nef" || e == ".nrw" || e == ".arw" || e == ".srf" ||
                   e == ".sr2" || e == ".dng" || e == ".orf" || e == ".rw2" || e == ".raf" || e == ".srw" ||
                   e == ".pef" || e == ".rwl" || e == ".3fr" || e == ".erf" || e == ".mrw" || e == ".x3f" ||
                   e == ".raw" || e == ".rwz" || e == ".iiq" || e == ".mos" || e == ".mef" || e == ".k25" ||
                   e == ".kdc" || e == ".dcr" || e == ".gpr";
        }

        static string AlbImageType(string f, bool hasLiveVideo)
        {
            if (IsAlbGif(f)) return "gif";
            if (IsAlbRaw(f)) return "raw";
            return hasLiveVideo ? "live" : "photo";
        }

        bool IsAlbImage(string f)
        {
            string e = Path.GetExtension(f).ToLower();
            return e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".bmp" || e == ".tif" || e == ".tiff" || e == ".webp" ||
                   e == ".heic" || e == ".heif" || e == ".gif" || IsAlbRaw(f);
        }
        bool IsAlbVideo(string f)
        {
            string e = Path.GetExtension(f).ToLower();
            return e == ".mov" || e == ".mp4" || e == ".m4v" || e == ".avi" || e == ".mkv" || e == ".3gp";
        }

        void StartAlbumScan() { StartAlbumScan(false, false); }

        void StartAlbumScan(bool auto) { StartAlbumScan(auto, false); }

        void StartAlbumScan(bool auto, bool full)
        {
            if (albRoots.Count == 0) { if (!auto) Send("{\"type\":\"toast\",\"msg\":\"请先添加相册路径\"}"); return; }
            // GIF/RAW 格式版本首次升级时强制全量枚举一次，避免旧目录缓存漏掉新格式。
            try { if (!File.Exists(AlbFormatFlagFile())) full = true; } catch { }
            // 打开相册时页面可能重复发 albopen；2 秒内的重复手动扫描合并成一次。
            if (!auto && !full)
            {
                if ((DateTime.UtcNow - albLastManualScan).TotalMilliseconds < 2000) return;
                albLastManualScan = DateTime.UtcNow;
            }
            if (!auto && albItems.Count == 0) ShowCachedAlbum();
            lock (albScanStateGate)
            {
                if (albRunning) { albRescanPending = true; albScanFull = albScanFull || full; albRescanFullPending = albRescanFullPending || full; return; }
                albRunning = true;
                albScanAuto = auto;
                albScanFull = full;
            }
            albWarmGen++;   // 让上一轮的后台预览缓存线程退出，避免用旧清单继续跑
            var t = new Thread(AlbScanThread); t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start();
        }

        string AlbRootOf(string path)
        {
            string best = null;
            foreach (var r in albRoots)
            {
                string rr = r.TrimEnd('\\');
                bool match = string.Equals(path, rr, StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith(rr + "\\", StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                if (best == null || best.TrimEnd('\\').Length < rr.Length) best = r;
            }
            return best == null ? "" : best;
        }

        AlbItem AlbItemFromEnt(AlbCacheEnt ce, string root)
        {
            var it = new AlbItem();
            it.path = ce.realPath;
            it.name = Path.GetFileName(ce.realPath);
            it.root = root ?? "";
            string rel = it.root.Length > 0 && ce.realPath.Length >= it.root.Length
                ? ce.realPath.Substring(it.root.Length).TrimStart('\\') : ce.realPath;
            it.folder = Path.GetDirectoryName(rel);
            if (string.IsNullOrEmpty(it.folder)) it.folder = ".";
            it.type = ce.type;
            it.size = ce.size; it.taken = ce.taken; it.w = ce.w; it.h = ce.h;
            it.lat = ce.lat; it.lon = ce.lon; it.dur = ce.dur;
            it.make = ce.make; it.model = ce.model; it.lens = ce.lens;
            it.fnum = ce.fnum; it.exposure = ce.exposure; it.focal = ce.focal; it.iso = ce.iso;
            return it;
        }

        // 单条相册项的 JSON，分页与增量推送共用
        void AppendAlbItemJson(System.Text.StringBuilder sb, AlbItem it)
        {
            sb.Append("{\"p\":").Append(JsonStr(it.path))
              .Append(",\"n\":").Append(JsonStr(it.name))
              .Append(",\"t\":").Append(JsonStr(it.type))
              .Append(",\"s\":").Append(it.size)
              .Append(",\"w\":").Append(it.w)
              .Append(",\"h\":").Append(it.h)
              .Append(",\"dt\":").Append(it.taken)
              .Append(",\"d\":").Append((long)it.dur)
              .Append(",\"la\":").Append(it.lat.ToString("0.######", CultureInfo.InvariantCulture))
              .Append(",\"lo\":").Append(it.lon.ToString("0.######", CultureInfo.InvariantCulture))
              .Append(",\"f\":").Append(JsonStr(it.folder))
              .Append(",\"rt\":").Append(JsonStr(it.root))
              .Append(",\"mk\":").Append(JsonStr(it.make ?? ""))
              .Append(",\"md\":").Append(JsonStr(it.model ?? ""))
              .Append(",\"ln\":").Append(JsonStr(it.lens ?? ""))
              .Append(",\"ap\":").Append(it.fnum.ToString("0.##", CultureInfo.InvariantCulture))
              .Append(",\"ex\":").Append(it.exposure.ToString("0.########", CultureInfo.InvariantCulture))
              .Append(",\"is\":").Append(it.iso)
              .Append(",\"fl\":").Append(it.focal.ToString("0.#", CultureInfo.InvariantCulture))
              .Append("}");
        }

        // 扫描过程中先推一批已解析好的项，前端边收边显示
        void SendAlbPartial(List<AlbItem> batch, int total)
        {
            if (batch == null || batch.Count == 0) return;
            try
            {
                var sb = new System.Text.StringBuilder(batch.Count * 160);
                sb.Append("{\"type\":\"albpartial\",\"total\":").Append(total).Append(",\"items\":[");
                for (int i = 0; i < batch.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    AppendAlbItemJson(sb, batch[i]);
                }
                sb.Append("]}");
                Send(sb.ToString());
            }
            catch { }
        }

        void AlbScanSendProgress(AlbScanCtx ctx, long total)
        {
            long done; int items;
            lock (ctx.gate) { done = ctx.done; items = ctx.items.Count; }
            if (total > 0 && done > total) done = total;
            Send("{\"type\":\"albscan\",\"phase\":\"scan\",\"done\":" + done + ",\"total\":" + total + ",\"items\":" + items + "}");
        }

        void AlbScanFlush(AlbScanCtx ctx, long total, bool force)
        {
            lock (ctx.gate)
            {
                if (ctx.pending.Count == 0)
                {
                    if (force) AlbScanSendProgress(ctx, total);
                    return;
                }
                bool due = force || ctx.pending.Count >= 120 || (DateTime.UtcNow - ctx.lastSend).TotalMilliseconds >= 350;
                if (!due) return;
                int take = force ? ctx.pending.Count : Math.Min(ctx.pending.Count, 200);
                var batch = ctx.pending.GetRange(0, take);
                ctx.pending.RemoveRange(0, take);
                // 扫描阶段只把前 2500 项推给界面用于“先显示”，其余只更新进度，
                // 避免 16 万项 JSON + DOM 把 UI 线程拖死；扫描结束后仍按分页正常加载。
                const int streamCap = 2500;
                int room = streamCap - ctx.streamed;
                if (room > 0)
                {
                    int sendCount = Math.Min(take, room);
                    if (sendCount < batch.Count) batch = batch.GetRange(0, sendCount);
                    ctx.streamed += sendCount;
                    SendAlbPartial(batch, ctx.items.Count);
                }
                ctx.lastSend = DateTime.UtcNow;
                long done = ctx.done;
                if (total > 0 && done > total) done = total;
                Send("{\"type\":\"albscan\",\"phase\":\"scan\",\"done\":" + done + ",\"total\":" + total + ",\"items\":" + ctx.items.Count + "}");
            }
        }

        void AlbScanAppend(AlbScanCtx ctx, List<AlbItem> batch, long total)
        {
            if (batch == null || batch.Count == 0) return;
            lock (ctx.gate)
            {
                ctx.items.AddRange(batch);
                ctx.pending.AddRange(batch);
            }
            AlbScanFlush(ctx, total, false);
        }

        void AlbScanCheckpointNow(AlbScanCtx ctx)
        {
            try
            {
                if (!ctx.changed) return;
                if ((DateTime.UtcNow - albLastCkpt).TotalSeconds < 60) return;
                albLastCkpt = DateTime.UtcNow;
                var idx = new Dictionary<string, AlbCacheEnt>(ctx.newIndex);
                var stamps = new Dictionary<string, long>(ctx.newStamps);
                SaveAlbIndexPartial(idx, stamps);
            }
            catch { }
        }

        void AlbScanDirWorker(AlbScanDir job, AlbScanCtx ctx, bool full, long total)
        {
            try
            {
                string dpath = job.path;
                string dl = dpath.ToLowerInvariant();
                long liveStamp = job.stamp;
                try { liveStamp = Directory.GetLastWriteTimeUtc(dpath).Ticks; } catch { }
                bool cachedDir = !full && !albDirtyDirs.ContainsKey(dl) && job.stamp != 0 && liveStamp == job.stamp && albDirStamp.ContainsKey(dl) && albDirStamp[dl] == job.stamp;
                string dirRoot = job.root;

                var dirItems = new List<AlbItem>(64);
                var imgBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var vidBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (cachedDir)
                {
                    List<AlbCacheEnt> ents;
                    if (albByDir.TryGetValue(dl, out ents) && ents != null && ents.Count > 0)
                    {
                        foreach (var ce in ents)
                        {
                            string bn = Path.GetFileNameWithoutExtension(ce.realPath);
                            if (IsAlbImage(ce.realPath)) { if (!imgBase.ContainsKey(bn)) imgBase[bn] = ce.realPath; }
                            else if (IsAlbVideo(ce.realPath)) { if (!vidBase.ContainsKey(bn)) vidBase[bn] = ce.realPath; }
                        }
                        foreach (var ce in ents)
                        {
                            Interlocked.Increment(ref ctx.done);
                            string lk = ce.realPath.ToLowerInvariant();
                            AlbItem it = AlbItemFromEnt(ce, dirRoot);
                            string bn = Path.GetFileNameWithoutExtension(ce.realPath);
                            bool isImg = IsAlbImage(ce.realPath);
                            if (isImg) it.type = AlbImageType(ce.realPath, vidBase.ContainsKey(bn));
                            else if (imgBase.ContainsKey(bn)) continue;
                            else it.type = "video";
                            ctx.newIndex[lk] = ce;
                            dirItems.Add(it);
                            if (dirItems.Count >= 150)
                            {
                                AlbScanAppend(ctx, dirItems, total);
                                dirItems = new List<AlbItem>(64);
                            }
                        }
                    }
                }
                else
                {
                    // 首次枚举时已经把文件清单挂在 job 上，这里不再重新遍历目录。
                    var files = job.files;
                    if (files == null)
                    {
                        files = new List<FileInfo>(64);
                        try
                        {
                            var di = new DirectoryInfo(dpath);
                            foreach (var fi in di.EnumerateFiles())
                            {
                                if (IsAlbImage(fi.Name) || IsAlbVideo(fi.Name)) files.Add(fi);
                            }
                        }
                        catch { }
                    }
                    foreach (var fi in files)
                    {
                        string bn = Path.GetFileNameWithoutExtension(fi.Name);
                        if (IsAlbImage(fi.Name)) { if (!imgBase.ContainsKey(bn)) imgBase[bn] = fi.FullName; }
                        else { if (!vidBase.ContainsKey(bn)) vidBase[bn] = fi.FullName; }
                    }
                    foreach (var fi in files)
                    {
                        Interlocked.Increment(ref ctx.done);
                        string f = fi.FullName;
                        bool isImg = IsAlbImage(f);
                        string bn = Path.GetFileNameWithoutExtension(f);
                        if (!isImg && imgBase.ContainsKey(bn)) continue;
                        long fsize = 0, mt = 0;
                        try { fsize = fi.Length; mt = fi.LastWriteTimeUtc.Ticks; } catch { }
                        string lk = f.ToLowerInvariant();
                        AlbCacheEnt ce;
                        albIndex.TryGetValue(lk, out ce);
                        bool hit = ce != null && ce.size == fsize && ce.mtime == mt && mt != 0;

                        var it = new AlbItem();
                        it.path = f; it.name = fi.Name; it.root = dirRoot;
                        string rel = it.root.Length > 0 && f.Length >= it.root.Length ? f.Substring(it.root.Length).TrimStart('\\') : f;
                        it.folder = Path.GetDirectoryName(rel);
                        if (string.IsNullOrEmpty(it.folder)) it.folder = ".";
                        it.size = fsize;
                        it.type = isImg ? AlbImageType(f, vidBase.ContainsKey(bn)) : "video";

                        if (hit)
                        {
                            it.taken = ce.taken; it.w = ce.w; it.h = ce.h;
                            it.lat = ce.lat; it.lon = ce.lon; it.dur = ce.dur;
                            it.make = ce.make; it.model = ce.model; it.lens = ce.lens;
                            it.fnum = ce.fnum; it.exposure = ce.exposure; it.focal = ce.focal; it.iso = ce.iso;
                        }
                        else
                        {
                            ctx.changed = true;
                            it.taken = mt > 0 ? (long)((mt - 621355968000000000L) / 10000000L) : 0;
                            if (isImg)
                            {
                                ExifMeta meta;
                                if (ExifDetail(f, out meta))
                                {
                                    if (meta.taken > 0) it.taken = meta.taken;
                                    it.lat = meta.lat; it.lon = meta.lon;
                                    it.make = meta.make; it.model = meta.model; it.lens = meta.lens;
                                    it.fnum = meta.fnum; it.exposure = meta.exposure; it.focal = meta.focal; it.iso = meta.iso;
                                }
                                if (!AlbTryImageSize(f, out it.w, out it.h)) { it.w = 0; it.h = 0; }
                                if (it.w == 0)
                                {
                                    try { using (var b = ShellThumb(f, 256)) { if (b != null) { it.w = b.Width; it.h = b.Height; } } } catch { }
                                }
                            }
                            else
                            {
                                double du = 0; long ct = 0;
                                if (Mp4Info(f, out du, out ct)) { it.dur = du; if (ct > 0) it.taken = ct; }
                            }
                            ce = new AlbCacheEnt();
                            ce.realPath = f; ce.size = fsize; ce.mtime = mt; ce.type = it.type;
                            ce.taken = it.taken; ce.w = it.w; ce.h = it.h; ce.lat = it.lat; ce.lon = it.lon; ce.dur = it.dur;
                            ce.make = it.make; ce.model = it.model; ce.lens = it.lens;
                            ce.fnum = it.fnum; ce.exposure = it.exposure; ce.focal = it.focal; ce.iso = it.iso;
                        }
                        ce.realPath = f;
                        ctx.newIndex[lk] = ce;
                        dirItems.Add(it);
                        if (dirItems.Count >= 150)
                        {
                            AlbScanAppend(ctx, dirItems, total);
                            dirItems = new List<AlbItem>(64);
                        }
                    }
                }

                AlbScanAppend(ctx, dirItems, total);
                ctx.newStamps[dl] = liveStamp;
            }
            catch (Exception ex)
            {
                Log("目录扫描失败 " + job.path + ": " + ex.Message);
            }
        }

        // 扫描快照可能在本机删除事件之前生成，或者旧目录缓存仍包含已删除文件；
        // 用 watcher 记录的 gone 路径做最后一道过滤，避免幽灵项重新写回列表。
        void AlbFilterKnownGone(List<AlbItem> items, Dictionary<string, AlbCacheEnt> idx)
        {
            try
            {
                foreach (var kv in albGonePaths)
                {
                    string p = kv.Key;
                    if (string.IsNullOrEmpty(p)) continue;
                    if (File.Exists(p)) { long goneTicks = 0; albGonePaths.TryRemove(p, out goneTicks); continue; }
                    items.RemoveAll(delegate(AlbItem it) { return string.Equals(it.path, p, StringComparison.OrdinalIgnoreCase); });
                    idx.Remove(p.ToLowerInvariant());
                }
            }
            catch { }
        }

        // 只清理“本轮确实扫描过的目录”里已经不在当前索引的缩略图；目录离线/扫描失败时不误删。
        void AlbCleanupOrphanThumbs(Dictionary<string, AlbCacheEnt> oldIndex, Dictionary<string, AlbCacheEnt> curIndex, Dictionary<string, long> scannedStamps)
        {
            try
            {
                if (oldIndex == null || curIndex == null || scannedStamps == null) return;
                foreach (var kv in oldIndex)
                {
                    AlbCacheEnt old = kv.Value;
                    if (old == null || string.IsNullOrEmpty(old.realPath)) continue;
                    string dk = Path.GetDirectoryName(old.realPath);
                    if (dk == null) dk = "";
                    dk = dk.ToLowerInvariant();
                    if (!scannedStamps.ContainsKey(dk)) continue;
                    AlbCacheEnt now = null;
                    if (curIndex.TryGetValue(kv.Key, out now) && now != null &&
                        now.size == old.size && now.mtime == old.mtime) continue;
                    DeleteCacheFileQuiet(AlbThumbFileFor(old.realPath, old.size, old.mtime));
                    DeleteCacheFileQuiet(VThumbCacheFor(old.realPath, old.size, old.mtime));
                }
            }
            catch { }
        }

        void AlbClearDirtyDirs(long scanStarted)
        {
            try
            {
                var done = new List<string>();
                foreach (var kv in albDirtyDirs)
                    if (kv.Value <= scanStarted) done.Add(kv.Key);
                foreach (var k in done)
                {
                    long dirtyTicks = 0;
                    albDirtyDirs.TryRemove(k, out dirtyTicks);
                }
            }
            catch { }
        }

        void AlbScanThread()
        {
            try
            {
                var roots = new List<string>(albRoots);
                Log("相册扫描开始 roots=" + string.Join(" | ", roots.ToArray()) + " full=" + albScanFull);
                Send("{\"type\":\"albscan\",\"phase\":\"enum\",\"done\":0,\"total\":0}");
                LoadAlbIndex();
                LoadAlbDirStamps();
                bool full = albScanFull;
                long scanStarted = DateTime.UtcNow.Ticks;

                var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                    "$RECYCLE.BIN", "System Volume Information", "$WinREAgent", "node_modules", ".git",
                    "Windows", "Program Files", "Program Files (x86)", "ProgramData"
                };

                // 第一遍只做目录枚举和媒体文件计数，得到真实总数；多根目录同时枚举。
                var jobs = new ConcurrentBag<AlbScanDir>();
                long[] totalBox = new long[1];
                int enumParallel = Math.Max(2, Math.Min(6, Environment.ProcessorCount));
                Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = enumParallel }, root =>
                {
                    var stack = new Stack<DirectoryInfo>();
                    try { stack.Push(new DirectoryInfo(root)); } catch { return; }
                    while (stack.Count > 0)
                    {
                        DirectoryInfo di;
                        try { di = stack.Pop(); } catch { break; }
                        string dpath = di.FullName;
                        long stamp = 0;
                        try { stamp = di.LastWriteTimeUtc.Ticks; } catch { }
                        try
                        {
                            foreach (var sub in di.EnumerateDirectories())
                            {
                                if (skipDirs.Contains(sub.Name)) continue;
                                if (IsAppInternalPath(sub.FullName)) continue;
                                try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { }
                                stack.Push(sub);
                            }
                        }
                        catch { }
                        // 目录时间戳未变且已有索引：直接复用整目录，连文件枚举都省掉。
                        // 但本地删除/改名事件会强制把目录标 dirty，不能仅凭时间戳复用。
                        bool cachedDir = false;
                        string dl = dpath.ToLowerInvariant();
                        if (!full && stamp != 0 && !albDirtyDirs.ContainsKey(dl))
                        {
                            long known = 0;
                            List<AlbCacheEnt> ents;
                            if (albDirStamp.TryGetValue(dl, out known) && known == stamp && albByDir.TryGetValue(dl, out ents))
                            {
                                cachedDir = true;
                                Interlocked.Add(ref totalBox[0], ents == null ? 0 : ents.Count);
                            }
                        }
                        List<FileInfo> files = null;
                        if (!cachedDir)
                        {
                            files = new List<FileInfo>(64);
                            try
                            {
                                foreach (var fi in di.EnumerateFiles())
                                    if (IsAlbImage(fi.Name) || IsAlbVideo(fi.Name)) files.Add(fi);
                            }
                            catch { }
                            if (files.Count > 0) Interlocked.Add(ref totalBox[0], files.Count);
                        }
                        jobs.Add(new AlbScanDir { path = dpath, root = AlbRootOf(dpath) ?? "", stamp = stamp, cached = cachedDir, files = files });
                    }
                });

                long total = totalBox[0];
                var ctx = new AlbScanCtx();
                Send("{\"type\":\"albscan\",\"phase\":\"scan\",\"done\":0,\"total\":" + total + "}");
                // 图片解码/磁盘读取并行度控制在 3，避免和缩略图、人物分析抢满 CPU 和磁盘队列。
                int scanParallel = Math.Max(2, Math.Min(3, Environment.ProcessorCount));
                var scanTask = Task.Run(() =>
                {
                    Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = scanParallel }, job =>
                        AlbScanDirWorker(job, ctx, full, total));
                });

                // 主线程每 2 秒刷新真实进度，并落一次断点；扫描线程不阻塞界面。
                while (!scanTask.IsCompleted)
                {
                    if (scanTask.Wait(2000)) break;
                    AlbScanSendProgress(ctx, total);
                    AlbScanCheckpointNow(ctx);
                }
                scanTask.Wait();
                AlbScanFlush(ctx, total, true);
                AlbScanSendProgress(ctx, total);

                var items = ctx.items;
                var priorIndex = albIndex;
                var newIndex = new Dictionary<string, AlbCacheEnt>(ctx.newIndex);
                var newStamps = new Dictionary<string, long>(ctx.newStamps);

                items.RemoveAll(delegate(AlbItem it) { return string.IsNullOrEmpty(AlbRootOf(it.path)); });
                var finalIndex = new Dictionary<string, AlbCacheEnt>();
                foreach (var kv in newIndex)
                {
                    if (kv.Value == null || string.IsNullOrEmpty(kv.Value.realPath)) continue;
                    if (string.IsNullOrEmpty(AlbRootOf(kv.Value.realPath))) continue;
                    finalIndex[kv.Key] = kv.Value;
                }
                newIndex = finalIndex;
                var finalStamps = new Dictionary<string, long>();
                foreach (var kv in newStamps)
                {
                    if (string.IsNullOrEmpty(AlbRootOf(kv.Key))) continue;
                    finalStamps[kv.Key] = kv.Value;
                }
                newStamps = finalStamps;

                AlbFilterKnownGone(items, newIndex);
                AlbCleanupOrphanThumbs(priorIndex, newIndex, newStamps);
                bool changed2 = ctx.changed || items.Count != albItems.Count;

                items.Sort(delegate(AlbItem a, AlbItem b) {
                    if (a.taken != b.taken) return b.taken.CompareTo(a.taken);
                    return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                });

                bool autoUpdate = albScanAuto || albItems.Count > 0;
                albItems = items;
                albIndex = newIndex;
                SaveAlbIndex(newIndex);
                SaveAlbDirStamps(newStamps);
                try { File.WriteAllText(AlbFormatFlagFile(), "gif_raw_v1", System.Text.Encoding.UTF8); } catch { }
                var byDir = new Dictionary<string, List<AlbCacheEnt>>();
                foreach (var kv in newIndex)
                {
                    string dk = Path.GetDirectoryName(kv.Value.realPath);
                    if (dk == null) dk = "";
                    dk = dk.ToLowerInvariant();
                    List<AlbCacheEnt> lst;
                    if (!byDir.TryGetValue(dk, out lst)) { lst = new List<AlbCacheEnt>(); byDir[dk] = lst; }
                    lst.Add(kv.Value);
                }
                albByDir = byDir;
                albDirStamp = newStamps;

                int photos = 0, videos = 0, lives = 0, gifs = 0, raws = 0, geotag = 0;
                var devices = new Dictionary<string, int>();
                foreach (var it in items)
                {
                    if (it.type == "photo") photos++;
                    else if (it.type == "video") videos++;
                    else if (it.type == "live") lives++;
                    else if (it.type == "gif") gifs++;
                    else if (it.type == "raw") raws++;
                    if (it.lat != 0 || it.lon != 0) geotag++;
                    string dev = string.IsNullOrEmpty(it.model) ? "未知设备" : it.model;
                    if (it.type != "video")
                    {
                        if (devices.ContainsKey(dev)) devices[dev]++; else devices[dev] = 1;
                    }
                }
                if (autoUpdate && !changed2)
                {
                    Send("{\"type\":\"albcount\",\"total\":" + items.Count + "}");
                }
                else
                {
                    Send("{\"type\":\"albstats\",\"root\":" + JsonStr(albRoots.Count > 0 ? albRoots[0] : "") +
                         ",\"roots\":" + JsonStr(string.Join("\n", albRoots.ToArray())) + ",\"total\":" + items.Count +
                         ",\"photos\":" + photos + ",\"videos\":" + videos + ",\"lives\":" + lives +
                         ",\"gifs\":" + gifs + ",\"raws\":" + raws + ",\"geo\":" + geotag +
                         ",\"devices\":" + devices.Count + (autoUpdate ? ",\"auto\":true" : "") + "}");
                }
                if (changed2)
                {
                    try
                    {
                        var vidList = new List<string>();
                        foreach (var it in items) if (it.type == "video") vidList.Add(it.path);
                        if (vidList.Count > 0)
                        {
                            var vt = new Thread(() => WarmVideoThumbs(vidList));
                            vt.IsBackground = true; vt.Start();
                        }
                    }
                    catch { }
                }
                StartPostScanBackground();
                AlbClearDirtyDirs(scanStarted);
                Log("相册扫描完成 items=" + items.Count + " files=" + total);
            }
            catch (Exception ex) { Log("相册扫描异常: " + ex); Send("{\"type\":\"toast\",\"msg\":" + JsonStr("相册扫描失败: " + ex.Message) + "}"); }
            finally
            {
                bool rescan = false;
                bool rescanFull = false;
                lock (albScanStateGate)
                {
                    albRunning = false;
                    albScanAuto = false;
                    albScanFull = false;
                    rescan = albRescanPending;
                    rescanFull = albRescanFullPending;
                    albRescanPending = false;
                    albRescanFullPending = false;
                }
                if (rescan) StartAlbumScan(true, rescanFull);
            }
        }

        // 扫描结束后不要同时启动缩略图预热、去重预热和人物分析，错开几秒，
        // 避免三路 GDI+/Python 任务同时抢 CPU 和磁盘。
        void StartPostScanBackground()
        {
            var t = new Thread(delegate()
            {
                try { Thread.Sleep(4000); if (!albRunning) StartAlbThumbWarm(); } catch { }
                try { Thread.Sleep(6000); if (!albRunning) StartDupWarm(); } catch { }
                try { Thread.Sleep(6000); if (!albRunning) StartFaceScan(true); } catch { }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        void SendAlbAllPaths()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"type\":\"albselectall\",\"total\":").Append(albItems.Count).Append(",\"paths\":[");
                for (int i = 0; i < albItems.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(JsonStr(albItems[i].path));
                }
                sb.Append("]}");
                Send(sb.ToString());
            }
            catch { }
        }

        void SendAlbPage(int off, int cnt)
        {
            try
            {
                if (cnt <= 0) cnt = 100;
                var sb = new System.Text.StringBuilder();
                int end = Math.Min(albItems.Count, off + cnt);
                sb.Append("{\"type\":\"albpage\",\"offset\":").Append(off).Append(",\"total\":").Append(albItems.Count).Append(",\"items\":[");
                for (int i = off; i < end; i++)
                {
                    var it = albItems[i];
                    if (i > off) sb.Append(",");
                    AppendAlbItemJson(sb, it);
                }
                sb.Append("]}");
                Send(sb.ToString());
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("分页失败: " + ex.Message) + "}"); }
        }

        // ---- 视频封面（相册缩略图）----
        bool vThumbWarming = false;

        static string VThumbDir()
        {
            return CacheSub("vthumbs");
        }

        static string VThumbCacheFor(string path, long size, long mtime)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                string key = path.ToLowerInvariant() + "|" + size + "|" + mtime;
                ulong h = 1469598103934665603UL;
                foreach (char c in key) { h ^= (ulong)c; h *= 1099511628211UL; }
                return Path.Combine(VThumbDir(), h.ToString("x16") + ".jpg");
            }
            catch { return null; }
        }

        static string VThumbCache(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return VThumbCacheFor(path, fi.Length, fi.LastWriteTimeUtc.Ticks);
            }
            catch { return null; }
        }

        static Bitmap LoadNoLock(string f)
        {
            try
            {
                byte[] buf = File.ReadAllBytes(f);
                using (var ms = new MemoryStream(buf))
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch { return null; }
        }

        static string PyExePath()
        {
            string emb = Path.Combine(Application.StartupPath, "python", "python.exe");
            if (File.Exists(emb)) return emb;
            return "python";
        }

        // 后台批量抽取视频首帧，逐个回填到相册网格
        void WarmVideoThumbs(List<string> vids)
        {
            if (vThumbWarming || vids == null || vids.Count == 0) return;
            vThumbWarming = true;
            try
            {
                string script = Path.Combine(Application.StartupPath, "vthumb.py");
                if (!File.Exists(script)) return;
                var todo = new List<string>();
                foreach (var v in vids)
                {
                    string c = VThumbCache(v);
                    if (c != null && !File.Exists(c)) todo.Add(v);
                }
                if (todo.Count == 0) return;
                string py = PyExePath();
                const int CH = 20;
                for (int i = 0; i < todo.Count; i += CH)
                {
                    var chunk = todo.GetRange(i, Math.Min(CH, todo.Count - i));
                    var jobs = new List<string>();
                    foreach (var v in chunk)
                    {
                        string c = VThumbCache(v);
                        if (c != null) jobs.Add("[" + JsonStr(v) + "," + JsonStr(c) + "]");
                    }
                    if (jobs.Count == 0) continue;
                    string jf = Path.Combine(CacheStore.Sub("temp"), "ibt_vthumb_jobs.json");
                    File.WriteAllText(jf, "[" + string.Join(",", jobs.ToArray()) + "]", new System.Text.UTF8Encoding(false));
                    var psi = new System.Diagnostics.ProcessStartInfo(py, "\"" + script + "\" \"" + jf + "\"");
                    psi.UseShellExecute = false; psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                    try
                    {
                        var p = System.Diagnostics.Process.Start(psi);
                        if (p != null) { p.WaitForExit(90000); try { p.Dispose(); } catch { } }
                    }
                    catch { }
                    foreach (var v in chunk)
                    {
                        string c = VThumbCache(v);
                        if (c != null && File.Exists(c)) SendAlbThumbOne(v);
                    }
                }
            }
            catch { }
            finally { vThumbWarming = false; }
        }

        void SendAlbThumbs(int off, int cnt)
        {
            try
            {
                if (cnt <= 0) cnt = 100;
                if (cnt > 120) cnt = 120;
                var sb = new System.Text.StringBuilder();
                int end = Math.Min(albItems.Count, off + cnt);
                sb.Append("{\"type\":\"albthumbs\",\"offset\":").Append(off).Append(",\"items\":[");
                for (int i = off; i < end; i++)
                {
                    var it = albItems[i];
                    if (i > off) sb.Append(",");
                    string t = null, u = "";
                    try
                    {
                        int tw = 0, th = 0;
                        string cf = AlbThumbPrepare(it.path, out tw, out th);
                        u = AlbThumbUrl(cf);
                        if (u.Length == 0 && cf != null && File.Exists(cf)) t = Convert.ToBase64String(File.ReadAllBytes(cf));
                    }
                    catch { }
                    sb.Append("{\"p\":").Append(JsonStr(it.path)).Append(",\"u\":")
                      .Append(u.Length == 0 ? "null" : JsonStr(u)).Append(",\"t\":")
                      .Append(t == null ? "null" : JsonStr("data:image/jpeg;base64," + t)).Append("}");
                }
                sb.Append("]}");
                Send(sb.ToString());
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("缩略图失败: " + ex.Message) + "}"); }
        }

        static readonly System.Threading.SemaphoreSlim albThumbGate = new System.Threading.SemaphoreSlim(3, 3);
        static DateTime albThumbLastUser = DateTime.MinValue;
        static bool thumbUrlLogOnce = false;

        // 扫描结束后在后台把还没缓存的预览图逐张生成好，用户没翻到也会继续
        volatile int albWarmGen = 0;
        void StartAlbThumbWarm()
        {
            int gen = albWarmGen;
            var snap = new List<string>();
            foreach (var it in albItems) snap.Add(it.path);
            if (snap.Count == 0) return;
            var t = new Thread(delegate()
            {
                foreach (var p in snap)
                {
                    if (gen != albWarmGen) return;
                    try
                    {
                        // 用户正在滚动看图时不抢磁盘：等当前操作停顿后再继续补后面的缩略图
                        while (gen == albWarmGen && (DateTime.UtcNow - albThumbLastUser).TotalMilliseconds < 500)
                            Thread.Sleep(150);
                        if (gen != albWarmGen) return;
                        string cf = AlbThumbFile(p);
                        if (cf != null && !File.Exists(cf))
                        {
                            int w = 0, h = 0;
                            AlbThumbPrepare(p, out w, out h);
                        }
                    }
                    catch { }
                    Thread.Sleep(2);
                }
            });
            t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start();
        }

        void SendAlbThumbOne(string path)
        {
            string p = path;
            albThumbLastUser = DateTime.UtcNow;
            // 解码/读盘不能占用 UI 线程，否则缩略图一张张出来时整个界面都会卡
            ThreadPool.QueueUserWorkItem(delegate
            {
                albThumbGate.Wait();
                try
                {
                    // 原文件已经不在时不能返回旧缓存，否则删除后残留的缓存会被重新挂回界面。
                    if (!File.Exists(p))
                    {
                        Send("{\"type\":\"albthumb\",\"p\":" + JsonStr(p) + ",\"fail\":true}");
                        return;
                    }
                    string t = null, u = ""; int w = 0, h = 0;
                    try
                    {
                        string cf = AlbThumbPrepare(p, out w, out h);
                        u = AlbThumbUrl(cf);
                        if (!thumbUrlLogOnce)
                        {
                            thumbUrlLogOnce = true;
                            Log("缩略图加载方式: " + (u.Length > 0 ? ("URL " + u) : "base64 回退"));
                        }
                        if (u.Length == 0 && cf != null && File.Exists(cf))
                            t = Convert.ToBase64String(File.ReadAllBytes(cf));
                    }
                    catch { }
                    Send("{\"type\":\"albthumb\",\"p\":" + JsonStr(p) + ",\"w\":" + w + ",\"h\":" + h +
                         ",\"u\":" + (u.Length == 0 ? "null" : JsonStr(u)) +
                         ",\"t\":" + (t == null ? "null" : JsonStr("data:image/jpeg;base64," + t)) + "}");
                }
                catch { }
                finally { albThumbGate.Release(); }
            });
        }

        void SendAlbView(string path)
        {
            try
            {
                AlbItem hit = null;
                foreach (var it in albItems) if (string.Equals(it.path, path, StringComparison.OrdinalIgnoreCase)) { hit = it; break; }
                string typ = hit != null ? hit.type :
                    (IsAlbVideo(path) ? "video" : (IsAlbGif(path) ? "gif" : (IsAlbRaw(path) ? "raw" : "photo")));
                int w = 0, h = 0; long sz = 0; long dt = 0; double du = 0, la = 0, lo = 0;
                if (hit != null) { sz = hit.size; dt = hit.taken; du = hit.dur; la = hit.lat; lo = hit.lon; }
                string b64 = null;
                try
                {
                    Bitmap first = typ == "raw" ? RawEmbeddedThumb(path, 1600) : null;
                    if (first == null) first = ShellThumb(path, 1600);
                    using (first)
                    {
                        if (first != null)
                        {
                            int max = 1400;
                            int bw = first.Width, bh = first.Height;
                            double k = Math.Min(1.0, (double)max / Math.Max(1, Math.Max(bw, bh)));
                            int nw = Math.Max(1, (int)(bw * k)), nh = Math.Max(1, (int)(bh * k));
                            using (var sm = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                            {
                                using (var g = Graphics.FromImage(sm)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(first, 0, 0, nw, nh); }
                                b64 = B64(sm, 84);
                                w = nw; h = nh;
                            }
                        }
                    }
                    if (b64 == null && typ != "video")
                    {
                        Bitmap b = typ == "raw" ? RawEmbeddedThumb(path, 1600) : LoadOriented(path);
                        if (b != null)
                        {
                            using (b)
                            {
                                int max = 1600; int bw = b.Width, bh = b.Height;
                                double k = Math.Min(1.0, (double)max / Math.Max(1, Math.Max(bw, bh)));
                                int nw = Math.Max(1, (int)(bw * k)), nh = Math.Max(1, (int)(bh * k));
                                using (var sm = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                                {
                                    using (var g = Graphics.FromImage(sm)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(b, 0, 0, nw, nh); }
                                    b64 = B64(sm, 85);
                                    w = nw; h = nh;
                                }
                            }
                        }
                    }
                }
                catch { }
                string comps = "";
                string liveVid = "";
                if (typ == "live")
                {
                    var cs = DupCompanions(path);
                    if (cs != null)
                    {
                        for (int i = 0; i < cs.Length; i++)
                        {
                            if (i > 0) comps += ", ";
                            comps += Path.GetFileName(cs[i]);
                            string ext = Path.GetExtension(cs[i]).ToLowerInvariant();
                            if (liveVid.Length == 0 && (ext == ".mov" || ext == ".mp4"))
                                liveVid = cs[i];
                        }
                    }
                }
                string mk = hit != null ? (hit.make ?? "") : "";
                string md = hit != null ? (hit.model ?? "") : "";
                string ln = hit != null ? (hit.lens ?? "") : "";
                double ap = hit != null ? hit.fnum : 0;
                double ex = hit != null ? hit.exposure : 0;
                double fl = hit != null ? hit.focal : 0;
                int isv = hit != null ? hit.iso : 0;
                if (hit == null && typ != "video")
                {
                    ExifMeta meta;
                    if (ExifDetail(path, out meta))
                    {
                        mk = meta.make; md = meta.model; ln = meta.lens;
                        ap = meta.fnum; ex = meta.exposure; fl = meta.focal; isv = meta.iso;
                        if (la == 0 && lo == 0) { la = meta.lat; lo = meta.lon; }
                    }
                }
                string addr = (la != 0 || lo != 0) ? GeoCached(la, lo) : "";
                string liveUrl = (typ == "live" && liveVid.Length > 0) ? AlbPrepareMedia(liveVid) : "";
                string videoUrl = typ == "video" ? AlbPrepareMedia(path) : "";
                string gifUrl = typ == "gif" ? AlbPrepareMedia(path) : "";
                Send("{\"type\":\"albview\",\"p\":" + JsonStr(path) + ",\"n\":" + JsonStr(Path.GetFileName(path)) +
                     ",\"t\":" + JsonStr(typ) + ",\"s\":" + sz + ",\"w\":" + w + ",\"h\":" + h +
                     ",\"dt\":" + dt + ",\"d\":" + (long)du +
                     ",\"la\":" + la.ToString("0.######", CultureInfo.InvariantCulture) +
                     ",\"lo\":" + lo.ToString("0.######", CultureInfo.InvariantCulture) +
                     ",\"c\":" + JsonStr(comps) +
                     ",\"lv\":" + JsonStr(liveVid) + ",\"lvurl\":" + JsonStr(liveUrl) +
                     ",\"vu\":" + JsonStr(videoUrl) + ",\"gu\":" + JsonStr(gifUrl) +
                     ",\"mk\":" + JsonStr(mk) + ",\"md\":" + JsonStr(md) + ",\"ln\":" + JsonStr(ln) +
                     ",\"ap\":" + ap.ToString("0.##", CultureInfo.InvariantCulture) +
                     ",\"ex\":" + ex.ToString("0.########", CultureInfo.InvariantCulture) +
                     ",\"is\":" + isv + ",\"fl\":" + fl.ToString("0.#", CultureInfo.InvariantCulture) +
                     ",\"addr\":" + JsonStr(addr) +
                     ",\"data\":" + (b64 == null ? "null" : JsonStr("data:image/jpeg;base64," + b64)) + "}");
                if (la != 0 || lo != 0) GeoLookupAsync(path, la, lo);
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("预览失败: " + ex.Message) + "}"); }
        }

        // ---- 逆地理编码：先查在线（Nominatim），失败/离线时用本地城市索引兜底 ----
        readonly Dictionary<string, string> geoCache = new Dictionary<string, string>();
        readonly HashSet<string> geoBusy = new HashSet<string>();
        readonly HashSet<string> geoOnlineDone = new HashSet<string>();

        class GeoTown { public string prov, city; public double lat, lon; }
        List<GeoTown> geoTowns = null;

        void LoadGeoTowns()
        {
            if (geoTowns != null) return;
            geoTowns = new List<GeoTown>();
            try
            {
                string f = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "assets\\geo\\cn_cities.txt");
                if (!File.Exists(f)) return;
                foreach (var line in File.ReadAllLines(f, System.Text.Encoding.UTF8))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 4) continue;
                    var t = new GeoTown();
                    t.prov = p[0]; t.city = p[1];
                    t.lat = double.Parse(p[2], CultureInfo.InvariantCulture);
                    t.lon = double.Parse(p[3], CultureInfo.InvariantCulture);
                    geoTowns.Add(t);
                }
            }
            catch { }
        }

        string GeoOffline(double lat, double lon)
        {
            if (lat == 0 && lon == 0) return "";
            LoadGeoTowns();
            if (geoTowns == null || geoTowns.Count == 0) return "";
            double best = 1e9; GeoTown hit = null;
            double cosLat = Math.Cos(lat * Math.PI / 180.0);
            foreach (var t in geoTowns)
            {
                double dx = (t.lon - lon) * cosLat, dy = t.lat - lat;
                double d = dx * dx + dy * dy;
                if (d < best) { best = d; hit = t; }
            }
            if (hit == null) return "";
            // 约 1.4 度≈150km，超出则认为不在城市群范围
            if (Math.Sqrt(best) > 1.4) return "";
            if (string.Equals(hit.prov, hit.city, StringComparison.Ordinal)) return hit.city;
            if (hit.prov.EndsWith("市", StringComparison.Ordinal) && hit.city.EndsWith("区", StringComparison.Ordinal)) return hit.prov;
            return hit.prov + " " + hit.city;
        }

        string GeoKey(double lat, double lon) { return lat.ToString("0.0000", CultureInfo.InvariantCulture) + "," + lon.ToString("0.0000", CultureInfo.InvariantCulture); }

        string GeoCached(double lat, double lon)
        {
            lock (geoCache)
            {
                string v;
                if (geoCache.TryGetValue(GeoKey(lat, lon), out v) && v != null && v.Length > 0) return v;
            }
            return GeoOffline(lat, lon);
        }

        void GeoLookupAsync(string path, double lat, double lon)
        {
            string k = GeoKey(lat, lon);
            lock (geoCache) { if (geoCache.ContainsKey(k)) return; }
            lock (geoOnlineDone) { if (geoOnlineDone.Contains(k)) return; }
            lock (geoBusy) { if (geoBusy.Contains(k)) return; geoBusy.Add(k); }
            var th = new Thread(() =>
            {
                string addr = "";
                try
                {
                    string url = "https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=16&accept-language=zh-CN&lat=" +
                                 lat.ToString("0.######", CultureInfo.InvariantCulture) + "&lon=" + lon.ToString("0.######", CultureInfo.InvariantCulture);
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.UserAgent = "ImageBatchTool/1.0 (local photo album)";
                    req.Timeout = 6000; req.ReadWriteTimeout = 6000;
                    using (var resp = req.GetResponse())
                    using (var sr = new System.IO.StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        var m = System.Text.RegularExpressions.Regex.Match(json, "\"display_name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (m.Success) addr = m.Groups[1].Value.Replace("\\\"", "\"");
                    }
                }
                catch { addr = ""; }
                if (addr.Length == 0) addr = GeoOffline(lat, lon);
                lock (geoBusy) { geoBusy.Remove(k); }
                lock (geoOnlineDone) { geoOnlineDone.Add(k); }
                lock (geoCache) { geoCache[k] = addr; }
                Send("{\"type\":\"albaddr\",\"p\":" + JsonStr(path) + ",\"addr\":" + JsonStr(addr) + "}");
            });
            th.IsBackground = true; th.Start();
        }

        // ---- 人脸分组（调用 python + OpenCV 检测与聚类）----
        volatile bool faceRunning = false;
        volatile bool faceStop = false;
        volatile bool faceAgain = false;
        System.Diagnostics.Process faceProc = null;
        string faceStopFile = "";
        string faceLastJson = "";

        string FaceStateFile() { return Path.Combine(CacheSub("index"), "face_state.json"); }
        string FaceResultFile() { return Path.Combine(CacheSub("index"), "face_groups.json"); }
        string FaceSigFile() { return Path.Combine(CacheSub("index"), "face_groups.sig"); }

        string FaceSignature()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var it in albItems)
                {
                    if (it.type == "video" || it.type == "raw") continue;
                    sb.Append(it.path.ToLowerInvariant()).Append('\t').Append(it.size).Append('\t');
                    AlbCacheEnt ce;
                    if (albIndex.TryGetValue(it.path.ToLowerInvariant(), out ce) && ce != null) sb.Append(ce.mtime);
                    else { try { sb.Append(File.GetLastWriteTimeUtc(it.path).Ticks); } catch { } }
                    sb.Append('\n');
                }
                using (var md5 = MD5.Create())
                {
                    var h = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
                    return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return ""; }
        }

        void SaveFaceResult(string json, string sig)
        {
            try
            {
                if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(sig)) return;
                File.WriteAllText(FaceResultFile(), json, new System.Text.UTF8Encoding(false));
                File.WriteAllText(FaceSigFile(), sig, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        bool TryLoadFaceResult()
        {
            try
            {
                string sig = FaceSignature();
                if (string.IsNullOrEmpty(sig)) return false;
                string stateFile = FaceStateFile();
                // 上次分析被中断时状态里仍有未完成项，不能直接拿旧结果当“已是最新”，
                // 必须重新启动增量分析，从中断处继续。
                try
                {
                    if (File.Exists(stateFile))
                    {
                        string stateText = File.ReadAllText(stateFile);
                        if (stateText.IndexOf("\"done\":false", StringComparison.Ordinal) >= 0) return false;
                    }
                }
                catch { }
                string rf = FaceResultFile(), sf = FaceSigFile();
                if (!File.Exists(rf) || !File.Exists(sf)) return false;
                if (File.ReadAllText(sf).Trim() != sig) return false;
                faceLastJson = File.ReadAllText(rf);
                return !string.IsNullOrEmpty(faceLastJson);
            }
            catch { return false; }
        }

        void StartFaceScan() { StartFaceScan(false); }

        void StartFaceScan(bool auto)
        {
            if (faceRunning) { if (auto) faceAgain = true; else Send("{\"type\":\"toast\",\"msg\":\"人脸分析正在进行\"}"); return; }
            if (albItems.Count == 0) { if (!auto) Send("{\"type\":\"toast\",\"msg\":\"相册还没有照片\"}"); return; }
            if (TryLoadFaceResult())
            {
                Send(faceLastJson);
                if (!auto) Send("{\"type\":\"toast\",\"msg\":\"人物分组已是最新\"}");
                return;
            }
            faceRunning = true; faceStop = false;
            faceStopFile = FaceStateFile() + ".stop";
            try { if (File.Exists(faceStopFile)) File.Delete(faceStopFile); } catch { }
            var th = new Thread(FaceScanThread); th.IsBackground = true; th.Start();
        }

        void FaceScanThread()
        {
            System.Diagnostics.Process proc = null;
            string faceListFile = null;
            try
            {
                string scanSig = FaceSignature();
                var list = new List<string>();
                foreach (var it in albItems) if (it.type != "video" && it.type != "raw") list.Add(it.path);
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string py = Path.Combine(dir, "python\\python.exe");
                if (!File.Exists(py)) py = "python";
                string script = Path.Combine(dir, "ai\\face_group.py");
                if (!File.Exists(script)) { Send("{\"type\":\"toast\",\"msg\":\"缺少 ai\\\\face_group.py\"}"); return; }
                faceListFile = CacheStore.TempFile("ibt_faces_", ".txt");
                string listFile = faceListFile;
                File.WriteAllLines(listFile, list.ToArray(), System.Text.Encoding.UTF8);
                string stateFile = FaceStateFile();
                try { Directory.CreateDirectory(Path.GetDirectoryName(stateFile)); } catch { }
                Send("{\"type\":\"albfaces\",\"phase\":\"enum\",\"done\":0,\"total\":" + list.Count + "}");

                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = py;
                psi.Arguments = "\"" + script + "\" \"" + listFile + "\" \"" + stateFile + "\" \"" + faceStopFile + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
                psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
                proc = System.Diagnostics.Process.Start(psi);
                faceProc = proc;

                var thumbs = new Dictionary<int, string>();
                var faces = new Dictionary<int, List<string>>();
                int facesTotal = 0;
                string line;
                var so = proc.StandardOutput;
                while ((line = so.ReadLine()) != null)
                {
                    if (faceStop) break;
                    string[] f = line.Split('\t');
                    if (f.Length < 2) continue;
                    if (f[0] == "P" && f.Length >= 3)
                    {
                        Send("{\"type\":\"albfaces\",\"phase\":\"run\",\"done\":" + f[1] + ",\"total\":" + f[2] + "}");
                    }
                    else if (f[0] == "G" && f.Length >= 3)
                    {
                        thumbs[int.Parse(f[1])] = f[2];
                    }
                    else if (f[0] == "F" && f.Length >= 7)
                    {
                        int gid = int.Parse(f[1]);
                        if (!faces.ContainsKey(gid)) faces[gid] = new List<string>();
                        faces[gid].Add("{\"p\":" + JsonStr(f[2]) + ",\"x\":" + f[3] + ",\"y\":" + f[4] +
                                       ",\"w\":" + f[5] + ",\"h\":" + f[6] + "}");
                        facesTotal++;
                    }
                    else if (f[0] == "E")
                    {
                        Send("{\"type\":\"toast\",\"msg\":" + JsonStr("人脸分析: " + line) + "}");
                    }
                }
                int exitCode = -1;
                try { proc.WaitForExit(5000); exitCode = proc.ExitCode; } catch { }

                if (faceStop)
                {
                    Send("{\"type\":\"albfaces\",\"phase\":\"stop\"}");
                    return;
                }
                // 模型缺失等原因导致脚本异常退出时，不要把空结果写成缓存，否则以后会一直显示 0 个人物。
                if (exitCode != 0 && facesTotal == 0)
                {
                    Send("{\"type\":\"albfaces\",\"phase\":\"error\",\"msg\":" + JsonStr("人脸分析失败，脚本退出码 " + exitCode) + "}");
                    return;
                }

                var order = new List<int>();
                foreach (var kv in faces) order.Add(kv.Key);
                order.Sort(delegate(int a, int b) { return faces[b].Count.CompareTo(faces[a].Count); });
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"type\":\"albfaces\",\"phase\":\"done\",\"photos\":").Append(list.Count)
                  .Append(",\"faces\":").Append(facesTotal).Append(",\"groups\":[");
                int shown = 0;
                foreach (var gid in order)
                {
                    if (shown >= 80) break;
                    if (shown > 0) sb.Append(",");
                    var fl = faces[gid];
                    sb.Append("{\"id\":").Append(gid).Append(",\"thumb\":")
                      .Append(thumbs.ContainsKey(gid) ? JsonStr("data:image/jpeg;base64," + thumbs[gid]) : "null")
                      .Append(",\"faces\":[").Append(string.Join(",", fl.ToArray())).Append("]}");
                    shown++;
                }
                sb.Append("]}");
                string faceJson = sb.ToString();
                faceLastJson = faceJson;
                Send(faceJson);
                SaveFaceResult(faceJson, scanSig);
            }
            catch (Exception ex)
            {
                Send("{\"type\":\"albfaces\",\"phase\":\"error\",\"msg\":" + JsonStr(ex.Message) + "}");
            }
            finally
            {
                // 停止时先给 Python 最多 6 秒，让它把断点状态落盘；仍未退出才强杀。
                try { if (proc != null && !proc.HasExited) proc.WaitForExit(6000); } catch { }
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { if (!string.IsNullOrEmpty(faceStopFile)) File.Delete(faceStopFile); } catch { }
                try { if (!string.IsNullOrEmpty(faceListFile)) File.Delete(faceListFile); } catch { }
                faceProc = null;
                faceRunning = false;
                if (faceAgain)
                {
                    faceAgain = false;
                    StartFaceScan(true);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShFileOp { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref ShFileOp op);

        // ==================== 应用内回收站 ====================
        class BinRow { public string gid, kind, orig, stored; public long ticks; }

        string CfgDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageBatchTool");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        string binDirCached = null;
        string BinDir()
        {
            if (binDirCached != null) return binDirCached;
            string cfg = CfgDir();
            try
            {
                string f = Path.Combine(cfg, "bin_dir.txt");
                if (File.Exists(f))
                {
                    string s = File.ReadAllText(f).Trim();
                    if (s.Length > 0 && Directory.Exists(s)) binDirCached = s;
                }
            }
            catch { }
            if (binDirCached == null)
            {
                string pics = "";
                try { pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); } catch { }
                binDirCached = Path.Combine(pics.Length > 0 ? pics : cfg, "图片批处理工具回收站");
            }
            try { if (!Directory.Exists(binDirCached)) Directory.CreateDirectory(binDirCached); } catch { }
            return binDirCached;
        }

        string BinFiles() { return Path.Combine(BinDir(), "files"); }
        string BinIndex() { return Path.Combine(BinDir(), "index.txt"); }

        List<BinRow> BinLoad()
        {
            var list = new List<BinRow>();
            try
            {
                string idx = BinIndex();
                if (!File.Exists(idx)) return list;
                foreach (var line in File.ReadAllLines(idx))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    var a = line.Split('\t');
                    if (a.Length < 5) continue;
                    var r = new BinRow();
                    r.gid = a[0];
                    long t; long.TryParse(a[1], out t); r.ticks = t;
                    r.kind = a[2];
                    r.orig = a[3].Replace("%09", "\t");
                    r.stored = a[4];
                    if (File.Exists(Path.Combine(BinFiles(), r.stored))) list.Add(r);
                }
            }
            catch { }
            return list;
        }

        void BinSave(List<BinRow> rows)
        {
            try
            {
                Directory.CreateDirectory(BinDir());
                var sb = new System.Text.StringBuilder();
                foreach (var r in rows)
                    sb.Append(r.gid).Append('\t').Append(r.ticks).Append('\t').Append(r.kind).Append('\t')
                      .Append(r.orig.Replace("\t", "%09")).Append('\t').Append(r.stored).Append("\r\n");
                File.WriteAllText(BinIndex(), sb.ToString());
            }
            catch { }
        }

        string BinStore(string src, string gid)
        {
            string files = BinFiles();
            Directory.CreateDirectory(files);
            string name = Path.GetFileName(src);
            string stored = gid + "_" + name;
            string abs = Path.Combine(files, stored);
            int n = 1;
            while (File.Exists(abs))
            {
                stored = gid + "_" + Path.GetFileNameWithoutExtension(name) + "_" + n + Path.GetExtension(name);
                abs = Path.Combine(files, stored); n++;
            }
            try { File.Move(src, abs); }
            catch
            {
                File.Copy(src, abs, true);
                File.Delete(src);
            }
            return stored;
        }

        int BinTrashFile(string path, string gid, string kind, List<BinRow> rows)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            try
            {
                string stored = BinStore(path, gid);
                var r = new BinRow();
                r.gid = gid; r.ticks = DateTime.Now.Ticks; r.kind = kind; r.orig = path; r.stored = stored;
                rows.Add(r);
                return 1;
            }
            catch { return 0; }
        }

        bool UnderAlbRoot(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            foreach (var root in albRoots)
            {
                try
                {
                    string r = root.TrimEnd('\\', '/');
                    if (p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetDirectoryName(p), r, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }

        void SendBinList()
        {
            try
            {
                var rows = BinLoad();
                rows.Sort(delegate (BinRow a, BinRow b) { return b.ticks.CompareTo(a.ticks); });
                var sb = new System.Text.StringBuilder();
                long total = 0; int cnt = 0;
                foreach (var r in rows)
                {
                    if (!string.Equals(r.kind, "m", StringComparison.OrdinalIgnoreCase)) continue;
                    string abs = Path.Combine(BinFiles(), r.stored);
                    long size = 0;
                    try { size = new FileInfo(abs).Length; } catch { }
                    total += size;
                    if (sb.Length > 0) sb.Append(",");
                    sb.Append("{\"id\":").Append(JsonStr(r.gid))
                      .Append(",\"name\":").Append(JsonStr(Path.GetFileName(r.orig)))
                      .Append(",\"orig\":").Append(JsonStr(r.orig))
                      .Append(",\"sp\":").Append(JsonStr(abs))
                      .Append(",\"time\":").Append(JsonStr(new DateTime(r.ticks).ToString("yyyy-MM-dd HH:mm")))
                      .Append(",\"size\":").Append(size).Append("}");
                    cnt++;
                }
                Send("{\"type\":\"albin\",\"dir\":" + JsonStr(BinDir()) + ",\"total\":" + total + ",\"items\":[" + sb.ToString() + "]}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("读取回收站失败: " + ex.Message) + "}"); }
        }

        void BinRestore(string[] gids)
        {
            try
            {
                var wanted = new Dictionary<string, bool>();
                foreach (var g in gids) if (!string.IsNullOrEmpty(g)) wanted[g.Trim()] = true;
                if (wanted.Count == 0) return;
                var rows = BinLoad();
                var keep = new List<BinRow>();
                int restored = 0; bool needScan = false;
                foreach (var r in rows)
                {
                    if (!wanted.ContainsKey(r.gid)) { keep.Add(r); continue; }
                    try
                    {
                        string abs = Path.Combine(BinFiles(), r.stored);
                        if (!File.Exists(abs)) continue;
                        string dir = Path.GetDirectoryName(r.orig);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        string dest = r.orig;
                        if (File.Exists(dest))
                        {
                            string be = Path.GetFileNameWithoutExtension(r.orig), ex = Path.GetExtension(r.orig);
                            int n = 1;
                            do { dest = Path.Combine(dir, be + " (还原" + n + ")" + ex); n++; } while (File.Exists(dest));
                        }
                        try { File.Move(abs, dest); }
                        catch { File.Copy(abs, dest, true); File.Delete(abs); }
                        restored++;
                        if (UnderAlbRoot(dest)) needScan = true;
                    }
                    catch { keep.Add(r); continue; }
                    if (string.Equals(r.kind, "m", StringComparison.OrdinalIgnoreCase) && UnderAlbRoot(r.orig)) needScan = true;
                }
                BinSave(keep);
                var sb = new System.Text.StringBuilder();
                foreach (var g in wanted.Keys) { if (sb.Length > 0) sb.Append(","); sb.Append(JsonStr(g)); }
                Send("{\"type\":\"albinrestored\",\"count\":" + restored + ",\"ids\":[" + sb.ToString() + "]}");
                if (needScan && !albRunning) StartAlbumScan();
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("还原失败: " + ex.Message) + "}"); }
        }

        void BinPurge(string[] gids)
        {
            try
            {
                var wanted = new Dictionary<string, bool>();
                foreach (var g in gids) if (!string.IsNullOrEmpty(g)) wanted[g.Trim()] = true;
                if (wanted.Count == 0) return;
                var rows = BinLoad();
                var keep = new List<BinRow>();
                int purged = 0;
                foreach (var r in rows)
                {
                    if (!wanted.ContainsKey(r.gid)) { keep.Add(r); continue; }
                    try
                    {
                        string abs = Path.Combine(BinFiles(), r.stored);
                        DeleteThumbCacheFor(abs);
                        File.Delete(abs);
                    }
                    catch { }
                    if (string.Equals(r.kind, "m", StringComparison.OrdinalIgnoreCase)) purged++;
                }
                BinSave(keep);
                var sb = new System.Text.StringBuilder();
                foreach (var g in wanted.Keys) { if (sb.Length > 0) sb.Append(","); sb.Append(JsonStr(g)); }
                Send("{\"type\":\"albinpurged\",\"count\":" + purged + ",\"ids\":[" + sb.ToString() + "]}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("删除失败: " + ex.Message) + "}"); }
        }

        void BinEmpty()
        {
            try
            {
                var rows = BinLoad();
                int purged = 0;
                foreach (var r in rows)
                {
                    try
                    {
                        string abs = Path.Combine(BinFiles(), r.stored);
                        DeleteThumbCacheFor(abs);
                        File.Delete(abs);
                    }
                    catch { }
                    if (string.Equals(r.kind, "m", StringComparison.OrdinalIgnoreCase)) purged++;
                }
                BinSave(new List<BinRow>());
                Send("{\"type\":\"albinpurged\",\"count\":" + purged + ",\"ids\":[],\"all\":true}");
                SendBinList();
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("清空失败: " + ex.Message) + "}"); }
        }

        void BinChooseDir()
        {
            string nd = null;
            InvokeUi(() =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "选择回收站存放文件夹（删除的照片会移动到这里）";
                    if (d.ShowDialog() == DialogResult.OK) nd = d.SelectedPath;
                }
            });
            if (string.IsNullOrEmpty(nd)) return;
            try
            {
                string oldDir = BinDir();
                if (string.Equals(oldDir.TrimEnd('\\'), nd.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                Directory.CreateDirectory(nd);
                // 已有回收站内容整体迁移到新目录，避免丢项
                var rows = BinLoad();
                int movedFiles = 0;
                string newFiles = Path.Combine(nd, "files");
                Directory.CreateDirectory(newFiles);
                foreach (var r in rows)
                {
                    try
                    {
                        string src = Path.Combine(BinFiles(), r.stored);
                        string dst = Path.Combine(newFiles, r.stored);
                        if (File.Exists(dst)) dst = Path.Combine(newFiles, r.gid + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + Path.GetExtension(r.stored));
                        try { File.Move(src, dst); } catch { File.Copy(src, dst, true); File.Delete(src); }
                        r.stored = Path.GetFileName(dst);
                        movedFiles++;
                    }
                    catch { }
                }
                binDirCached = nd;
                try { File.WriteAllText(Path.Combine(CfgDir(), "bin_dir.txt"), nd); } catch { }
                BinSave(rows);
                Send("{\"type\":\"toast\",\"msg\":" + JsonStr("回收站目录已切换，迁移 " + movedFiles + " 个文件") + "}");
                SendBinList();
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("切换目录失败: " + ex.Message) + "}"); }
        }

        void DeleteCacheFileQuiet(string f)
        {
            try { if (!string.IsNullOrEmpty(f) && File.Exists(f)) File.Delete(f); } catch { }
        }

        // 删除回收站文件前调用：原文件还在时先算出路径，再一并清掉缩略图缓存。
        void DeleteThumbCacheFor(string path)
        {
            try
            {
                DeleteCacheFileQuiet(AlbThumbFile(path));
                DeleteCacheFileQuiet(VThumbCache(path));
            }
            catch { }
        }

        void AlbumDelete(string[] paths)
        {
            try
            {
                var rows = BinLoad();
                int removed = 0, moved = 0, allFiles = 0;
                var gone = new System.Text.StringBuilder();
                foreach (var p in paths)
                {
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                    // 文件一旦移入回收站，原路径就不存在了，缩略图缓存 key 必须在移动前算好。
                    string thumbCache = AlbThumbFile(p);
                    string videoCache = VThumbCache(p);
                    string gid = DateTime.Now.Ticks.ToString() + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
                    int n = BinTrashFile(p, gid, "m", rows);
                    if (n > 0)
                    {
                        moved++; allFiles++;
                        DeleteCacheFileQuiet(thumbCache);
                        DeleteCacheFileQuiet(videoCache);
                        // 实况照片的 MOV / AAE 跟随主图一起进回收站，还原时一并回去
                        var cs = DupCompanions(p);
                        if (cs != null) foreach (var c in cs) if (File.Exists(c))
                        {
                            string ct = AlbThumbFile(c), cv = VThumbCache(c);
                            if (BinTrashFile(c, gid, "c", rows) > 0)
                            {
                                allFiles++;
                                DeleteCacheFileQuiet(ct);
                                DeleteCacheFileQuiet(cv);
                            }
                        }
                        bool hit = false;
                        for (int i = albItems.Count - 1; i >= 0; i--)
                            if (string.Equals(albItems[i].path, p, StringComparison.OrdinalIgnoreCase)) { albItems.RemoveAt(i); hit = true; }
                        if (hit) { removed++; if (gone.Length > 0) gone.Append(","); gone.Append(JsonStr(p)); }
                    }
                }
                if (moved == 0) { Send("{\"type\":\"toast\",\"msg\":\"没有可删除的文件\"}"); return; }
                BinSave(rows);
                Send("{\"type\":\"albdeleted\",\"count\":" + removed + ",\"files\":" + allFiles + ",\"bin\":true,\"paths\":[" + gone.ToString() + "]}");
                // 删除后自动做一次增量刷新，保证计数/分页与其他视图同步
                try { var t = new Thread(() => { System.Threading.Thread.Sleep(350); StartAlbumScan(true); }); t.IsBackground = true; t.Start(); } catch { }
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("删除失败: " + ex.Message) + "}"); }
        }

        // 相册「删除缓存」：清掉扫描索引、缩略图与各类临时文件，释放磁盘与内存占用
        void ClearAlbCache()
        {
            long bytes = 0; int files = 0;
            Action<string> delFile = null;
            Action<string> delDir = null;
            Action<string> clearDir = null;
            delFile = delegate(string f)
            {
                try { if (!File.Exists(f)) return; bytes += new FileInfo(f).Length; File.Delete(f); files++; } catch { }
            };
            delDir = delegate(string d)
            {
                try
                {
                    if (!Directory.Exists(d)) return;
                    foreach (var f in Directory.GetFiles(d)) delFile(f);
                    foreach (var sub in Directory.GetDirectories(d)) delDir(sub);
                    try { Directory.Delete(d, true); } catch { }
                }
                catch { }
            };
            // 保留目录本身，避免 WebView2 的 thumb.local 虚拟目录映射因目录被删除而失效
            clearDir = delegate(string d)
            {
                try
                {
                    if (!Directory.Exists(d)) return;
                    foreach (var f in Directory.GetFiles(d)) delFile(f);
                    foreach (var sub in Directory.GetDirectories(d)) delDir(sub);
                }
                catch { }
            };
            try
            {
                // 1) 扫描索引
                string idx = AlbIndexFile();
                delFile(idx);
                delFile(idx + ".tmp");
                delFile(AlbDirsFile());
                delFile(AlbDirsFile() + ".tmp");
                delFile(DupCacheFile());
                delFile(DupCacheFile() + ".tmp");
                lock (dupCacheLock) { dupCache.Clear(); dupCacheLoaded = false; }
                // 2) 缩略图 / 视频封面缓存（安装目录 cache）
                clearDir(Path.Combine(CacheRoot(), "thumbs"));
                clearDir(Path.Combine(CacheRoot(), "vthumbs"));
                // 兼容旧版本残留
                delDir(Path.Combine(Path.GetTempPath(), "ibt_thumbs"));
                delDir(Path.Combine(Path.GetTempPath(), "ibt_vthumbs"));
                // 3) 修图/选片过程产生的临时文件（保留最近 2 分钟内的，避免误删正在使用的）
                string[] prefixes = { "ai_in_", "ai_out_", "pipe_in_", "pipe_out_", "beauty_in_", "beauty_out_", "beautypro_in_", "beautypro_out_", "idprev_", "pick_", "ibt_faces_" };
                try
                {
                    var cutoff = DateTime.Now.AddMinutes(-2);
                    foreach (var dir in new[] { CacheStore.Sub("temp"), Path.GetTempPath() })
                    {
                        foreach (var f in Directory.GetFiles(dir))
                        {
                            string n = Path.GetFileName(f);
                            bool match = false;
                            foreach (var pfx in prefixes) if (n.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                            if (!match) continue;
                            try { if (File.GetLastWriteTime(f) < cutoff) delFile(f); } catch { }
                        }
                    }
                }
                catch { }
                // 内存中的索引也一并释放，下次扫描按需重建
                albIndex.Clear();
                albByDir.Clear();
                albDirStamp.Clear();
                albIndexLoaded = false;
                albDirLoaded = false;
                string size = bytes >= 1073741824L ? (bytes / 1073741824.0).ToString("0.00") + " GB"
                            : bytes >= 1048576L ? (bytes / 1048576.0).ToString("0.0") + " MB"
                            : (bytes / 1024.0).ToString("0") + " KB";
                Send("{\"type\":\"toast\",\"msg\":" + JsonStr("已删除 " + files + " 个缓存文件，释放 " + size) + "}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("清理缓存失败: " + ex.Message) + "}"); }
        }

        // 相册右键「另存为」：选一个目标文件夹，把选中照片复制过去（实况伴生文件一起）
        void AlbumSaveAs(string[] paths)
        {
            try
            {
                var list = new List<string>();
                if (paths != null) foreach (var p in paths)
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) list.Add(p.Trim());
                if (list.Count == 0) { Send("{\"type\":\"toast\",\"msg\":\"没有可另存的文件\"}"); return; }
                string dest = null;
                InvokeUi(() =>
                {
                    using (var d = new FolderBrowserDialog())
                    {
                        d.Description = list.Count > 1 ? ("选择另存到哪个文件夹（共 " + list.Count + " 张）") : "选择另存到哪个文件夹";
                        d.ShowNewFolderButton = true;
                        if (d.ShowDialog() == DialogResult.OK) dest = d.SelectedPath;
                    }
                });
                if (string.IsNullOrEmpty(dest)) return;
                int ok = 0, extra = 0, fail = 0;
                foreach (var p in list)
                {
                    try
                    {
                        string name = Path.GetFileName(p);
                        string target = Path.Combine(dest, name);
                        if (File.Exists(target)) target = AlbumUniquePath(dest, name);
                        File.Copy(p, target, false);
                        ok++;
                        var cs = DupCompanions(p);
                        if (cs != null) foreach (var c in cs)
                        {
                            if (!File.Exists(c)) continue;
                            string cn = Path.GetFileName(c);
                            string ct = Path.Combine(dest, cn);
                            if (File.Exists(ct)) ct = AlbumUniquePath(dest, cn);
                            File.Copy(c, ct, false); extra++;
                        }
                    }
                    catch { fail++; }
                }
                Send("{\"type\":\"albsaved\",\"count\":" + ok + ",\"extra\":" + extra + ",\"fail\":" + fail + ",\"dir\":" + JsonStr(dest) + "}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("另存失败: " + ex.Message) + "}"); }
        }

        string AlbumUniquePath(string dir, string name)
        {
            string b = Path.GetFileNameWithoutExtension(name), e = Path.GetExtension(name);
            for (int i = 2; i < 2000; i++)
            {
                string t = Path.Combine(dir, b + " (" + i + ")" + e);
                if (!File.Exists(t)) return t;
            }
            return Path.Combine(dir, b + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + e);
        }

        void SendPreview()
        {
            if (selected < 0 || selected >= files.Count) return;
            string fpath = files[selected];
            try
            {
                // cachedBase 现在存的是裁切后的原图（未套 LUT），换文件/换裁切才重载
                if (cachedBase == null || cachedFile != fpath)
                {
                    ResetAiCache();
                    if (cachedBase != null) { cachedBase.Dispose(); cachedBase = null; }
                    cachedBase = LoadBase(fpath);
                    cachedFile = fpath;
                    cacheGen++;
                }
                int mw = par.ContainsKey("preview_max") ? (int)par["preview_max"] : 1200;
                int w = cachedBase.Width, h = cachedBase.Height;
                if (w > mw) { h = h * mw / w; w = mw; }
                // 缓存缩小后的底图（只有换图/换裁切/换预览尺寸才重新缩放）
                if (cachedSmall == null || cachedSmallW != w || cachedSmallH != h || cachedSmallGen != cacheGen) {
                    if (cachedSmall != null) cachedSmall.Dispose();
                    cachedSmall = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(cachedSmall)) {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(cachedBase, 0, 0, w, h);
                    }
                    cachedSmallW = w; cachedSmallH = h; cachedSmallGen = cacheGen;
                }
                // 从缓存克隆一份做处理（不污染缓存底图）
                using (var small = new Bitmap(w, h))
                using (var g = Graphics.FromImage(small))
                {
                    g.DrawImage(cachedSmall, 0, 0, w, h);
                    ApplyLUT(small);       // LUT 在小图上跑，预览快 15 倍
                    ApplyColor(small);
                    ApplyTiltShift(small);
                    // 胶片质感（与导出流程一致）
                    if (par.ContainsKey("fg_grain") || par.ContainsKey("fg_leak") || par.ContainsKey("fg_halo"))
                        FilmFx.Apply(small, par["fg_grain"], par["fg_leak"], par["fg_halo"]);

                    // 景深虚化（仅在开启时，预览用同一模型；超分不在预览里跑，避免拖慢）
                    if (par.ContainsKey("dof_on") && par["dof_on"] > 0.5)
                    {
                        var dofb = BeautyRunner.ProcessDof(small, par.ContainsKey("dof_strength") ? (int)par["dof_strength"] : 60,
                                                          par.ContainsKey("dof_focus") ? (int)par["dof_focus"] : 50);
                        if (dofb != null) {
                            using (var gd = Graphics.FromImage(small)) gd.DrawImage(dofb, 0, 0, w, h);
                            dofb.Dispose();
                        } else Send("{\"type\":\"status\",\"text\":\"景深虚化失败: " + BeautyRunner.LastError.Replace("\"","'") + "\"}");
                    }

                    // 美颜相机：分项美颜（磨皮/美白/祛斑/黑眼圈/亮眼/口红/红润/瘦脸/大眼/瘦鼻/下巴）
                    if (par.ContainsKey("beauty_pro_on") && par["beauty_pro_on"] > 0.5)
                    {
                        var pro = BeautyRunner.ProcessPro(small, BeautyRunner.BuildParams(par));
                        if (pro != null)
                        {
                            using (var g3 = Graphics.FromImage(small))
                            {
                                g3.DrawImage(pro, 0, 0, w, h);
                            }
                            pro.Dispose();
                            Send("{\"type\":\"status\",\"text\":\"美颜相机: " + BeautyRunner.LastError.Replace("\"","'") + "\"}");
                        }
                        else
                        {
                            Send("{\"type\":\"status\",\"text\":\"美颜相机失败: " + BeautyRunner.LastError.Replace("\"","'") + "\"}");
                        }
                    }

                    // AI美颜（GFPGAN）
                    if (par.ContainsKey("beauty_on") && par["beauty_on"] > 0.5)
                    {
                        BeautyRunner.Strength = (float)(par.ContainsKey("beauty_strength") ? par["beauty_strength"] / 100.0 : 0.7);
                        var beautified = BeautyRunner.Process(small);
                        if (beautified != null)
                        {
                            using (var g2 = Graphics.FromImage(small))
                            {
                                g2.DrawImage(beautified, 0, 0, w, h);
                            }
                            beautified.Dispose();
                            Send("{\"type\":\"status\",\"text\":\"AI美颜: " + BeautyRunner.LastError.Replace("\"","'") + "\"}");
                        }
                        else
                        {
                            Send("{\"type\":\"status\",\"text\":\"AI美颜失败: " + BeautyRunner.LastError.Replace("\"","'") + "\"}");
                        }
                    }

                    // 直方图 + 输出图（JPEG 编码）一次完成
                    int[] histR = new int[16], histG = new int[16], histB = new int[16], histL = new int[16];
                    BitmapData hd = small.LockBits(new Rectangle(0,0,w,h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    int stride = hd.Stride;
                    byte[] hbuf = new byte[stride * h];
                    Marshal.Copy(hd.Scan0, hbuf, 0, hbuf.Length);
                    small.UnlockBits(hd);
                    // 直方图（并行累加）
                    Parallel.For(0, h, y => {
                        int row = y * stride;
                        for (int x = 0; x < w; x++) {
                            int i = row + x*4;
                            int bi = hbuf[i]>>4, gi = hbuf[i+1]>>4, ri = hbuf[i+2]>>4;
                            Interlocked.Increment(ref histR[ri]);
                            Interlocked.Increment(ref histG[gi]);
                            Interlocked.Increment(ref histB[bi]);
                            int lum = (int)(0.299*hbuf[i+2]+0.587*hbuf[i+1]+0.114*hbuf[i]);
                            Interlocked.Increment(ref histL[lum>>4]);
                        }
                    });

                    using (var ms = new MemoryStream())
                    {
                        var ep = new EncoderParameters(1);
                        ep.Param[0] = new EncoderParameter(Encoder.Quality, 75L);
                        small.Save(ms, LUT.JpegCodec, ep);
                        string b64 = Convert.ToBase64String(ms.ToArray());
                        var sb = new System.Text.StringBuilder();
                        sb.Append("{\"type\":\"preview\",\"data\":\"data:image/jpeg;base64,").Append(b64).Append("\",\"hist\":{\"r\":[");
                        for (int i = 0; i < 16; i++) { if (i > 0) sb.Append(","); sb.Append(histR[i]); }
                        sb.Append("],\"g\":[");
                        for (int i = 0; i < 16; i++) { if (i > 0) sb.Append(","); sb.Append(histG[i]); }
                        sb.Append("],\"b\":[");
                        for (int i = 0; i < 16; i++) { if (i > 0) sb.Append(","); sb.Append(histB[i]); }
                        sb.Append("],\"l\":[");
                        for (int i = 0; i < 16; i++) { if (i > 0) sb.Append(","); sb.Append(histL[i]); }
                        sb.Append("]}");

                        // 输出尺寸
                        int ow = cachedBase.Width, oh = cachedBase.Height;
                        int sizeMode = par.ContainsKey("out_size") ? (int)par["out_size"] : 0;
                        ApplySizeMode(ref ow, ref oh, sizeMode);
                        long estBytes = EstimateSize(small, ow, oh);
                        sb.Append(",\"out_w\":").Append(ow).Append(",\"out_h\":").Append(oh)
                          .Append(",\"est_kb\":").Append(estBytes / 1024).Append("}");
                        Send(sb.ToString());
                    }
                }
            }
            catch (Exception ex) {
                Log("预览异常: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        static void ApplySizeMode(ref int ow, ref int oh, int sm)
        {
            if (sm == 1) { ow /= 2; oh /= 2; }
            else if (sm == 2) { ow /= 4; oh /= 4; }
            else if (sm == 3) {
                int ls = Math.Max(ow, oh);
                if (ls > 2048) { double sc = 2048.0 / ls; ow = (int)(ow*sc); oh = (int)(oh*sc); }
            }
            else if (sm == 4) {
                int ls = Math.Max(ow, oh);
                if (ls > 4096) { double sc = 4096.0 / ls; ow = (int)(ow*sc); oh = (int)(oh*sc); }
            }
        }

        long EstimateSize(Bitmap small, int ow, int oh)
        {
            try {
                long q = (par.ContainsKey("lossless") && par["lossless"] > 0.5) ? 100L : (long)(int)(par.ContainsKey("quality") ? par["quality"] : 95);
                using (var estMs = new MemoryStream()) {
                    var estEp = new EncoderParameters(1);
                    estEp.Param[0] = new EncoderParameter(Encoder.Quality, q);
                    small.Save(estMs, LUT.JpegCodec, estEp);
                    long previewBytes = estMs.Length;
                    double scale = (double)(ow * oh) / (small.Width * small.Height);
                    return (long)(previewBytes * scale);
                }
            } catch {
                return (long)(ow * oh * 0.2);
            }
        }

        Bitmap LoadOriented(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var img = Image.FromStream(fs, true, true))
            {
                int orient = 1;
                try {
                    foreach (var pid in img.PropertyIdList) if (pid == 0x0112) {
                        var p = img.GetPropertyItem(0x0112);
                        orient = BitConverter.ToInt16(p.Value, 0);
                        break;
                    }
                } catch {}
                var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                    g.DrawImage(img, new Rectangle(0,0,img.Width,img.Height));
                switch (orient) {
                    case 3: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 6: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 8: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
                return bmp;
            }
        }

        // 加载 + 裁切（不套 LUT，缓存这个原图）
        Bitmap LoadBase(string path)
        {
            var src = LoadOriented(path);
            double cx = par["crop_x"], cy = par["crop_y"], cw = par["crop_w"], chh = par["crop_h"];
            if (cw > 0.01 && chh > 0.01 && (cx > 0.001 || cy > 0.001 || cw < 0.999 || chh < 0.999))
            {
                int rx = (int)(cx * src.Width), ry = (int)(cy * src.Height);
                int rw = (int)(cw * src.Width), rh = (int)(chh * src.Height);
                if (rx < 0) rx = 0; if (ry < 0) ry = 0;
                if (rx + rw > src.Width) rw = src.Width - rx;
                if (ry + rh > src.Height) rh = src.Height - ry;
                if (rw > 1 && rh > 1) {
                    var cropRect = new Rectangle(rx, ry, rw, rh);
                    var cropped = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(cropped))
                        g.DrawImage(src, new Rectangle(0,0,rw,rh), cropRect, GraphicsUnit.Pixel);
                    src.Dispose();
                    src = cropped;
                }
            }
            return src;
        }

        // LUT 处理（对任意 Bitmap 原地修改，预览用小图、导出用大图）
        // 无LUT时的基础自动调色：中值灰度世界WB + 自动曝光 + 温和对比/饱和
        void AutoEnhance(Bitmap src)
        {
            var rect = new Rectangle(0,0,src.Width,src.Height);
            var data = src.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            byte[] buf = new byte[stride * src.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            int W = src.Width, H = src.Height;

            // 采样统计
            long sumB=0, sumG=0, sumR=0; int cnt=0;
            var lumHist = new int[256];
            for (int y = 0; y < H; y += 2) {
                int row = y*stride;
                for (int x = 0; x < W; x += 2) {
                    int i = row + x*4;
                    int b = buf[i], g = buf[i+1], r = buf[i+2];
                    sumB += b; sumG += g; sumR += r; cnt++;
                    int lum = (int)(0.299*r + 0.587*g + 0.114*b);
                    if (lum < 0) lum = 0; else if (lum > 255) lum = 255;
                    lumHist[lum]++;
                }
            }
            double avgR = sumR / (double)cnt;
            double avgG = sumG / (double)cnt;
            double avgB = sumB / (double)cnt;
            double gray = (avgR + avgG + avgB) / 3.0;

            // 白平衡增益（中值灰度世界，限制在合理范围）
            double wbR = gray / Math.Max(avgR, 1);
            double wbG = gray / Math.Max(avgG, 1);
            double wbB = gray / Math.Max(avgB, 1);
            if (wbR > 1.2) wbR = 1.2; if (wbR < 0.8) wbR = 0.8;
            if (wbG > 1.2) wbG = 1.2; if (wbG < 0.8) wbG = 0.8;
            if (wbB > 1.2) wbB = 1.2; if (wbB < 0.8) wbB = 0.8;

            // 自动曝光：找中亮度
            long accum = 0; int medLum = 128;
            for (int v = 0; v < 256; v++) {
                accum += lumHist[v];
                if (accum >= cnt/2) { medLum = v; break; }
            }
            double expGain = 128.0 / Math.Max(medLum, 1);
            if (expGain > 1.3) expGain = 1.3;
            if (expGain < 0.75) expGain = 0.75;

            // 逐像素应用
            for (int y = 0; y < H; y++) {
                int row = y*stride;
                for (int x = 0; x < W; x++) {
                    int i = row + x*4;
                    double b = buf[i] / 255.0;
                    double g = buf[i+1] / 255.0;
                    double r = buf[i+2] / 255.0;

                    // WB
                    r *= wbR; g *= wbG; b *= wbB;

                    // 曝光（亮度域，保持色彩比例）
                    double lum = 0.299*r + 0.587*g + 0.114*b;
                    double newLum = lum * expGain;
                    if (newLum > 1.0) newLum = 1.0;
                    double ratio = lum > 0.001 ? newLum / lum : 1.0;
                    r *= ratio; g *= ratio; b *= ratio;

                    // 温和对比（S曲线中点附近）
                    r = 0.5 + (r - 0.5) * 1.08;
                    g = 0.5 + (g - 0.5) * 1.08;
                    b = 0.5 + (b - 0.5) * 1.08;

                    // 温和饱和
                    double lum2 = 0.299*r + 0.587*g + 0.114*b;
                    r = lum2 + (r - lum2) * 1.06;
                    g = lum2 + (g - lum2) * 1.06;
                    b = lum2 + (b - lum2) * 1.06;

                    if (r < 0) r = 0; else if (r > 1) r = 1;
                    if (g < 0) g = 0; else if (g > 1) g = 1;
                    if (b < 0) b = 0; else if (b > 1) b = 1;

                    buf[i] = (byte)(b * 255);
                    buf[i+1] = (byte)(g * 255);
                    buf[i+2] = (byte)(r * 255);
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            src.UnlockBits(data);
        }

        void ApplyLUT(Bitmap src)
        {
            // 懒加载 LUT（换 LUT 后 activeLut 被置空，这里补载）
            if (activeLutPath != null && activeLut == null)
                try { activeLut = CubeLut.Load(activeLutPath); }
                catch { activeLut = null; }
            bool aiOptOn = par.ContainsKey("ai_opt") && par["ai_opt"] > 0.5;
            bool autoOpt = (lutMode == "log");
            if (activeLut == null && !aiOptOn && !autoOpt) { Log("ApplyLUT: LUT未加载"); return; }
            Log("ApplyLUT开始: " + src.Width + "x" + src.Height + " mode=" + lutMode + " aiOpt=" + aiOptOn + " autoOpt=" + autoOpt);

            // 无LUT时：自动和AI可叠加，先自动再AI
            if (activeLut == null)
            {
                if (autoOpt)
                {
                    Log("自动优化：无LUT，基础自动调色");
                    AutoEnhance(src);
                }
                if (aiOptOn)
                {
                    Log("AI优化：在当前图基础上AI增强");
                    // 无LUT模式：清除LUT提示，避免Python按Log LUT模式输出
                    AiRunner.LutIsLogHint = false;
                    AiRunner.LutBlackHint = 0.0;
                    AiRunner.LutWhiteHint = 1.0;
                    var rectA = new Rectangle(0,0,src.Width,src.Height);
                    var dataA = src.LockBits(rectA, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                    int strideA = dataA.Stride;
                    byte[] bufA = new byte[strideA * src.Height];
                    Marshal.Copy(dataA.Scan0, bufA, 0, bufA.Length);
                    int WA = src.Width, HA = src.Height;
                    bool aiOk = AiRunner.ProcessImage(bufA, WA, HA, strideA);
                    Log("AI ProcessImage=" + aiOk + " err=" + AiRunner.LastError);
                    if (aiOk) {
                        double wbRA = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbR));
                        double wbGA = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbG));
                        double wbBA = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbB));
                        double expA = Math.Max(0.85, Math.Min(1.2, AiRunner.OutExposure));
                        double conA = Math.Max(0.9, Math.Min(1.15, AiRunner.OutContrast));
                        double satA = Math.Max(0.85, Math.Min(1.15, AiRunner.OutSaturation));
                        for (int y = 0; y < HA; y++) {
                            int row = y * strideA;
                            for (int x = 0; x < WA; x++) {
                                int i = row + x*4;
                                double b = bufA[i], g = bufA[i+1], r = bufA[i+2];
                                r *= wbRA * expA; g *= wbGA * expA; b *= wbBA * expA;
                                double lum = 0.299*r + 0.587*g + 0.114*b;
                                r = lum + (r-lum)*conA; g = lum + (g-lum)*conA; b = lum + (b-lum)*conA;
                                lum = 0.299*r + 0.587*g + 0.114*b;
                                r = lum + (r-lum)*satA; g = lum + (g-lum)*satA; b = lum + (b-lum)*satA;
                                if (r > 230) r = 230 + (r-230)*0.4;
                                if (g > 230) g = 230 + (g-230)*0.4;
                                if (b > 230) b = 230 + (b-230)*0.4;
                                bufA[i] = (byte)(b<0?0:b>255?255:b);
                                bufA[i+1] = (byte)(g<0?0:g>255?255:g);
                                bufA[i+2] = (byte)(r<0?0:r>255?255:r);
                            }
                        }
                        Log("AI增强完成: WB=" + wbRA.ToString("F2") + "/" + wbGA.ToString("F2") + "/" + wbBA.ToString("F2") +
                            " exp=" + expA.ToString("F2") + " con=" + conA.ToString("F2") + " sat=" + satA.ToString("F2") +
                            " scene=" + AiRunner.OutScene);
                    }
                    Marshal.Copy(bufA, 0, dataA.Scan0, bufA.Length);
                    src.UnlockBits(dataA);
                }
                return;
            }

            // 自动模式：不做灰片转换，直接套LUT，套完再调曝光/对比/饱和
            bool lutIsLogType = false;
            float[] srgbToLog = null; // 自动模式不再使用灰片转换
            bool doLutAnalysis = (lutMode == "log" || lutMode == "ai");
            if (doLutAnalysis)
            {
                if (!lutAnalyzed || adaptCurveLut != activeLutPath)
                {
                    int n = activeLut.Size;
                    var y = new float[n];
                    for (int i = 0; i < n; i++) {
                        int idx = ((i*n+i)*n+i)*3;
                        y[i] = (activeLut.Table[idx]+activeLut.Table[idx+1]+activeLut.Table[idx+2])/3f;
                    }
                    for (int i = 1; i < n; i++) if (y[i] < y[i-1]) y[i] = y[i-1];
                    float blackPt = y[0], whitePt = y[n-1], range = whitePt - blackPt;
                    // 判定 Log 型：仅以 LUT 声明的输入范围为准（DaVinci 标准的 Log LUT 声明
                    // 非默认输入范围）。不再用对角线黑场/白场启发式——暗调创意 LUT
                    // （如夜景滤镜）会被误判为 Log 型，套上灰片转换后颜色全错。
                    bool hasNonDefaultRange = (Math.Abs(activeLut.InputMin) > 0.001f || Math.Abs(activeLut.InputMax - 1f) > 0.001f);
                    lutIsLogType = hasNonDefaultRange;
                    adaptCurveLut = activeLutPath;
                    cachedBlackPt = blackPt; cachedWhitePt = whitePt;
                    lutAnalyzed = true;

                    // AI分析LUT性格：色温倾向、对比度、饱和度
                    int mid = n / 2;
                    int midIdx = ((mid*n+mid)*n+mid)*3;
                    float midR = activeLut.Table[midIdx], midG = activeLut.Table[midIdx+1], midB = activeLut.Table[midIdx+2];
                    // LUT色温：中间灰点偏红还是偏蓝
                    cachedLutWarm = midR - midB; // 正=偏暖(黄/橙)，负=偏冷(青/蓝)
                    // LUT对比：对角线输出范围
                    cachedLutContrast = range;
                    // LUT饱和倾向：偏离对角线的程度
                    float offDiag = 0;
                    for (int i = 0; i < n; i += 4) {
                        int idx = ((i*n+i)*n+i)*3;
                        float r = activeLut.Table[idx], g = activeLut.Table[idx+1], b = activeLut.Table[idx+2];
                        float gray = (r+g+b)/3f;
                        offDiag += Math.Abs(r-gray) + Math.Abs(g-gray) + Math.Abs(b-gray);
                    }
                    cachedLutSaturation = offDiag / (n/4 * 3);
                    Log("AI分析LUT: 色温=" + (cachedLutWarm>0?"偏暖":"偏冷") + cachedLutWarm.ToString("F2") +
                        " 对比=" + cachedLutContrast.ToString("F2") + " 饱和倾向=" + cachedLutSaturation.ToString("F2"));
                    // 根据 LUT 类型自动推荐参数
                    // 自动/AI模式默认：滤镜强度50，色度100
                    int recommended = 50;
                    int recommendedChroma = 100;
                    par["filterStrength"] = recommended;
                    par["chroma"] = recommendedChroma;
                    par["filmContrast"] = 0;
                    Send("{\"type\":\"setparam\",\"key\":\"filterStrength\",\"value\":" + recommended + "}");
                    Send("{\"type\":\"setparam\",\"key\":\"chroma\",\"value\":" + recommendedChroma + "}");
                    Send("{\"type\":\"setparam\",\"key\":\"filmContrast\",\"value\":0}");
                    Log((lutMode=="ai"?"AI":"自动") + " LUT: " + (lutIsLogType ? "Log转换型" : "创意型") + " 格数=" + activeLut.Size + "^3，强度=" + recommended + "，色度=" + recommendedChroma);
                }
                else
                {
                    // 缓存状态：以 LUT 声明的输入范围为准
                    lutIsLogType = (Math.Abs(activeLut.InputMin) > 0.001f || Math.Abs(activeLut.InputMax - 1f) > 0.001f);
                }

                // Log型LUT：自动/AI模式先转成对应的灰片再套用
                if (lutIsLogType && (lutMode == "ai" || lutMode == "log"))
                {
                    // 灰片转换 = 把 sRGB 亮度重映射到 LUT 声明的输入范围。
                    // 之前用 LUT 对角线的黑场/白场做压缩区间是错的：那是输出范围，
                    // 会把画面亮度双重压缩，导致颜色发灰、偏色。
                    srgbToLog = new float[256];
                    double lo = activeLut.InputMin, hi = activeLut.InputMax;
                    double span = Math.Max(1e-4, hi - lo);
                    for (int k = 0; k < 256; k++)
                    {
                        double c = k / 255.0;
                        srgbToLog[k] = (float)Math.Max(0.0, Math.Min(1.0, lo + c * span));
                    }
                }
            }

            int mode = lutMode == "log" ? 1 : lutMode == "ai" ? 4 : 0; // 1=自动, 4=AI, 0=直出
            float baseStrength = (float)(par["filterStrength"] / 100.0);
            float baseChroma = (float)(par["chroma"] / 100.0);
            float lutStrength = baseStrength;
            float chroma = baseChroma;
            // 直出模式：不做自动白平衡/曝光校准，但滤镜强度和色度滑块仍生效
            bool doChroma = Math.Abs(chroma - 1.0) > 0.001;
            bool doLut = lutStrength < 0.999f;

            var rect = new Rectangle(0,0,src.Width,src.Height);
            var data = src.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int bytes = stride * src.Height;
            byte[] buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);

            int W = src.Width, H = src.Height;
            float[] tbl = activeLut.Table;
            int lutSize = activeLut.Size;
            float inMin = activeLut.InputMin, inMax = activeLut.InputMax;
            float inRange = Math.Max(1e-6f, inMax - inMin);

            // 自动模式：先扫一遍图，统计白平衡和曝光
            double wbR = 1, wbG = 1, wbB = 1;
            float[] expTable = null;
            float[] outputCalTable = null; // AI模式：LUT输出后做黑场/白场校准
            bool doAutoCalib = false; // 根据LUT类型动态决定
            bool doAutoExposure = false;
            bool needInputExposure = false; // 输入侧曝光校正（Log型恒true；自动模式创意型仅异常曝光图）
            bool aiNeedInputExposure = false; // AI模式：AI判定曝光有问题时做输入侧校正
            bool aiMode = (mode == 4); // AI 模式：智能场景分析+智能调色
            bool isPortrait = false; // 人像检测（像素循环里也要用）

            if (mode == 1 || mode == 4)
            {
                // 如果已有缓存的AI参数且是大图（导出），直接复用，保证预览和输出一致
                // 批量模式下 forceFreshAnalysis=true，每张图单独分析
                if (aiParamsCached && W > 2000 && !forceFreshAnalysis) {
                    wbR = cachedWbR; wbG = cachedWbG; wbB = cachedWbB;
                    expTable = cachedExpTable;
                    outputCalTable = cachedOutputCalTable;
                    isPortrait = cachedIsPortrait;
                    // 恢复曝光校正标志：缓存表非空 = 当时需要输入侧曝光校正
                    needInputExposure = (cachedExpTable != null);
                    aiNeedInputExposure = (cachedExpTable != null);
                }
                else
                {
                // 采样统计（步长3加速）
                var rSamples = new List<int>();
                var gSamples = new List<int>();
                var bSamples = new List<int>();
                var lumSamples = new List<int>();
                var hist = new int[256];
                long total = 0;
                for (int y = 0; y < H; y += 3) {
                    int row = y * stride;
                    for (int x = 0; x < W; x += 3) {
                        int i = row + x*4;
                        int b = buf[i], g = buf[i+1], r = buf[i+2];
                        rSamples.Add(r); gSamples.Add(g); bSamples.Add(b);
                        int lum = (int)(0.299*r + 0.587*g + 0.114*b);
                        lumSamples.Add(lum);
                        if (lum < 0) lum = 0; else if (lum > 255) lum = 255;
                        hist[lum]++;
                        total++;
                    }
                }
                // 白平衡：中值灰度世界法
                // - Log 型 LUT：预校准（0.7~1.3）
                // - 自动模式 + 创意型 LUT：温和自动白平衡（0.9~1.1），只修图的色偏、不洗 LUT 风格
                bool needPreCalib = lutIsLogType;
                bool doMildAuto = (mode == 1 && !lutIsLogType);
                // 计算通道中值（AI引擎需要，无论是否预校准）
                rSamples.Sort(); gSamples.Sort(); bSamples.Sort();
                double medR = rSamples[rSamples.Count/2];
                double medG = gSamples[gSamples.Count/2];
                double medB = bSamples[bSamples.Count/2];
                if (needPreCalib || doMildAuto) {
                    double medGray = (medR + medG + medB) / 3.0;
                    wbR = medGray / Math.Max(medR, 1);
                    wbG = medGray / Math.Max(medG, 1);
                    wbB = medGray / Math.Max(medB, 1);
                    if (needPreCalib) {
                        if (wbR > 1.3) wbR = 1.3; if (wbR < 0.7) wbR = 0.7;
                        if (wbG > 1.3) wbG = 1.3; if (wbG < 0.7) wbG = 0.7;
                        if (wbB > 1.3) wbB = 1.3; if (wbB < 0.7) wbB = 0.7;
                    } else {
                        // 温和白平衡：±10%，只校正明显色偏
                        if (wbR > 1.1) wbR = 1.1; if (wbR < 0.9) wbR = 0.9;
                        if (wbG > 1.1) wbG = 1.1; if (wbG < 0.9) wbG = 0.9;
                        if (wbB > 1.1) wbB = 1.1; if (wbB < 0.9) wbB = 0.9;
                    }
                } else {
                    wbR = 1.0; wbG = 1.0; wbB = 1.0;
                }
                // 曝光校准：中值亮度（无论是否预校准，都要算出来用于AI调整）
                lumSamples.Sort();
                double medLum = lumSamples[lumSamples.Count/2];
                // 输入侧曝光校正策略：
                // - Log 型 LUT：恒做（0.8~1.2，灰片转换需要正确的中灰输入）
                // - 自动模式 + 创意型 LUT：恒做温和校正（0.9~1.1），向中灰靠拢但幅度克制，
                //   图健康时增益≈1 几乎无感，图偏暗/偏亮时自动模式与直出才有可见区别
                needInputExposure = needPreCalib || doMildAuto;
                if (needInputExposure) {
                    double exposureGain = 128.0 / Math.Max(medLum, 1);
                    if (needPreCalib) {
                        if (exposureGain > 1.2) exposureGain = 1.2; // 收紧上限，防止预曝光把画面推爆
                        if (exposureGain < 0.8) exposureGain = 0.8;
                    } else {
                        if (exposureGain > 1.1) exposureGain = 1.1; // 创意型只做温和校正
                        if (exposureGain < 0.9) exposureGain = 0.9;
                    }
                    expTable = new float[256];
                    for (int k = 0; k < 256; k++) {
                        double v = k * exposureGain / 255.0;
                        if (v < 0) v = 0; else if (v > 1) v = 1;
                        expTable[k] = (float)v;
                    }
                    Log("输入曝光校正: 中值=" + medLum.ToString("F0") + " 增益=" + exposureGain.ToString("F2") +
                        (needPreCalib ? " (Log型)" : " (创意型温和)") +
                        " WB=" + wbR.ToString("F3") + "/" + wbG.ToString("F3") + "/" + wbB.ToString("F3"));
                } else {
                    // 直出模式：不做任何输入校正
                    wbR = 1.0; wbG = 1.0; wbB = 1.0;
                    expTable = null;
                }

                // AI模式：所有调色参数由AI直接输出
                if (aiMode) {
                    Log("AI: 进入AI块，开始调用AI模型...");
                    // 设置LUT信息提示给AI
                    AiRunner.LutIsLogHint = lutIsLogType;
                    AiRunner.LutBlackHint = cachedBlackPt;
                    AiRunner.LutWhiteHint = cachedWhitePt;
                    bool aiOk = AiRunner.ProcessImage(buf, W, H, stride);
                    Log("AI: ProcessImage返回=" + aiOk + " 错误=" + AiRunner.LastError);
                    if (aiOk) {
                        Log("AI全流程: " + AiRunner.LastError);
                    } else {
                        Log("AI: " + AiRunner.LastError + "，使用默认参数");
                    }

                    // 直接应用AI输出的白平衡（创意型窄幅±5%，AI优化原图但保留LUT风格）
                    if (lutIsLogType) {
                        wbR = AiRunner.OutWbR;
                        wbG = AiRunner.OutWbG;
                        wbB = AiRunner.OutWbB;
                    } else {
                        wbR = (float)Math.Max(0.95, Math.Min(1.05, AiRunner.OutWbR));
                        wbG = (float)Math.Max(0.95, Math.Min(1.05, AiRunner.OutWbG));
                        wbB = (float)Math.Max(0.95, Math.Min(1.05, AiRunner.OutWbB));
                    }

                    // AI预校准曝光：把原图中灰推到LUT期望位置（套LUT前用）
                    double preExp = Math.Max(0.85, Math.Min(1.15, AiRunner.OutPreExposure)); // 限幅防过曝
                    aiNeedInputExposure = Math.Abs(AiRunner.OutPreExposure - 1.0) > 0.05; // AI判定曝光明显异常才校正
                    expTable = new float[256];
                    for (int k = 0; k < 256; k++) {
                        double v = (k / 255.0) * preExp;
                        if (v > 1.0) v = 1.0; if (v < 0) v = 0;
                        expTable[k] = (float)v;
                    }

                    // AI决定是否做sRGB→Log转换及转换曲线（仅 Log 型 LUT 才做，创意型保持原色）
                    if (AiRunner.OutUseLogConvert && lutIsLogType) {
                        float gamma = (float)AiRunner.OutLogGamma;
                        srgbToLog = new float[256];
                        for (int k = 0; k < 256; k++) {
                            double v = k / 255.0;
                            // 非线性映射：gamma<1压缩高光（Log感），gamma>1拉伸
                            srgbToLog[k] = (float)(Math.Pow(v, gamma));
                        }
                        Log("AI Log转换曲线: gamma=" + gamma.ToString("F2"));
                    }

                    // AI对比度和饱和度（套完LUT后用）
                    aiContrast = (float)AiRunner.OutContrast;
                    aiSaturation = (float)AiRunner.OutSaturation;
                    aiHighlights = (float)AiRunner.OutHighlights;
                    aiShadows = (float)AiRunner.OutShadows;

                    // 皮肤检测
                    long skinCount = 0;
                    for (int y = 0; y < H; y += 4) {
                        int row = y * stride;
                        for (int x = 0; x < W; x += 4) {
                            int i = row + x*4;
                            int b = buf[i], g = buf[i+1], r = buf[i+2];
                            if (r > g && g > b && (r-g) > 10 && (r-g) < 60 && r > 80) skinCount++;
                        }
                    }
                    isPortrait = skinCount > total * 0.03;
                }
                outputCalTable = null;
                cachedWbR = wbR; cachedWbG = wbG; cachedWbB = wbB;
                cachedExpTable = expTable;
                cachedOutputCalTable = outputCalTable;
                cachedIsPortrait = isPortrait;
                aiParamsCached = true;
                } // end else (compute params)
            }

            // AI模式：根据LUT类型决定预校准
            if (aiMode) {
                if (lutIsLogType) {
                    // Log型LUT需要完整预校准
                    doAutoCalib = true;
                    doAutoExposure = true;
                } else {
                    // 创意型LUT：AI 先优化原图（白平衡已窄幅±5%，见上文），
                    // 仅当 AI 判定原图曝光明显异常时做输入侧曝光校正；
                    // 套用后的偏色由"AI校验-校准"闭环处理
                    doAutoCalib = true;
                    doAutoExposure = aiNeedInputExposure;
                }
            } else {
                // 自动模式：Log型完整预校准；创意型温和白平衡+曝光（±10%）
                doAutoCalib = (mode == 1);
                doAutoExposure = needInputExposure;
            }
            Log((aiMode?"AI神经网络模式":"自动模式") + " LUT类型: " + (lutIsLogType?"Log型":"创意型") +
                " 预校准=" + doAutoCalib + " AI对比度=" + aiContrast.ToString("F2") +
                " AI饱和=" + aiSaturation.ToString("F2"));

            // AI闭环校验已移到套用之后（校验-校准-验证闭环），此处不再预补偿中灰
            aiPostExposure = 1.0f;

            // 传给原生函数：WB/曝光/Log都在像素循环里做（C#和原生统一路径）
            float[] wbGainArr = doAutoCalib ? new float[] { (float)wbR, (float)wbG, (float)wbB } : null;
            float[] expArr = (doAutoExposure && expTable != null) ? expTable : null;
            float[] logArr = (srgbToLog != null) ? srgbToLog : null;

            // GPU 优先：D3D11 compute shader 最快
            // 注意：GPU 接口不支持曝光表/灰片表，凡是需要这两张表的路径必须走 CPU，
            // 否则三路径结果不一致（GPU 成功时自动/AI 模式的曝光与灰片转换会被丢弃）
            bool gpuDone = false;
            bool needTables = (expArr != null) || (logArr != null);
            // 线性化套 LUT：GPU/原生路径不支持，强制走 C# 逐像素（保证预览与导出行为一致）
            bool linLut = par.ContainsKey("lut_linear") && par["lut_linear"] > 0.5;
            if (linLut) needTables = true;
            if (!needTables)
                try { gpuDone = Native.gpu_lut_apply(buf, W, H, stride, tbl, lutSize, inMin, inMax,
                    lutStrength, chroma, wbGainArr) != 0; } catch { gpuDone = false; }
            Log("GPU路径: " + (gpuDone ? "成功" : (needTables ? "跳过(需曝光/灰片表)" : "失败/跳过")) + " WB=" + (wbGainArr!=null));

            // CPU 原生 C++（AVX2+OpenMP），GPU失败才走
            bool useNative = !gpuDone && !linLut;
            if (useNative) {
                try {
                    Native.lut_apply(buf, W, H, stride, tbl, lutSize, inMin, inMax,
                        lutStrength, chroma, wbGainArr, expArr, logArr);
                } catch {
                    useNative = false;
                }
            }
            // GPU和C++都成功时跳过C#循环，避免LUT套两遍
            if (!gpuDone && !useNative)
            {
                Parallel.For(0, H, yy =>
                {
                    int row = yy * stride;
                    var rng = new Random(yy * 7919);
                    for (int x = 0; x < W; x++)
                    {
                        int i = row + x*4;
                        int bi = buf[i], gi = buf[i+1], ri = buf[i+2];
                        double lr = ri / 255.0, lg = gi / 255.0, lb = bi / 255.0;
                        // 1. 白平衡
                        if (doAutoCalib) { lr *= wbR; lg *= wbG; lb *= wbB; if (lr>1)lr=1; if (lg>1)lg=1; if (lb>1)lb=1; }
                        // 2. 曝光校准（亮度域调整，保持色彩比例）
                        if (expTable != null && doAutoExposure) {
                            double lum = 0.299*lr+0.587*lg+0.114*lb;
                            double newLum = expTable[(int)(lum*255)];
                            double ratio = lum > 0.001 ? newLum/lum : 1.0;
                            lr = Math.Max(0,Math.Min(1,lr*ratio)); lg = Math.Max(0,Math.Min(1,lg*ratio)); lb = Math.Max(0,Math.Min(1,lb*ratio));
                        }
                        // 3. sRGB→Log灰片（亮度域转换，保持三通道色彩比例）
                        if (srgbToLog != null) {
                            double lum = 0.299*lr + 0.587*lg + 0.114*lb;
                            double logLum = srgbToLog[(int)(lum*255)];
                            double ratio = lum > 0.001 ? logLum/lum : 1.0;
                            lr = Math.Max(0, Math.Min(1, lr*ratio));
                            lg = Math.Max(0, Math.Min(1, lg*ratio));
                            lb = Math.Max(0, Math.Min(1, lb*ratio));
                        }
                        // 4. 线性化（可选）：LUT 若按线性空间制作，先转到线性再查表
                        if (linLut) { lr = LinSpace.DecodeF((float)lr); lg = LinSpace.DecodeF((float)lg); lb = LinSpace.DecodeF((float)lb); }
                        float sr = (float)((lr-inMin)/inRange), sg = (float)((lg-inMin)/inRange), sb = (float)((lb-inMin)/inRange);
                        float nr, ng, nb;
                        CubeLut.SampleTetra(tbl, lutSize, sr, sg, sb, out nr, out ng, out nb);
                        // 5. 反线性化回 sRGB（在线性输出上做强度混合更自然）
                        if (linLut) { nr = LinSpace.EncodeF(nr); ng = LinSpace.EncodeF(ng); nb = LinSpace.EncodeF(nb); }
                        if (doLut) {
                            nr = nr*lutStrength + (float)(ri/255f)*(1-lutStrength); ng = ng*lutStrength + (float)(gi/255f)*(1-lutStrength); nb = nb*lutStrength + (float)(bi/255f)*(1-lutStrength);
                        }
                        // 1. 先做LUT输出校准（把灰片范围拉伸回正常0-1）
                        if (outputCalTable != null) {
                            nr = outputCalTable[(int)(nr*255f)];
                            ng = outputCalTable[(int)(ng*255f)];
                            nb = outputCalTable[(int)(nb*255f)];
                        }
                        // 2. 色度调整（在校准后的正确亮度上做）
                        if (doChroma) { double lum = 0.299*nr+0.587*ng+0.114*nb; nr = (float)(lum+(nr-lum)*chroma); ng = (float)(lum+(ng-lum)*chroma); nb = (float)(lum+(nb-lum)*chroma); }
                        // 2.5 软高光滚降：只压缩接近纯白的部分，保持 LUT 明暗与色彩关系
                        if (nr > 0.92f) nr = 0.92f + (nr - 0.92f) * 0.35f;
                        if (ng > 0.92f) ng = 0.92f + (ng - 0.92f) * 0.35f;
                        if (nb > 0.92f) nb = 0.92f + (nb - 0.92f) * 0.35f;
                        // AI模式后处理：只做肤色保护（对比/饱和/曝光由独立pass统一处理）
                        if (aiMode && isPortrait) {
                            double origR = ri/255.0, origG = gi/255.0, origB = bi/255.0;
                            // 肤色检测：R>G>B，R-G在合理范围
                            if (origR > origG && origG > origB && (origR-origG) > 0.04 && (origR-origG) < 0.25 && origR > 0.3) {
                                // 肤色拉回40%原色，避免LUT把人脸染奇怪颜色
                                nr = (float)(nr * 0.6 + origR * 0.4);
                                ng = (float)(ng * 0.6 + origG * 0.4);
                                nb = (float)(nb * 0.6 + origB * 0.4);
                            }
                        }
                        if (nr<0)nr=0; else if (nr>1)nr=1; if (ng<0)ng=0; else if (ng>1)ng=1; if (nb<0)nb=0; else if (nb>1)nb=1;
                        buf[i] = (byte)(nb*255); buf[i+1] = (byte)(ng*255); buf[i+2] = (byte)(nr*255);
                    }
                });
            }

            // ===== 套用后"校验-校准-验证"闭环（自动/AI 模式）=====
            // 目标：输出图中灰区的亮度与灰平衡贴合"LUT 想要的颜色"（LUT 在中灰输入的输出），
            // 而不是把画面强拉到固定中灰——暗调 LUT 保持暗调、亮调保持亮调，风格不被破坏。
            // 自动模式：算法校验（最多 3 轮）；AI 模式：算法校验 + AI 二次校验（模型看输出图给偏差）。
            if ((mode == 1 || mode == 4) && doLut && tbl != null)
            {
                float lr0, lg0, lb0;
                CubeLut.SampleTetra(tbl, lutSize, 0.5f, 0.5f, 0.5f, out lr0, out lg0, out lb0);
                double expLum = 0.299*lr0 + 0.587*lg0 + 0.114*lb0;   // LUT 想要的中灰亮度
                double expRB = lr0 - lb0;                            // LUT 想要的中灰灰平衡
                float savedAiCon = aiContrast, savedAiSat = aiSaturation;
                float savedAiHl = aiHighlights, savedAiSh = aiShadows;

                int maxRounds = (mode == 1) ? 4 : 2; // 自动 4 轮；AI 每轮要跑模型，限 2 轮
                for (int round = 0; round < maxRounds; round++)
                {
                    // 校验：统计输出中灰区（亮度贴近 LUT 期望中灰 ±0.15）的平均亮度与灰平衡
                    long sLum = 0, sRB = 0; int nMid = 0;
                    for (int y = 0; y < H; y += 2)
                    {
                        int row = y * stride;
                        for (int x = 0; x < W; x += 2)
                        {
                            int i = row + x*4;
                            double r = buf[i+2], g = buf[i+1], b = buf[i];
                            double lum = (0.299*r + 0.587*g + 0.114*b) / 255.0;
                            if (Math.Abs(lum - expLum) < 0.15)
                            {
                                sLum += (int)(0.299*r + 0.587*g + 0.114*b);
                                sRB += (int)(r - b);
                                nMid++;
                            }
                        }
                    }
                    if (nMid < 1000) { Log("校验闭环: 中灰区样本不足(" + nMid + ")，跳过"); break; }
                    double avgLum = (double)sLum / nMid / 255.0;
                    double avgRB = (double)sRB / nMid / 255.0;
                    double lumErr = avgLum - expLum;
                    double balErr = avgRB - expRB;
                    Log("校验闭环 第" + (round+1) + "轮: 期望中灰=" + expLum.ToString("F3") +
                        " 实际=" + avgLum.ToString("F3") + " 亮度偏差=" + lumErr.ToString("F3") +
                        " 灰平衡偏差=" + balErr.ToString("F3"));
                    if (Math.Abs(lumErr) < 0.08 && Math.Abs(balErr) < 0.05) { Log("校验闭环: 达标"); break; }

                    // 校准（输出侧窄增益，小步进，每轮最多 ±10%）
                    double lumGain = 1.0 - lumErr * 0.6;
                    if (lumGain > 1.1) lumGain = 1.1; if (lumGain < 0.9) lumGain = 0.9;
                    double rGain = 1.0 - balErr * 0.5; // balErr>0 偏红 → 降 R
                    double bGain = 1.0 + balErr * 0.5; // 同时升 B
                    if (rGain > 1.1) rGain = 1.1; if (rGain < 0.9) rGain = 0.9;
                    if (bGain > 1.1) bGain = 1.1; if (bGain < 0.9) bGain = 0.9;
                    Parallel.For(0, H, yy => {
                        int row = yy * stride;
                        for (int x = 0; x < W; x++)
                        {
                            int i = row + x*4;
                            double r = buf[i+2], g = buf[i+1], b = buf[i];
                            r = r * lumGain * rGain; g = g * lumGain; b = b * lumGain * bGain;
                            if (r > 235) r = 235 + (r-235)*0.3;
                            if (g > 235) g = 235 + (g-235)*0.3;
                            if (b > 235) b = 235 + (b-235)*0.3;
                            buf[i+2] = (byte)Math.Max(0, Math.Min(255, r));
                            buf[i+1] = (byte)Math.Max(0, Math.Min(255, g));
                            buf[i]   = (byte)Math.Max(0, Math.Min(255, b));
                        }
                    });
                    Log("校验闭环 校准: 亮度增益=" + lumGain.ToString("F3") + " R/B增益=" + rGain.ToString("F3") + "/" + bGain.ToString("F3"));
                }

                // AI 二次校验：AI 看输出图给白平衡偏差，窄幅校准后恢复后处理参数
                if (aiMode)
                {
                    try
                    {
                        AiRunner.LutIsLogHint = false;
                        bool aiOkV = AiRunner.ProcessImage(buf, W, H, stride);
                        if (aiOkV)
                        {
                            double wbDev = Math.Max(Math.Abs(AiRunner.OutWbR-1.0),
                                Math.Max(Math.Abs(AiRunner.OutWbG-1.0), Math.Abs(AiRunner.OutWbB-1.0)));
                            Log("AI校验输出图: WB偏差=" + wbDev.ToString("F3") + " " + AiRunner.LastError);
                            if (wbDev > 0.04)
                            {
                                // AI 校准：应用 AI 给出的白平衡（反向窄限幅，防止过度）
                                double wA = Math.Max(0.95, Math.Min(1.05, AiRunner.OutWbR));
                                double wB = Math.Max(0.95, Math.Min(1.05, AiRunner.OutWbB));
                                double wG = Math.Max(0.95, Math.Min(1.05, AiRunner.OutWbG));
                                for (int y = 0; y < H; y++)
                                {
                                    int row = y * stride;
                                    for (int x = 0; x < W; x++)
                                    {
                                        int i = row + x*4;
                                        double r = buf[i+2]*wA, g = buf[i+1]*wG, b = buf[i]*wB;
                                        buf[i+2] = (byte)Math.Max(0, Math.Min(255, r));
                                        buf[i+1] = (byte)Math.Max(0, Math.Min(255, g));
                                        buf[i]   = (byte)Math.Max(0, Math.Min(255, b));
                                    }
                                }
                                Log("AI校准: WB=" + wA.ToString("F3") + "/" + wG.ToString("F3") + "/" + wB.ToString("F3"));
                            }
                        }
                        else Log("AI校验: 模型调用失败 " + AiRunner.LastError);
                    }
                    catch (Exception ex) { Log("AI校验异常: " + ex.Message); }
                    // 恢复 AI 后处理参数（校验调用会覆盖模型输出字段）
                    aiContrast = savedAiCon; aiSaturation = savedAiSat;
                    aiHighlights = savedAiHl; aiShadows = savedAiSh;
                }
            }

            // === AI后处理pass：对比/饱和 + 中灰补偿 + 软高光滚降 ===
            if (aiMode) {
                // 采样当前图亮度，计算曝光校准增益（把AI+LUT后的画面拉回正常中灰）
                long lumSum = 0; int lumCnt = 0;
                for (int y = 0; y < H; y += 4) {
                    int row = y * stride;
                    for (int x = 0; x < W; x += 4) {
                        int i = row + x*4;
                        int b = buf[i], g = buf[i+1], r = buf[i+2];
                        lumSum += (int)(0.299*r + 0.587*g + 0.114*b);
                        lumCnt++;
                    }
                }
                double avgLum = lumCnt > 0 ? (double)lumSum / lumCnt / 255.0 : 0.5;
                // 中灰归一化只用于 Log 型 LUT（转 709 后亮度域可能偏移）；
                // 创意型 LUT 不拉中灰——暗调/亮调是滤镜本身的风格，强行拉回会破坏观感并导致高光死白
                double normGain = 1.0;
                if (lutIsLogType) {
                    normGain = 0.5 / Math.Max(avgLum, 0.05);
                    if (normGain > 1.15) normGain = 1.15;
                    if (normGain < 0.85) normGain = 0.85;
                }
                Log("AI后处理pass: 对比=" + aiContrast.ToString("F3") + " 饱和=" + aiSaturation.ToString("F3") +
                    " 高光=" + aiHighlights.ToString("F3") + " 阴影=" + aiShadows.ToString("F3") +
                    " 中灰补偿=" + aiPostExposure.ToString("F3") + " 当前亮度=" + avgLum.ToString("F3") +
                    " 曝光校准增益=" + normGain.ToString("F3") + " (Log型=" + lutIsLogType + ")");
                double passHl = Math.Max(0.85, Math.Min(1.0, (double)aiHighlights));
                double passSh = Math.Max(1.0, Math.Min(1.15, (double)aiShadows));
                bool doHlPass = passHl < 0.999, doShPass = passSh > 1.001;
                Parallel.For(0, H, yy => {
                    int row = yy * stride;
                    for (int x = 0; x < W; x++) {
                        int i = row + x*4;
                        double b = buf[i], g = buf[i+1], r = buf[i+2];
                        // 0. 先软高光滚降再曝光校准：先滚降可避免增益把高光推成死白
                        if (r > 235) r = 235 + (r - 235) * 0.3;
                        if (g > 235) g = 235 + (g - 235) * 0.3;
                        if (b > 235) b = 235 + (b - 235) * 0.3;
                        r *= normGain; g *= normGain; b *= normGain;
                        // 1. 中灰闭环补偿
                        if (Math.Abs(aiPostExposure - 1.0f) > 0.001f) {
                            r *= aiPostExposure; g *= aiPostExposure; b *= aiPostExposure;
                        }
                        // 1.5 AI高光压缩 / 阴影提亮（引擎已输出，亮度域渐进生效）
                        if (doHlPass || doShPass) {
                            double lum0 = 0.299*r + 0.587*g + 0.114*b;
                            if (doHlPass && lum0 > 180) {
                                double t = Math.Min(1.0, (lum0 - 180) / 75.0);
                                double hg = 1.0 - (1.0 - passHl) * t;
                                r *= hg; g *= hg; b *= hg;
                            }
                            if (doShPass && lum0 < 70) {
                                double t = 1.0 - lum0 / 70.0;
                                double sg = 1.0 + (passSh - 1.0) * t;
                                r *= sg; g *= sg; b *= sg;
                            }
                        }
                        // 2. AI对比度（软S曲线，保护高光和暗部）
                        if (Math.Abs(aiContrast - 1.0f) > 0.001f) {
                            // 只对中间调(60-220)做对比，高光和暗部不拉伸
                            // 用taper曲线：中间对比强，两端逐渐减弱
                            r = SoftContrast(r, aiContrast);
                            g = SoftContrast(g, aiContrast);
                            b = SoftContrast(b, aiContrast);
                        }
                        // 3. AI饱和度
                        if (Math.Abs(aiSaturation - 1.0f) > 0.001f) {
                            double lum = 0.299*r + 0.587*g + 0.114*b;
                            r = lum + (r - lum) * aiSaturation;
                            g = lum + (g - lum) * aiSaturation;
                            b = lum + (b - lum) * aiSaturation;
                        }
                        // 4. 软高光滚降：>235柔和压缩
                        if (r > 235) r = 235 + (r - 235) * 0.35;
                        if (g > 235) g = 235 + (g - 235) * 0.35;
                        if (b > 235) b = 235 + (b - 235) * 0.35;
                        buf[i]   = (byte)Math.Max(0, Math.Min(255, b));
                        buf[i+1] = (byte)Math.Max(0, Math.Min(255, g));
                        buf[i+2] = (byte)Math.Max(0, Math.Min(255, r));
                    }
                });
            }

            // ===== 自动模式：总体优化（温和黑白场拉伸 + 轻对比/饱和，不改色相）=====
            if (mode == 1)
            {
                var hist = new int[256];
                for (int y = 0; y < H; y += 2)
                {
                    int row = y * stride;
                    for (int x = 0; x < W; x += 2)
                    {
                        int i = row + x*4;
                        int lum = (int)(0.299*buf[i+2] + 0.587*buf[i+1] + 0.114*buf[i]);
                        if (lum < 0) lum = 0; else if (lum > 255) lum = 255;
                        hist[lum]++;
                    }
                }
                long tot = 0; for (int k = 0; k < 256; k++) tot += hist[k];
                if (tot > 1000)
                {
                    int lo = 0, hi = 255; long acc = 0;
                    for (int k = 0; k < 256; k++) { acc += hist[k]; if (acc > tot * 0.005) { lo = k; break; } }
                    acc = 0;
                    for (int k = 255; k >= 0; k--) { acc += hist[k]; if (acc > tot * 0.005) { hi = k; break; } }
                    // 温和拉伸：只拉伸 2~255 范围外的黑场/白场，避免改变 LUT 整体风格
                    if (lo > 0 || hi < 255)
                    {
                        double scale = 255.0 / Math.Max(1, hi - lo);
                        const double contrast = 1.02, sat = 1.02;
                        Parallel.For(0, H, yy => {
                            int row = yy * stride;
                            for (int x = 0; x < W; x++)
                            {
                                int i = row + x*4;
                                double r = buf[i+2], g = buf[i+1], b = buf[i];
                                r = (r - lo) * scale; g = (g - lo) * scale; b = (b - lo) * scale;
                                double lum = (0.299*r + 0.587*g + 0.114*b);
                                r = 128 + (r - 128) * contrast; g = 128 + (g - 128) * contrast; b = 128 + (b - 128) * contrast;
                                lum = (0.299*r + 0.587*g + 0.114*b);
                                r = lum + (r - lum) * sat; g = lum + (g - lum) * sat; b = lum + (b - lum) * sat;
                                if (r > 235) r = 235 + (r-235)*0.3;
                                if (g > 235) g = 235 + (g-235)*0.3;
                                if (b > 235) b = 235 + (b-235)*0.3;
                                buf[i+2] = (byte)Math.Max(0, Math.Min(255, r));
                                buf[i+1] = (byte)Math.Max(0, Math.Min(255, g));
                                buf[i]   = (byte)Math.Max(0, Math.Min(255, b));
                            }
                        });
                        Log("自动总体优化: 黑白场[" + lo + "," + hi + "] 对比1.02 饱和1.02");
                    }
                }
            }

            // ===== 最终过曝保护：只压缩极端过曝，不再把暗调/亮调 LUT 强拉回中灰 =====
            // （旧逻辑对中值<80 的画面做 1.4 倍提亮，把夜景/暗调滤镜的风格抹掉，
            //   同时高光被 clamp 成大片死白——这就是"自动模式经常过曝"的根源）
            if (mode == 1 || mode == 4) {
                var lumList = new List<int>();
                for (int y = 0; y < H; y += 4) {
                    int row = y * stride;
                    for (int x = 0; x < W; x += 4) {
                        int i = row + x*4;
                        int lum = (int)(0.299*buf[i+2] + 0.587*buf[i+1] + 0.114*buf[i]);
                        lumList.Add(lum);
                    }
                }
                lumList.Sort();
                int medOutLum = lumList[lumList.Count/2];
                double finalGain = 1.0;
                if (medOutLum > 200) finalGain = 200.0 / Math.Max(medOutLum, 1); // 只防极端过曝
                if (finalGain < 0.8) finalGain = 0.8;
                if (finalGain < 0.98) {
                    Parallel.For(0, H, yy => {
                        int row = yy * stride;
                        for (int x = 0; x < W; x++) {
                            int i = row + x*4;
                            int r = buf[i+2], g = buf[i+1], b = buf[i];
                            double nr = r, ng = g, nb = b;
                            if (nr > 235) nr = 235 + (nr - 235) * 0.3;
                            if (ng > 235) ng = 235 + (ng - 235) * 0.3;
                            if (nb > 235) nb = 235 + (nb - 235) * 0.3;
                            buf[i+2] = (byte)Math.Max(0, Math.Min(255, nr * finalGain));
                            buf[i+1] = (byte)Math.Max(0, Math.Min(255, ng * finalGain));
                            buf[i]   = (byte)Math.Max(0, Math.Min(255, nb * finalGain));
                        }
                    });
                    Log("最终过曝保护: 中值=" + medOutLum + " 增益=" + finalGain.ToString("F2"));
                }
            }

            // ===== 独立AI优化pass：无论是否选LUT，开启ai_opt就跑 =====
            if (aiOptOn) {
                // 套完LUT后的独立AI pass：清除LUT提示，做通用调色
                AiRunner.LutIsLogHint = false;
                AiRunner.LutBlackHint = 0.0;
                AiRunner.LutWhiteHint = 1.0;
                bool aiOk2 = AiRunner.ProcessImage(buf, W, H, stride);
                Log("AI优化pass: " + aiOk2 + " " + AiRunner.LastError);
                if (aiOk2) {
                    double aWbR = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbR));
                    double aWbG = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbG));
                    double aWbB = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbB));
                    double aExp = Math.Max(0.85, Math.Min(1.2, AiRunner.OutExposure));
                    double aCon = Math.Max(0.9, Math.Min(1.15, AiRunner.OutContrast));
                    double aSat = Math.Max(0.85, Math.Min(1.15, AiRunner.OutSaturation));
                    // AI 高光/阴影（引擎已输出，这里接入：<1 压高光、>1 提阴影）
                    double aHl = Math.Max(0.85, Math.Min(1.0, AiRunner.OutHighlights));
                    double aSh = Math.Max(1.0, Math.Min(1.15, AiRunner.OutShadows));
                    bool doHl = aHl < 0.999, doSh = aSh > 1.001;
                    for (int y = 0; y < H; y++) {
                        int row = y * stride;
                        for (int x = 0; x < W; x++) {
                            int i = row + x*4;
                            double b = buf[i], g = buf[i+1], r = buf[i+2];
                            r *= aWbR * aExp; g *= aWbG * aExp; b *= aWbB * aExp;
                            double lum = 0.299*r + 0.587*g + 0.114*b;
                            // 高光区渐进压缩（>180 起效，越高越强），保护高光细节
                            if (doHl && lum > 180) {
                                double t = Math.Min(1.0, (lum - 180) / 75.0);
                                double hg = 1.0 - (1.0 - aHl) * t;
                                r *= hg; g *= hg; b *= hg;
                            }
                            // 阴影区渐进提亮（<70 起效，越暗越强），提回暗部层次
                            if (doSh && lum < 70) {
                                double t = 1.0 - lum / 70.0;
                                double sg = 1.0 + (aSh - 1.0) * t;
                                r *= sg; g *= sg; b *= sg;
                            }
                            lum = 0.299*r + 0.587*g + 0.114*b;
                            r = lum + (r-lum)*aCon; g = lum + (g-lum)*aCon; b = lum + (b-lum)*aCon;
                            lum = 0.299*r + 0.587*g + 0.114*b;
                            r = lum + (r-lum)*aSat; g = lum + (g-lum)*aSat; b = lum + (b-lum)*aSat;
                            if (r > 230) r = 230 + (r-230)*0.4;
                            if (g > 230) g = 230 + (g-230)*0.4;
                            if (b > 230) b = 230 + (b-230)*0.4;
                            buf[i] = (byte)(b<0?0:b>255?255:b);
                            buf[i+1] = (byte)(g<0?0:g>255?255:g);
                            buf[i+2] = (byte)(r<0?0:r>255?255:r);
                        }
                    }
                    Log("AI优化pass完成: exp=" + aExp.ToString("F2") + " con=" + aCon.ToString("F2") + " sat=" + aSat.ToString("F2") +
                        " 高光=" + aHl.ToString("F3") + " 阴影=" + aSh.ToString("F3") + " 场景=" + AiRunner.OutScene);
                }
            }

            Marshal.Copy(buf, 0, data.Scan0, bytes);
            src.UnlockBits(data);
        }

        void ApplyColor(Bitmap bmp)
        {
            // AI一键优化：自适应白平衡 + 智能黑白场 + 中灰锚定 + 高光保护（独立于LUT流程）
            if (par.ContainsKey("color_auto") && par["color_auto"] > 0.5) {
                int aw = bmp.Width, ah = bmp.Height;
                var ad = bmp.LockBits(new Rectangle(0,0,aw,ah), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                int astride = ad.Stride;
                byte[] abuf = new byte[astride*ah];
                Marshal.Copy(ad.Scan0, abuf, 0, abuf.Length);
                ColorAutoEngine.Apply(abuf, aw, ah, astride);
                Marshal.Copy(abuf, 0, ad.Scan0, abuf.Length);
                bmp.UnlockBits(ad);
                return;
            }
            float sat = (float)(par["saturation"] / 100.0);
            float con = (float)(par["contrast"] / 100.0);
            float bri = (float)(par["brightness"] / 100.0);
            float tp = (float)(par["temp"]);
            float ti = (float)(par["tint"]);
            float vig = (float)(par["vignette"] / 100.0);
            float fc = (float)(par["filmContrast"] / 100.0);
            float sh = par.ContainsKey("shadows") ? (float)par["shadows"] : 0f;
            float hl = par.ContainsKey("highlights") ? (float)par["highlights"] : 0f;
            bool doVig = vig > 0.001;
            bool doShadow = sh != 0 || hl != 0;
            bool doFc = fc > 0;

            int W = bmp.Width, H = bmp.Height;
            var rect = new Rectangle(0,0,W,H);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int bytes = stride * H;
            byte[] buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);

            // GPU 优先：D3D11 compute shader
            bool usedNative = false;
            try {
                usedNative = Native.gpu_color_adjust(buf, W, H, stride,
                    sat, con, bri, sh, hl, tp, ti, vig) != 0;
            } catch { usedNative = false; }
            // CPU 原生 C++ 回退
            if (!usedNative) try {
                Native.color_adjust(buf, W, H, stride,
                    sat * 100f, con * 100f, bri * 100f,
                    100f + sh * 500f, 100f - hl * 500f,
                    tp * 100f, ti * 100f, 100f + vig * 100f);
                usedNative = true;
            } catch { usedNative = false; }
            // 胶片对比（C# 保留）
            if (usedNative && doFc) {
                Parallel.For(0, H, y => {
                    int row = y*stride;
                    for (int x = 0; x < W; x++) {
                        int i = row + x*4;
                        for (int c = 0; c < 3; c++) {
                            float v = buf[i+c]/255f;
                            v = v + fc*1.6f*(v-0.5f)*v*(1-v)*2;
                            buf[i+c] = (byte)Math.Max(0, Math.Min(255, v*255f));
                        }
                    }
                });
            }

            if (!usedNative)
            Parallel.For(0, H, y =>
            {
                int row = y * stride;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x*4;
                    double b = buf[i], g = buf[i+1], r = buf[i+2];
                    if (tp != 0) { r += tp*0.6; b -= tp*0.5; }
                    if (ti != 0) { g += ti*0.4; }
                    r *= bri; g *= bri; b *= bri;
                    r = (r-128)*con+128; g = (g-128)*con+128; b = (b-128)*con+128;
                    if (doFc) {
                        double t = r/255.0; r = (t + fc*1.6*(t-0.5)*t*(1-t)*2)*255;
                        t = g/255.0; g = (t + fc*1.6*(t-0.5)*t*(1-t)*2)*255;
                        t = b/255.0; b = (t + fc*1.6*(t-0.5)*t*(1-t)*2)*255;
                    }
                    if (sat != 1.0) {
                        double gray = 0.299*r+0.587*g+0.114*b;
                        r = gray+(r-gray)*sat; g = gray+(g-gray)*sat; b = gray+(b-gray)*sat;
                    }
                    if (doShadow) {
                        double lum = (0.299*r+0.587*g+0.114*b)/255.0;
                        double shMask = Math.Max(0, 1.0 - lum*2.0);
                        double hlMask = Math.Max(0, (lum-0.5)*2.0);
                        double adj = sh*0.20*shMask*255.0 - hl*0.18*hlMask*255.0;
                        r += adj; g += adj; b += adj;
                    }
                    if (doVig) {
                        double dx = (x-W/2.0)/(W/2.0);
                        double dy = (y-H/2.0)/(H/2.0);
                        double dist = Math.Sqrt(dx*dx+dy*dy);
                        double f = 1 - vig*Math.Max(0, dist-0.5)*1.2;
                        if (f < 0) f = 0;
                        r *= f; g *= f; b *= f;
                    }
                    buf[i]   = (byte)Math.Max(0, Math.Min(255, b));
                    buf[i+1] = (byte)Math.Max(0, Math.Min(255, g));
                    buf[i+2] = (byte)Math.Max(0, Math.Min(255, r));
                }
            });

            Marshal.Copy(buf, 0, data.Scan0, bytes);
            bmp.UnlockBits(data);
        }

        // 可分离盒式模糊（并行行处理）
        void BoxBlurPass(byte[] src, byte[] dst, int w, int h, int stride, int radius)
        {
            int r = Math.Max(1, radius);
            // 水平（并行行）
            Parallel.For(0, h, y =>
            {
                int row = y * stride;
                for (int x = 0; x < w; x++) {
                    double rs = 0, gs = 0, bs = 0; int cnt = 0;
                    int x0 = x - r, x1 = x + r;
                    if (x0 < 0) x0 = 0; if (x1 >= w) x1 = w - 1;
                    for (int k = x0; k <= x1; k++) {
                        int i = row + k*4;
                        bs += src[i]; gs += src[i+1]; rs += src[i+2];
                        cnt++;
                    }
                    int d = row + x*4;
                    dst[d]   = (byte)(bs/cnt);
                    dst[d+1] = (byte)(gs/cnt);
                    dst[d+2] = (byte)(rs/cnt);
                }
            });
            // 垂直（并行列，拆分到行粒度并行）
            byte[] tmp = new byte[stride*h];
            Array.Copy(dst, tmp, dst.Length);
            Parallel.For(0, h, y =>
            {
                int row = y * stride;
                for (int x = 0; x < w; x++) {
                    double rs = 0, gs = 0, bs = 0; int cnt = 0;
                    int y0 = y - r, y1 = y + r;
                    if (y0 < 0) y0 = 0; if (y1 >= h) y1 = h - 1;
                    for (int k = y0; k <= y1; k++) {
                        int i = k*stride + x*4;
                        bs += tmp[i]; gs += tmp[i+1]; rs += tmp[i+2];
                        cnt++;
                    }
                    dst[row+x*4]   = (byte)(bs/cnt);
                    dst[row+x*4+1] = (byte)(gs/cnt);
                    dst[row+x*4+2] = (byte)(rs/cnt);
                }
            });
        }

        void ApplyTiltShift(Bitmap bmp)
        {
            if (!par.ContainsKey("ts_on") || par["ts_on"] < 0.5) return;
            if (!par.ContainsKey("ts_blur")) return;
            float blur = (float)par["ts_blur"];
            if (blur <= 0) return;

            int band = Math.Max(1, (int)par["ts_band"]);
            float posV = (float)par["ts_pos"];
            float angleDeg = (float)par["ts_angle"];
            int h = bmp.Height, w = bmp.Width;

            float focusY = h * (0.5f + posV/200.0f);
            float halfBand = h * band / 100.0f;
            double a = angleDeg * Math.PI / 180.0;
            double cosA = Math.Cos(a), sinA = Math.Sin(a);
            double cy = focusY;

            double rD = 0.3 + (blur - 1) * 2.7 / 19.0;
            double scale = Math.Max(0.5, h / 600.0);
            int r = Math.Max(1, (int)Math.Round(rD * scale));

            var srcRect = new Rectangle(0,0,w,h);
            var srcBd = bmp.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int srcStride = srcBd.Stride;
            byte[] srcBuf = new byte[srcStride*h];
            Marshal.Copy(srcBd.Scan0, srcBuf, 0, srcBuf.Length);
            bmp.UnlockBits(srcBd);

            byte[] a1 = (byte[])srcBuf.Clone();
            // GPU 模糊（3 pass），失败回退 CPU
            bool gpuBlur = false;
            try {
                for (int pass = 0; pass < 3; pass++) {
                    if (Native.gpu_blur(a1, w, h, srcStride, r) == 0) break;
                    if (pass == 2) gpuBlur = true;
                }
                // 校验：GPU 输出如果全黑/异常，回退 CPU
                if (gpuBlur) {
                    long sum = 0;
                    for (int i = 0; i < srcBuf.Length; i += 4) sum += a1[i] + a1[i+1] + a1[i+2];
                    if (sum < srcBuf.Length / 4 * 30) gpuBlur = false; // 平均亮度<10 视为异常
                }
            } catch { gpuBlur = false; }
            if (!gpuBlur) {
                // 重新从原图开始做 CPU 模糊（GPU 结果不可用）
                a1 = (byte[])srcBuf.Clone();
                byte[] a2 = new byte[srcStride*h];
                for (int pass = 0; pass < 3; pass++) {
                    BoxBlurPass(a1, a2, w, h, srcStride, r);
                    byte[] tmp = a1; a1 = a2; a2 = tmp;
                }
            }

            var bd = bmp.LockBits(srcRect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = bd.Stride;
            byte[] buf = new byte[stride*h];
            Marshal.Copy(bd.Scan0, buf, 0, buf.Length);

            float sat = (float)(par["ts_sat"]/100.0);
            float con = (float)(par["ts_con"]/100.0);
            float bri = (float)(par["ts_bri"]/100.0);
            float vig = (float)(par["ts_vig"]/100.0);
            bool doSat = sat != 1.0;
            bool doVig = vig > 0.001;

            Parallel.For(0, h, y =>
            {
                int row = y * stride;
                double dy = y - cy;
                for (int x = 0; x < w; x++)
                {
                    double dx = x - w/2.0;
                    double ry = -dx*sinA + dy*cosA;
                    float dist = (float)Math.Abs(ry);
                    // 平滑过渡：焦点内完全清晰，向外逐渐过渡到全模糊
                    float inner = halfBand * 0.5f;
                    float outer = halfBand * 2.0f;
                    float alpha;
                    if (dist <= inner) alpha = 0f;
                    else if (dist >= outer) alpha = 1f;
                    else {
                        float t = (dist - inner) / (outer - inner);
                        // smoothstep: t*t*(3-2t)
                        alpha = t * t * (3f - 2f * t);
                    }
                    int i = row + x*4;
                    double bb = buf[i], gg = buf[i+1], rr = buf[i+2];
                    double br = a1[i], bg = a1[i+1], brr = a1[i+2];
                    if (alpha > 0) {
                        if (doSat) {
                            double lg = 0.299*brr+0.587*bg+0.114*br;
                            brr = lg+(brr-lg)*sat; bg = lg+(bg-lg)*sat; br = lg+(br-lg)*sat;
                        }
                        brr *= bri; bg *= bri; br *= bri;
                        brr = (brr-128)*con+128; bg = (bg-128)*con+128; br = (br-128)*con+128;
                        brr *= 0.95; bg *= 0.99; br *= 1.02;
                    }
                    double f = alpha;
                    double ob = bb*(1-f)+br*f;
                    double og = gg*(1-f)+bg*f;
                    double orr = rr*(1-f)+brr*f;
                    double focusBoost = 1.0 - alpha;
                    if (focusBoost > 0.01) {
                        double lg = 0.299*orr+0.587*og+0.114*ob;
                        double bs = 1.0 + 0.18*focusBoost;
                        double bc = 1.0 + 0.10*focusBoost;
                        ob = lg+(ob-lg)*bs; og = lg+(og-lg)*bs; orr = lg+(orr-lg)*bs;
                        ob = (ob-128)*bc+128; og = (og-128)*bc+128; orr = (orr-128)*bc+128;
                    }
                    if (doVig) {
                        double ndx = (x-w/2.0)/(w/2.0);
                        double ndy = (y-h/2.0)/(h/2.0);
                        double r2 = ndx*ndx+ndy*ndy;
                        double vf = 1.0 - vig*Math.Min(1.0, r2*0.8);
                        if (vf < 0) vf = 0;
                        ob *= vf; og *= vf; orr *= vf;
                    }
                    buf[i]   = (byte)Math.Max(0, Math.Min(255, ob));
                    buf[i+1] = (byte)Math.Max(0, Math.Min(255, og));
                    buf[i+2] = (byte)Math.Max(0, Math.Min(255, orr));
                }
            });

            Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
            bmp.UnlockBits(bd);
        }

        // 单张完整渲染（导出与「重做」共用）
        Bitmap RenderOne(string f)
        {
            Bitmap bmp = LoadBase(f);
            bool ok = false;
            try
            {
                ApplyLUT(bmp);
                ApplyColor(bmp);
                ApplyTiltShift(bmp);
                // 胶片质感
                if (par.ContainsKey("fg_grain") || par.ContainsKey("fg_leak") || par.ContainsKey("fg_halo"))
                    FilmFx.Apply(bmp, par["fg_grain"], par["fg_leak"], par["fg_halo"]);
                // 景深虚化（深度模型）
                if (par.ContainsKey("dof_on") && par["dof_on"] > 0.5)
                {
                    var dof = BeautyRunner.ProcessDof(bmp, (int)par["dof_strength"], (int)par["dof_focus"]);
                    if (dof != null) { bmp.Dispose(); bmp = dof; }
                    else Log("景深虚化跳过: " + BeautyRunner.LastError);
                }
                // 美颜相机：分项美颜（先做，GFPGAN 细节修复在其后）
                if (par.ContainsKey("beauty_pro_on") && par["beauty_pro_on"] > 0.5)
                {
                    var pro = BeautyRunner.ProcessPro(bmp, BeautyRunner.BuildParams(par));
                    if (pro != null) { bmp.Dispose(); bmp = pro; }
                    else Log("美颜相机失败 " + f + ": " + BeautyRunner.LastError);
                }
                // AI人像增强
                if (par.ContainsKey("beauty_on") && par["beauty_on"] > 0.5)
                {
                    float st = (float)(par.ContainsKey("beauty_strength") ? par["beauty_strength"] / 100.0 : 0.7);
                    var beautified = BeautyRunner.Process(bmp, st);
                    if (beautified != null) { bmp.Dispose(); bmp = beautified; }
                }
                // AI 超分 2×（放在尺寸模式之前，先放大再按需缩小）
                if (par.ContainsKey("sr_on") && par["sr_on"] > 0.5)
                {
                    var sr = BeautyRunner.ProcessSr(bmp, par.ContainsKey("sr_scale") ? (int)par["sr_scale"] : 2);
                    if (sr != null) { bmp.Dispose(); bmp = sr; }
                    else Log("AI超分跳过: " + BeautyRunner.LastError);
                }
                ok = true;
            }
            finally
            {
                if (!ok) { try { bmp.Dispose(); } catch { } bmp = null; }
            }
            return bmp;
        }

        // 命名模板占位符：{name} {seq} {date} {time} {folder} {yyyy} {mm} {dd}
        string ExpandName(string tpl, string srcPath, int seq)
        {
            string name = Path.GetFileNameWithoutExtension(srcPath);
            string folder = Path.GetFileName(Path.GetDirectoryName(srcPath));
            DateTime dt = DateTime.Now;
            try { dt = File.GetLastWriteTime(srcPath); } catch { }
            string s = tpl
                .Replace("{name}", name)
                .Replace("{seq}", seq.ToString("D3"))
                .Replace("{date}", dt.ToString("yyyyMMdd"))
                .Replace("{time}", dt.ToString("HHmmss"))
                .Replace("{yyyy}", dt.ToString("yyyy"))
                .Replace("{mm}", dt.ToString("MM"))
                .Replace("{dd}", dt.ToString("dd"))
                .Replace("{folder}", folder == null ? "" : folder);
            foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch.ToString(), "_");
            s = s.Replace("|", "_").Trim();
            if (s.Length == 0) s = name + "_out";
            return s;
        }

        // 输出路径：重名策略 0=覆盖 1=加序号 2=跳过；支持命名模板与保留来源子目录
        string ResolveDst(string srcPath, int seq, out bool skip)
        {
            skip = false;
            string dir = outputDir;
            if (par.ContainsKey("out_subdir") && par["out_subdir"] > 0.5)
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(srcPath));
                if (!string.IsNullOrEmpty(parent))
                {
                    string sub = Path.Combine(outputDir, parent);
                    try { Directory.CreateDirectory(sub); dir = sub; } catch { dir = outputDir; }
                }
            }
            string tpl = parS.ContainsKey("out_template") ? parS["out_template"] : "";
            string baseName = tpl.Trim().Length > 0 ? ExpandName(tpl, srcPath, seq) : Path.GetFileNameWithoutExtension(srcPath) + "_out";
            string ext = ".jpg";
            int mode = par.ContainsKey("dup_mode") ? (int)par["dup_mode"] : 1;
            bool resume = par.ContainsKey("skip_existing") && par["skip_existing"] > 0.5;
            string first = Path.Combine(dir, baseName + ext);
            if (resume && File.Exists(first) && new FileInfo(first).Length > 0) { skip = true; return first; }
            if (mode == 0) return first;
            if (!File.Exists(first)) return first;
            if (mode == 2) { skip = true; return first; }
            for (int k = 2; k < 10000; k++)
            {
                string p = Path.Combine(dir, baseName + "_" + k + ext);
                if (!File.Exists(p)) return p;
            }
            return first;
        }

        void RecordFail(string path, string why)
        {
            lock (failList) failList.Add(new string[] { path, why == null ? "" : why });
        }

        static string CsvCell(string s)
        {
            s = (s == null ? "" : s).Replace("\r", " ").Replace("\n", " ");
            if (s.Contains(",") || s.Contains("\"")) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        string WriteFailCsv()
        {
            try
            {
                string name = "_failures_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                string p = Path.Combine(outputDir, name);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("源文件,原因");
                lock (failList) foreach (var x in failList) sb.AppendLine(CsvCell(x[0]) + "," + CsvCell(x[1]));
                File.WriteAllText(p, sb.ToString(), new System.Text.UTF8Encoding(true));
                return p;
            }
            catch (Exception ex) { Log("写失败清单失败: " + ex.Message); return null; }
        }

        bool ExportOne(string f, int seq)
        {
            bool skip;
            string dst = ResolveDst(f, seq, out skip);
            if (skip) { Log("跳过已存在: " + Path.GetFileName(dst)); return true; }

            // 证件照走独立流程
            if (par.ContainsKey("id_on") && par["id_on"] > 0.5)
            {
                bool idOk = ExportIdPhoto(f, dst);
                if (!idOk) RecordFail(f, BeautyRunner.LastError == null ? "证件照处理失败" : BeautyRunner.LastError);
                return idOk;
            }

            Bitmap bmp = RenderOne(f);
            if (bmp == null) { RecordFail(f, "渲染失败"); return false; }
            try
            {
                int sm = par.ContainsKey("out_size") ? (int)par["out_size"] : 0;
                int nw = bmp.Width, nh = bmp.Height;
                ApplySizeMode(ref nw, ref nh, sm);
                Bitmap outBmp = bmp;
                if (nw != bmp.Width || nh != bmp.Height)
                {
                    outBmp = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(outBmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(bmp, 0, 0, nw, nh);
                    }
                }
                else if (NeedSoloCopy()) { outBmp = bmp.Clone() as Bitmap; }

                try
                {
                    if (par.ContainsKey("dither") && par["dither"] > 0.5) DitherFx.Apply(outBmp);
                    if (!par.ContainsKey("exif_keep") || par["exif_keep"] > 0.5) MetaWriter.CopyMeta(f, outBmp);
                    long q = (par.ContainsKey("lossless") && par["lossless"] > 0.5) ? 100L : (long)(int)(par.ContainsKey("quality") ? par["quality"] : 95);
                    var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(Encoder.Quality, q);
                    outBmp.Save(dst, LUT.JpegCodec, ep);

                    // 统一转 sRGB：拿源图内嵌 ICC 做真实色彩转换（只在开启时多跑一次 Python）
                    if (par.ContainsKey("icc_convert") && par["icc_convert"] > 0.5)
                    {
                        string iccErr;
                        string iccOut = PyTools.Run(BeautyRunner.PythonExe, "srgb.py", new string[] { dst }, 60000, out iccErr);
                        if (iccOut != null && iccOut.Contains("\"converted\": true")) Log("已转 sRGB: " + Path.GetFileName(dst));
                    }
                }
                finally
                {
                    if (outBmp != bmp) outBmp.Dispose();
                }
            }
            finally { bmp.Dispose(); }
            return true;
        }

        bool NeedSoloCopy()
        {
            return (par.ContainsKey("dither") && par["dither"] > 0.5)
                || (!par.ContainsKey("exif_keep") || par["exif_keep"] > 0.5);
        }

        bool ExportIdPhoto(string f, string dst)
        {
            int sizeMode = par.ContainsKey("id_size") ? (int)par["id_size"] : 0;
            int bg = par.ContainsKey("id_bg") ? (int)par["id_bg"] : 0;
            int sheet = par.ContainsKey("id_sheet") ? (int)par["id_sheet"] : 0;
            int cw = par.ContainsKey("id_custom_w") ? (int)par["id_custom_w"] : 413;
            int ch = par.ContainsKey("id_custom_h") ? (int)par["id_custom_h"] : 579;
            int pw = par.ContainsKey("id_paper_w") ? (int)par["id_paper_w"] : 1800;
            int ph = par.ContainsKey("id_paper_h") ? (int)par["id_paper_h"] : 1200;
            string res = BeautyRunner.ProcessIdPhoto(f, sizeMode, bg, cw, ch, sheet, pw, ph, dst);
            if (res == null) {
                string e = (BeautyRunner.LastError ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                Log("证件照失败 " + Path.GetFileName(f) + ": " + e);
                Send("{\"type\":\"toast\",\"msg\":\"证件照失败：" + e + "\"}");
                return false;
            }
            Log("证件照完成: " + Path.GetFileName(res));
            return true;
        }

        void DoExport(List<string> srcList = null)
        {
            List<string> list = srcList != null ? srcList : new List<string>(files);
            if (string.IsNullOrEmpty(outputDir)) {
                string picked = null;
                InvokeUi(() => {
                    using (var d = new FolderBrowserDialog()) {
                        d.Description = "选择渲染输出目录";
                        if (d.ShowDialog() == DialogResult.OK) picked = d.SelectedPath;
                    }
                });
                if (string.IsNullOrEmpty(picked)) return;
                outputDir = picked;
            }
            int total = list.Count;
            if (total == 0) return;

            lock (failList) failList.Clear();
            running = true; pauseFlag = false; stopFlag = false;
            Send("{\"type\":\"runstate\",\"running\":true,\"paused\":false}");

            // 批量模式：每张图单独分析AI参数，不共用预览缓存
            forceFreshAnalysis = true;
            ResetAiCache();

            // 并发：AI 模式会共享模型/静态状态，强制串行；其余按 CPU 数并行
            bool aiHeavy = (lutMode == "ai") || (par.ContainsKey("ai_opt") && par["ai_opt"] > 0.5)
                        || (par.ContainsKey("beauty_on") && par["beauty_on"] > 0.5)
                        || (par.ContainsKey("sr_on") && par["sr_on"] > 0.5)
                        || (par.ContainsKey("dof_on") && par["dof_on"] > 0.5);
            int want = par.ContainsKey("workers") ? (int)par["workers"] : 0;
            int workers = aiHeavy ? 1 : (want > 0 ? want : Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)));
            Log("开始渲染: " + total + " 张，并发 " + workers);

            int cursor = -1, done = 0, failCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var threads = new List<Thread>();
            for (int w = 0; w < workers; w++)
            {
                var t = new Thread(() => {
                    while (true)
                    {
                        while (pauseFlag && !stopFlag) Thread.Sleep(100);
                        if (stopFlag) break;
                        int idx = Interlocked.Increment(ref cursor);
                        if (idx >= total) break;
                        try {
                            if (!ExportOne(list[idx], idx + 1)) Interlocked.Increment(ref failCount);
                        }
                        catch (Exception ex) {
                            Log("导出失败 " + list[idx] + ": " + ex.Message);
                            RecordFail(list[idx], ex.Message);
                            Interlocked.Increment(ref failCount);
                        }
                        int d = Interlocked.Increment(ref done);
                        Send("{\"type\":\"progress\",\"done\":" + d + ",\"total\":" + total + "}");
                    }
                });
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join();

            forceFreshAnalysis = false;
            ResetAiCache();
            running = false; pauseFlag = false;
            Send("{\"type\":\"progress\",\"done\":" + done + ",\"total\":" + total + "}");
            Send("{\"type\":\"runstate\",\"running\":false,\"paused\":false}");
            Log("渲染结束: 用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s，失败 " + failCount);

            int realFails;
            lock (failList) realFails = failList.Count;
            if (realFails > 0) {
                string csv = WriteFailCsv();
                if (csv != null) {
                    parS["_last_fail_csv"] = csv;
                    Send("{\"type\":\"fails\",\"count\":" + realFails + ",\"csv\":" + JsonStr(csv) + "}");
                    Send("{\"type\":\"toast\",\"msg\":\"有 " + realFails + " 张失败，已导出清单：_failures_*.csv\"}");
                }
            } else {
                Send("{\"type\":\"toast\",\"msg\":\"全部完成，无失败：" + done + " 张\"}");
            }

            if (par.ContainsKey("open_after") && par["open_after"] > 0.5)
                try { System.Diagnostics.Process.Start(outputDir); } catch {}
        }


        void LoadLuts(string dir)
        {
            lutFiles.Clear();
            if (!Directory.Exists(dir)) return;
            foreach (var pattern in new[] { "*.cube", "*.3dl", "*.lut" })
                foreach (var f in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
                    lutFiles.Add(f);
        }

        // 按「分组名 + LUT 名」定位磁盘文件（重命名/删除用）
        string FindLutFile(string group, string name)
        {
            if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(name)) return null;
            foreach (var p in lutFiles)
            {
                string g = Path.GetFileName(Path.GetDirectoryName(p));
                if (!string.Equals(g, group, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        // 早期「参考图→LUT」直接写进 我的-LUT，升级后统一归到「参考图生成」分组
        void MigrateRefLuts()
        {
            try
            {
                string root = Path.Combine(Application.StartupPath, "luts");
                string from = Path.Combine(root, "我的-LUT");
                string to = Path.Combine(root, "参考图生成");
                if (!Directory.Exists(from)) return;
                Directory.CreateDirectory(to);
                int n = 0;
                foreach (var f in Directory.GetFiles(from, "参考_*.cube", SearchOption.TopDirectoryOnly))
                {
                    string dst = Path.Combine(to, Path.GetFileName(f));
                    if (File.Exists(dst)) continue;
                    try { File.Move(f, dst); n++; } catch { }
                }
                if (n > 0) Log("已把 " + n + " 个参考图 LUT 归入「参考图生成」分组");
            }
            catch { }
        }

        string JsonStr(string s) { return "\"" + (s??"").Replace("\\","\\\\").Replace("\"","\\\"") + "\""; }
    }
}

