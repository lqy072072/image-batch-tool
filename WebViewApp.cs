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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Windows.Forms;
using Encoder = System.Drawing.Imaging.Encoder;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace ImageBatchTool
{
    // 全局缓存路径：默认在安装目录 cache；用户可在相册「选项 → 缓存位置」中改到其他文件夹。
    // 缓存位置配置文件放在 AppData，不能放在 cache 内，否则换位置后会找不到配置。
    static class CacheStore
    {
        static readonly object sync = new object();
        static string _root;

        static string ConfigDir()
        {
            try { return AppModule.ConfigDir(); }
            catch { return Path.Combine(Application.StartupPath, "config"); }
        }

        public static string RootConfigFile()
        {
            return Path.Combine(ConfigDir(), "cache_root.txt");
        }

        static string PendingFile()
        {
            return Path.Combine(ConfigDir(), "cache_move.txt");
        }

        public static string DefaultRoot()
        {
            return Path.Combine(Application.StartupPath, "cache");
        }

        static bool PathsEqual(string a, string b)
        {
            try
            {
                string pa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string pb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static bool IsSubPath(string child, string parent)
        {
            try
            {
                string pc = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string pp = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return pc.StartsWith(pp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string Root()
        {
            lock (sync)
            {
                if (_root != null) return _root;
                string configured = DefaultRoot();
                try
                {
                    string cfg = RootConfigFile();
                    if (File.Exists(cfg))
                    {
                        string text = File.ReadAllText(cfg).Trim();
                        if (text.Length > 0) configured = Path.GetFullPath(text);
                    }
                }
                catch { configured = DefaultRoot(); }
                _root = configured;
                try
                {
                    if (!Directory.Exists(_root)) Directory.CreateDirectory(_root);
                }
                catch
                {
                    // 自定义盘符暂时不可用时先退回默认位置，保证程序还能启动；
                    // 配置不改，下次盘符恢复后仍会使用用户设置。
                    try
                    {
                        string fallback = DefaultRoot();
                        Directory.CreateDirectory(fallback);
                        _root = fallback;
                    }
                    catch { }
                }
                EnsureMarker(_root);
                return _root;
            }
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

        static void WriteRootConfig(string root)
        {
            Directory.CreateDirectory(ConfigDir());
            File.WriteAllText(RootConfigFile(), root, new System.Text.UTF8Encoding(false));
        }

        // 缓存根标识：卸载器只清理确实由本程序创建的自定义缓存目录，
        // 避免误删用户自己选择的其他文件夹中的同名子目录。
        static void EnsureMarker(string root)
        {
            try
            {
                string marker = Path.Combine(root, ".ibt_cache_root");
                if (!File.Exists(marker))
                    File.WriteAllText(marker, "ImageBatchTool cache root\n", new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // 只记录待迁移信息；真正移动放在下次启动、WebView2 还没打开缓存目录时执行。
        // 这样不会在程序运行中移动被 WebView2 锁住的 webview2 目录。
        public static void RequestMove(string newRoot)
        {
            string target = Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string oldRoot = Root();
            if (PathsEqual(oldRoot, target)) throw new InvalidOperationException("新缓存位置与当前位置相同");
            if (IsSubPath(target, oldRoot) || IsSubPath(oldRoot, target))
                throw new InvalidOperationException("新缓存位置不能位于当前缓存目录内部，也不能包含当前缓存目录");
            Directory.CreateDirectory(ConfigDir());
            File.WriteAllText(PendingFile(), oldRoot + "\r\n" + target, new System.Text.UTF8Encoding(false));
        }

        static void CopyCacheTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            int copied = 0;
            foreach (string f in Directory.GetFiles(src))
            {
                string to = Path.Combine(dst, Path.GetFileName(f));
                try
                {
                    // 支持断点续迁：目标已有且大小、时间一致的文件直接跳过。
                    // 这样中途退出后再次启动不会把几百 MB 缓存全部重新复制一遍。
                    if (File.Exists(to))
                    {
                        try
                        {
                            var si = new FileInfo(f);
                            var di = new FileInfo(to);
                            if (si.Length == di.Length &&
                                si.LastWriteTimeUtc == di.LastWriteTimeUtc)
                            {
                                copied++;
                                if ((copied % 300) == 0) Application.DoEvents();
                                continue;
                            }
                        }
                        catch { }
                        try { File.SetAttributes(to, FileAttributes.Normal); } catch { }
                    }
                    File.Copy(f, to, true);
                }
                catch (Exception ex) { throw new IOException("复制缓存文件失败: " + f + " -> " + to, ex); }
                copied++;
                if ((copied % 300) == 0) Application.DoEvents();
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                string to = Path.Combine(dst, Path.GetFileName(d));
                CopyCacheTree(d, to);
            }
        }

        static void MoveCacheTree(string src, string dst)
        {
            if (!Directory.Exists(src))
            {
                Directory.CreateDirectory(dst);
                return;
            }
            if (PathsEqual(src, dst)) return;

            bool dstEmpty = Directory.Exists(dst) && Directory.GetFileSystemEntries(dst).Length == 0;
            if (!Directory.Exists(dst) || dstEmpty)
            {
                try
                {
                    if (Directory.Exists(dst)) Directory.Delete(dst);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    Directory.Move(src, dst);
                    return;
                }
                catch { /* 跨盘或目录被占用时走逐文件复制 */ }
            }

            // 目标目录已有内容时合并；旧缓存同名文件覆盖目标中的同名缓存文件。
            CopyCacheTree(src, dst);
            try { Directory.Delete(src, true); } catch { }
        }

        public static void ApplyPendingMove()
        {
            string pending = PendingFile();
            if (!File.Exists(pending)) return;

            // 同一时间只允许一个实例执行迁移。否则用户看到“没窗口”会反复双击，
            // 多个进程同时把同一份缓存复制到同一目标，互相覆盖并卡死。
            bool lockTaken = false;
            Mutex moveMutex = null;
            try
            {
                moveMutex = new Mutex(false, @"Local\ImageBatchToolCacheMove");
                try { lockTaken = moveMutex.WaitOne(0); }
                catch (AbandonedMutexException) { lockTaken = true; }
                if (!lockTaken)
                {
                    try { MainForm.Log("另一个实例正在迁移缓存，本次跳过迁移，先使用原缓存启动"); } catch { }
                    return;
                }
                ApplyPendingMoveCore(pending);
            }
            finally
            {
                if (lockTaken)
                {
                    try { moveMutex.ReleaseMutex(); } catch { }
                }
                if (moveMutex != null) moveMutex.Dispose();
            }
        }

        static void ApplyPendingMoveCore(string pending)
        {
            if (!File.Exists(pending)) return;
            string[] lines = File.ReadAllLines(pending);
            if (lines.Length < 2)
            {
                try { File.Delete(pending); } catch { }
                return;
            }
            string oldRoot = Path.GetFullPath(lines[0].Trim());
            string newRoot = Path.GetFullPath(lines[1].Trim());
            if (PathsEqual(oldRoot, newRoot))
            {
                WriteRootConfig(newRoot);
                _root = newRoot;
                EnsureMarker(newRoot);
                File.Delete(pending);
                return;
            }
            if (IsSubPath(newRoot, oldRoot) || IsSubPath(oldRoot, newRoot))
                throw new InvalidOperationException("缓存迁移路径非法：新位置不能与旧位置互相包含");
            if (File.Exists(newRoot)) throw new IOException("新缓存位置是一个文件，不是文件夹");

            CacheMigrationForm progress = null;
            try
            {
                progress = new CacheMigrationForm(newRoot);
                progress.Show();
                Application.DoEvents();
                MoveCacheTree(oldRoot, newRoot);
                // 文件已经移到新位置后先把内存根切过去；即使配置文件写入失败，
                // 当前进程也不要去重新创建旧目录。
                lock (sync) { _root = newRoot; }
                EnsureMarker(newRoot);
                WriteRootConfig(newRoot);
                try { File.Delete(pending); } catch { }
            }
            finally
            {
                if (progress != null)
                {
                    try { progress.Close(); progress.Dispose(); } catch { }
                }
            }
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
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_BLURBEHIND { public int dwFlags; public bool fEnable; public IntPtr hRgnBlur; public bool fTransitionOnMaximized; }
        [DllImport("dwmapi.dll")] public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blur);
        [StructLayout(LayoutKind.Sequential)] public struct ACCENTPOLICY { public int State, Flags, Color, Animation; }
        [StructLayout(LayoutKind.Sequential)] public struct WINCOMPDATA { public int Attr; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPDATA data);
        public static int Accent(IntPtr hwnd, int state, int color) {
            ACCENTPOLICY a = new ACCENTPOLICY { State = state, Flags = 2, Color = color, Animation = 0 };
            WINCOMPDATA d = new WINCOMPDATA { Attr = 19, Data = Marshal.AllocHGlobal(Marshal.SizeOf(a)), Size = Marshal.SizeOf(a) };
            try { Marshal.StructureToPtr(a, d.Data, false); return SetWindowCompositionAttribute(hwnd, ref d); }
            finally { Marshal.FreeHGlobal(d.Data); }
        }

        // 整窗半透明：WS_EX_LAYERED + LWA_ALPHA。
        // WebView2 的透明页面不能叠加 TransparencyKey（会把子窗口命中测试整层跳过，
        // 鼠标全部落到下层窗口），但整窗 alpha 不影响命中测试，窗口仍可正常点击。
        public static bool SetAlpha(IntPtr hwnd, int alpha) {
            try {
                if (hwnd == IntPtr.Zero || alpha < 0 || alpha > 255) return false;
                int ex = GetWindowLong(hwnd, -20);
                if ((ex & 0x80000) == 0) {
                    SetWindowLong(hwnd, -20, ex | 0x80000);
                }
                return SetLayeredWindowAttributes(hwnd, 0, (byte)alpha, 0x2);
            } catch { return false; }
        }

        // v2.34：液态玻璃不再需要整窗 alpha；切回不透明时把 WS_EX_LAYERED 摘掉，
        // 避免旧皮肤残留的分层样式继续影响 WebView2 的合成与圆角裁剪。
        public static bool ClearLayered(IntPtr hwnd) {
            try {
                if (hwnd == IntPtr.Zero) return false;
                int ex = GetWindowLong(hwnd, -20);
                if ((ex & 0x80000) != 0) SetWindowLong(hwnd, -20, ex & ~0x80000);
                return true;
            } catch { return false; }
        }

        // 原生 Acrylic：Win11 走 DWM 系统背景，Win10 退回 SetWindowCompositionAttribute。
        // 两者都由系统合成器做模糊，不抓屏、不做 JPEG、不跨进程推帧。
        public static bool ApplyAcrylic(IntPtr hwnd) {
            try {
                int dark = 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                int type = 3; // DWMSBT_TRANSIENTWINDOW = Acrylic
                int hr = DwmSetWindowAttribute(hwnd, 38, ref type, sizeof(int));
                if (hr == 0) {
                    var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref m);
                    return true;
                }
                int accent = Accent(hwnd, 4, unchecked((int)0x44FFFFFF));
                if (accent == 0) {
                    var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref m);
                    return true;
                }
            } catch { }
            return false;
        }
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
    // 液态玻璃实时背景：
    //   把窗口自身排除在屏幕捕获之外（WDA_EXCLUDEFROMCAPTURE），
    //   定时截取窗口所在屏幕区域 -> 缩小 -> 高斯模糊 -> 作为 Form 背景。
    //   WebView2 页面保持透明，因此毛玻璃里是窗口下方真实的桌面/其它窗口，
    //   窗口移动、下方内容变化时背景会真实跟着变化，而不是一张静态毛玻璃贴图。
    // ============================================================
    class GlassBackdrop : IDisposable
    {
        readonly Form host;
        readonly WebView2 web;
        readonly System.Windows.Forms.Timer timer;
        Bitmap cap, small;
        Bitmap fullDesktop;
        Point deskOrigin;
        Size deskSize;
        Rectangle lastBounds;
        bool hasLastBounds;
        bool enabled, failed;
        int busy;
        string lastFrame = "";

        public GlassBackdrop(Form host, WebView2 web)
        {
            this.host = host;
            this.web = web;
            // v2.37 性能修复：不再每 420ms 抓屏 + 推 JPEG。底图改为“按需刷新”：
            // 只在窗口移动结束、尺寸调整结束、重新激活时补一帧，平时保持静态。
            timer = new System.Windows.Forms.Timer { Interval = 520 };
            timer.Tick += (s, e) => { timer.Stop(); Capture(); };
            try { host.LocationChanged += (s, e) => ScheduleRefresh(); } catch { }
            try { host.ResizeEnd += (s, e) => ScheduleRefresh(); } catch { }
            try { host.Activated += (s, e) => ScheduleRefresh(); } catch { }
        }

        // 拖动/缩放期间不抓屏，停下来 0.52 秒后再取一帧，避免整窗反复重绘。
        void ScheduleRefresh()
        {
            if (!enabled || failed || host == null || host.IsDisposed || !host.Visible) return;
            try { timer.Stop(); timer.Start(); } catch { }
        }

        public bool Enabled { get { return enabled; } }

        // 在窗口显示之前抓一张整屏桌面。之后每次只补抓窗口四周区域，
        // 再按窗口当前位置裁切；窗口移动时，新位置早已是真实桌面，不会截到自己。
        public void PrimeDesktop()
        {
            try
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                try { MainForm.Log("液态玻璃 Prime " + vs.Width + "x" + vs.Height); } catch { }
                if (vs.Width < 80 || vs.Height < 80) return;
                if (fullDesktop != null && deskSize.Width == vs.Width && deskSize.Height == vs.Height && deskOrigin.X == vs.Left && deskOrigin.Y == vs.Top) return;
                if (fullDesktop != null) fullDesktop.Dispose();
                fullDesktop = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(fullDesktop))
                    g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
                deskOrigin = vs.Location;
                deskSize = vs.Size;
                try { MainForm.Log("液态玻璃底图 " + vs.Width + "x" + vs.Height + " @" + vs.X + "," + vs.Y); } catch { }
            }
            catch (Exception ex) { try { MainForm.Log("液态玻璃底图失败: " + ex.Message); } catch { } }
        }

        public bool Enable()
        {
            if (failed || host == null || host.IsDisposed) return enabled;
            if (!host.IsHandleCreated || !host.Visible) return false;
            if (enabled) { Capture(); return true; }
            try
            {
                enabled = true;
                host.BackgroundImageLayout = ImageLayout.Stretch;
                // v2.37：只取一帧静态底图，不启动持续抓屏定时器。
                Capture();
                return true;
            }
            catch { failed = true; return false; }
        }

        // v2.29 性能版：只在窗口显示后取一帧（启动底图已提前拍好），不启动 170ms 定时器。
        public bool Snapshot()
        {
            try { MainForm.Log("静态玻璃 Snapshot 请求 full=" + (fullDesktop != null) + " visible=" + (host != null && host.Visible) + " enabled=" + enabled + " failed=" + failed); } catch { }
            if (failed || host == null || host.IsDisposed || !host.IsHandleCreated || !host.Visible) return false;
            if (fullDesktop == null) return false;
            if (enabled) { PushFrame(); return true; }
            try
            {
                enabled = true;
                host.BackgroundImageLayout = ImageLayout.Stretch;
                Capture();
                try { MainForm.Log("静态玻璃 Snapshot 完成 bg=" + (host.BackgroundImage != null) + " frame=" + lastFrame.Length); } catch { }
                return true;
            }
            catch (Exception ex) { failed = true; try { MainForm.Log("静态玻璃 Snapshot 失败 " + ex.Message); } catch { } return false; }
        }

        public void Disable()
        {
            try { timer.Stop(); } catch { }
            enabled = false;
            try { if (host != null) { host.BackgroundImage = null; host.Invalidate(); } } catch { }
            DisposeBitmaps();
        }

        // 切到真透明方案时，清掉页面里残留的上一张静态玻璃底图。
        public void ClearFrame()
        {
            lastFrame = "";
            try
            {
                if (web != null && web.CoreWebView2 != null)
                    web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"glassbg\",\"clear\":true}");
            }
            catch { }
        }

        void DisposeBitmaps()
        {
            try { if (cap != null) { cap.Dispose(); cap = null; } } catch { }
            try { if (small != null) { small.Dispose(); small = null; } } catch { }
            try { if (fullDesktop != null) { fullDesktop.Dispose(); fullDesktop = null; } } catch { }
        }

        void Capture()
        {
            if (!enabled || failed || host == null || host.IsDisposed || !host.Visible) return;
            if (host.WindowState == FormWindowState.Minimized) return;
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            try
            {
                var b = host.Bounds;
                if (b.Width < 120 || b.Height < 100) return;
                if (fullDesktop == null) PrimeDesktop();
                // 上一次窗口位置四周先补抓：窗口移动后，新位置原本就在上一次窗口之外，
                // 这些区域是真实桌面，不会截到窗口自己。
                if (hasLastBounds) PatchDesktop(lastBounds);
                PatchDesktop(b);
                lastBounds = b;
                hasLastBounds = true;

                int sw = Math.Max(240, Math.Min(560, b.Width / 2));
                int sh = Math.Max(100, (int)Math.Round(sw * (double)b.Height / b.Width));
                if (small == null || small.Width != sw || small.Height != sh)
                {
                    if (small != null) small.Dispose();
                    small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
                }
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    Rectangle src = new Rectangle(b.X - deskOrigin.X, b.Y - deskOrigin.Y, b.Width, b.Height);
                    if (fullDesktop != null)
                    {
                        Rectangle safe = Rectangle.Intersect(src, new Rectangle(0, 0, fullDesktop.Width, fullDesktop.Height));
                        if (safe.Width > 0 && safe.Height > 0)
                        {
                            using (var full = new Bitmap(safe.Width, safe.Height, PixelFormat.Format32bppArgb))
                            using (var fg = Graphics.FromImage(full))
                            {
                                fg.DrawImage(fullDesktop, new Rectangle(0, 0, safe.Width, safe.Height), safe, GraphicsUnit.Pixel);
                                g.DrawImage(full, new Rectangle(0, 0, sw, sh));
                            }
                        }
                        else g.Clear(Color.FromArgb(8, 18, 32));
                    }
                    else
                    {
                        g.Clear(Color.FromArgb(8, 18, 32));
                    }
                }
                BoxBlur(small, 4, 2);
                host.BackgroundImage = small;
                host.Invalidate();
                PushFrame();
            }
            catch { }
            finally { busy = 0; }
        }

        // 只截窗口以外四条边，补进整屏底图；窗口移动后新位置就是真实桌面。
        void PatchDesktop( Rectangle b)
        {
            if (fullDesktop == null || fullDesktop.Width < 8 || fullDesktop.Height < 8) return;
            try
            {
                Rectangle vs = new Rectangle(deskOrigin, deskSize);
                using (var g = Graphics.FromImage(fullDesktop))
                {
                    PatchBand(g, Rectangle.Intersect(new Rectangle(vs.Left, vs.Top, Math.Max(0, b.Left - vs.Left), vs.Height), vs));
                    PatchBand(g, Rectangle.Intersect(new Rectangle(b.Right, vs.Top, Math.Max(0, vs.Right - b.Right), vs.Height), vs));
                    if (b.Left < vs.Right && b.Right > vs.Left)
                    {
                        int l = Math.Max(b.Left, vs.Left), r = Math.Min(b.Right, vs.Right);
                        PatchBand(g, Rectangle.Intersect(new Rectangle(l, vs.Top, Math.Max(0, r - l), Math.Max(0, b.Top - vs.Top)), vs));
                        PatchBand(g, Rectangle.Intersect(new Rectangle(l, b.Bottom, Math.Max(0, r - l), Math.Max(0, vs.Bottom - b.Bottom)), vs));
                    }
                }
            }
            catch { }
        }

        void PatchBand(Graphics g, Rectangle r)
        {
            if (fullDesktop == null || r.Width <= 0 || r.Height <= 0) return;
            Point dest = new Point(r.X - deskOrigin.X, r.Y - deskOrigin.Y);
            if (dest.X < 0 || dest.Y < 0 || dest.X + r.Width > fullDesktop.Width || dest.Y + r.Height > fullDesktop.Height) return;
            try { g.CopyFromScreen(r.Location, dest, r.Size, CopyPixelOperation.SourceCopy); } catch { }
        }

        void PushFrame()
        {
            if (web == null || web.CoreWebView2 == null || small == null) return;
            try
            {
                using (var ms = new MemoryStream())
                {
                    try { small.Save(ms, ImageFormat.Jpeg); }
                    catch (Exception ex) { try { MainForm.Log("液态玻璃 JPEG 失败: " + ex.Message); } catch { } return; }
                    byte[] bytes = ms.ToArray();
                    if (bytes.Length < 64) return;
                    string b64 = Convert.ToBase64String(bytes);
                    // 页面可能比第一帧晚 ready；静态底图每 170ms 重发一次，保证一定能收到。
                    lastFrame = b64;
                    string json = "{\"type\":\"glassbg\",\"data\":\"data:image/jpeg;base64," + b64 + "\"}";
                    web.CoreWebView2.PostWebMessageAsJson(json);
                    try { MainForm.Log("静态玻璃帧已推送 " + bytes.Length + "B"); } catch { }
                    if (lastFrame == b64) { }
                    // 帧日志会每 170ms 刷屏，正常运行不写
                }
            }
            catch { }
        }

        static unsafe void BoxBlur(Bitmap bmp, int radius, int passes)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] src = new byte[stride * h];
                byte[] tmp = new byte[stride * h];
                Marshal.Copy(data.Scan0, src, 0, src.Length);
                int win = radius * 2 + 1;
                for (int p = 0; p < passes; p++)
                {
                    // 水平
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * stride;
                        for (int c = 0; c < 3; c++)
                        {
                            int sum = 0;
                            for (int k = -radius; k <= radius; k++)
                            {
                                int xx = k < 0 ? 0 : (k >= w ? w - 1 : k);
                                sum += src[row + xx * 4 + c];
                            }
                            for (int x = 0; x < w; x++)
                            {
                                tmp[row + x * 4 + c] = (byte)(sum / win);
                                int add = x + radius + 1; if (add >= w) add = w - 1;
                                int del = x - radius; if (del < 0) del = 0;
                                sum += src[row + add * 4 + c] - src[row + del * 4 + c];
                            }
                        }
                        for (int x = 0; x < w; x++) tmp[row + x * 4 + 3] = src[row + x * 4 + 3];
                    }
                    // 垂直
                    for (int x = 0; x < w; x++)
                    {
                        int col = x * 4;
                        for (int c = 0; c < 3; c++)
                        {
                            int sum = 0;
                            for (int k = -radius; k <= radius; k++)
                            {
                                int yy = k < 0 ? 0 : (k >= h ? h - 1 : k);
                                sum += tmp[yy * stride + col + c];
                            }
                            for (int y = 0; y < h; y++)
                            {
                                src[y * stride + col + c] = (byte)(sum / win);
                                int add = y + radius + 1; if (add >= h) add = h - 1;
                                int del = y - radius; if (del < 0) del = 0;
                                sum += tmp[add * stride + col + c] - tmp[del * stride + col + c];
                            }
                        }
                        for (int y = 0; y < h; y++) src[y * stride + col + 3] = tmp[y * stride + col + 3];
                    }
                }
                Marshal.Copy(src, 0, data.Scan0, src.Length);
            }
            finally { bmp.UnlockBits(data); }
        }

        public void Dispose() { try { Disable(); } catch { } try { timer.Dispose(); } catch { } }
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
                try { MessageBox.Show("程序遇到异常：\r\n" + e.Exception.Message, AppModule.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { MainForm.Log("致命异常: " + e.ExceptionObject); } catch { }
            };
            string mode = AppModule.Id;
            MainForm.Log("程序启动 mode=" + mode);
            var main = new MainForm();
            main.StartupMode = mode;
            // v2.41：独立编辑器接受相册右键传来的图片路径；忽略 --debug-webview 等开关。
            var startupPaths = new List<string>();
            foreach (string a in args)
            {
                if (a.StartsWith("--")) continue;
                try { if (File.Exists(a) || Directory.Exists(a)) startupPaths.Add(a); } catch { }
            }
            main.StartupPaths = startupPaths.ToArray();
            main.ApplyModeIcon();
            main.FormClosed += (s, e) => MainForm.Log("主窗口已关闭");
            main.FormClosing += (s, e) => { try { main.AiStopAll(); } catch { } };
            Application.ApplicationExit += (s, e) => MainForm.Log("ApplicationExit");
            // 与独立玻璃探测程序保持同一启动顺序：先 Show 出真实窗口，再进入消息循环。
            // 直接 Application.Run(main) 会在进入循环后才首次显示，DWM 背景有时会退回不透明灰。
            main.Show();
            Application.Run();
            MainForm.Log("Application.Run 返回");
            // 修改缓存位置后重启：等旧 WebView2/文件句柄释放，再拉起新实例。
            // 新实例启动时会先执行 CacheStore.ApplyPendingMove()，再打开 WebView2。
            if (MainForm.RestartAfterCacheMove)
            {
                MainForm.RestartAfterCacheMove = false;
                try
                {
                    System.Threading.Thread.Sleep(1500);
                    var psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.Arguments = "";
                    psi.WorkingDirectory = Application.StartupPath;
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    MainForm.Log("已启动新实例完成缓存迁移");
                }
                catch (Exception ex)
                {
                    try { MainForm.Log("重启失败: " + ex.Message); } catch { }
                }
            }
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
                string shared = AppModule.SharedSkinFile();
                string sf = Path.Combine(Application.StartupPath, "skin.txt");
                if (File.Exists(shared)) skin = File.ReadAllText(shared).Trim().ToLower();
                else if (File.Exists(sf)) skin = File.ReadAllText(sf).Trim().ToLower();
            } catch {}
            bool light = skin == "light";
            bool frost = skin == "frost";
            bool aurora = skin == "aurora";
            bool glass = skin == "liquid" || skin == "smoke";
            bool pale = light || frost || aurora;
            bool eva01 = skin == "eva01" || skin == "eva-01" || skin == "unit01" || skin == "unit-01";
            bool eva00 = skin == "eva00" || skin == "eva-00" || skin == "unit00" || skin == "unit-00";
            bool eva02 = skin == "eva02" || skin == "eva-02" || skin == "unit02" || skin == "unit-02";
            BackColor = eva01 ? Color.FromArgb(7, 5, 13)
                : eva00 ? Color.FromArgb(7, 17, 29)
                : eva02 ? Color.FromArgb(16, 5, 7)
                : glass ? Color.FromArgb(19, 21, 49)
                : frost ? Color.FromArgb(246, 244, 250)
                : light ? Color.FromArgb(245, 245, 247)
                : aurora ? Color.FromArgb(245, 250, 255)
                : Color.FromArgb(43, 43, 43);

            var t = new Label();
            t.Text = AppModule.DisplayName;
            t.ForeColor = eva01 ? Color.FromArgb(240, 234, 255)
                : eva00 ? Color.FromArgb(234, 245, 255)
                : eva02 ? Color.FromArgb(255, 240, 233)
                : glass ? Color.FromArgb(246, 250, 255)
                : pale ? Color.FromArgb(29, 29, 31)
                : Color.FromArgb(220, 220, 220);
            t.Font = new Font("Microsoft YaHei UI", 14f);
            t.AutoSize = false; t.Size = new Size(360, 40); t.Location = new Point(0, 70);
            t.TextAlign = ContentAlignment.MiddleCenter;

            var sub = new Label();
            sub.Text = AppModule.SplashSubtitle;
            sub.ForeColor = eva01 ? Color.FromArgb(185, 172, 214)
                : eva00 ? Color.FromArgb(169, 196, 220)
                : eva02 ? Color.FromArgb(223, 174, 159)
                : glass ? Color.FromArgb(183, 205, 237)
                : pale ? Color.FromArgb(142, 142, 147)
                : Color.FromArgb(140, 140, 140);
            sub.Font = new Font("Microsoft YaHei UI", 9f);
            sub.AutoSize = false; sub.Size = new Size(360, 20); sub.Location = new Point(0, 120);
            sub.TextAlign = ContentAlignment.MiddleCenter;

            var bar = new Panel();
            bar.Size = new Size(200, 3); bar.Location = new Point(80, 160);
            bar.BackColor = eva01 ? Color.FromArgb(42, 24, 64)
                : eva00 ? Color.FromArgb(22, 50, 77)
                : eva02 ? Color.FromArgb(58, 16, 18)
                : glass ? Color.FromArgb(61, 72, 111)
                : pale ? Color.FromArgb(229, 229, 234)
                : Color.FromArgb(60, 60, 60);
            var fill = new Panel();
            fill.Size = new Size(0, 3); fill.Location = new Point(0, 0);
            fill.BackColor = eva01 ? Color.FromArgb(185, 255, 61)
                : eva00 ? Color.FromArgb(255, 201, 74)
                : eva02 ? Color.FromArgb(255, 159, 26)
                : glass ? Color.FromArgb(98, 197, 255)
                : Color.FromArgb(9, 105, 218);
            bar.Controls.Add(fill);

            var pct = new Label();
            pct.Text = "0%";
            pct.ForeColor = eva01 ? Color.FromArgb(185, 172, 214)
                : eva00 ? Color.FromArgb(169, 196, 220)
                : eva02 ? Color.FromArgb(223, 174, 159)
                : glass ? Color.FromArgb(196, 218, 248)
                : pale ? Color.FromArgb(142, 142, 147)
                : Color.FromArgb(140, 140, 140);
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
    // 缓存迁移进度窗口：跨盘迁移 740MB/18 万文件时，避免用户以为程序没启动
    // ============================================================
    public class CacheMigrationForm : Form
    {
        public CacheMigrationForm(string targetPath)
        {
            Text = "正在迁移缓存";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(500, 190);
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            BackColor = Color.FromArgb(43, 43, 43);

            var title = new Label();
            title.Text = "正在迁移缓存，请稍候…";
            title.ForeColor = Color.FromArgb(230, 230, 230);
            title.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            title.AutoSize = false;
            title.Size = new Size(460, 30);
            title.Location = new Point(20, 25);

            var sub = new Label();
            sub.Text = "缓存文件较多，首次跨盘迁移可能需要几分钟。请勿重复启动程序。";
            sub.ForeColor = Color.FromArgb(160, 160, 160);
            sub.Font = new Font("Microsoft YaHei UI", 9f);
            sub.AutoSize = false;
            sub.Size = new Size(460, 22);
            sub.Location = new Point(20, 60);

            var target = new Label();
            target.Text = "目标：" + targetPath;
            target.ForeColor = Color.FromArgb(140, 140, 140);
            target.Font = new Font("Microsoft YaHei UI", 8.5f);
            target.AutoSize = false;
            target.AutoEllipsis = true;
            target.Size = new Size(460, 20);
            target.Location = new Point(20, 88);

            var bar = new ProgressBar();
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 30;
            bar.Size = new Size(460, 14);
            bar.Location = new Point(20, 120);

            Controls.Add(title);
            Controls.Add(sub);
            Controls.Add(target);
            Controls.Add(bar);
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
    // 关于窗口：跟随当前皮肤绘制。黑金使用墨黑底 + 金色浪纹，
    // 其它皮肤使用对应的底色/强调色；内容、链接和关闭行为保持不变。
    // ============================================================
    sealed class ThemedAboutForm : Form
    {
        sealed class Palette
        {
            public Color Back;
            public Color Panel;
            public Color Text;
            public Color Muted;
            public Color Accent;
            public Color Border;
            public bool Wave;
        }

        string _appTitle;
        string _version;
        string _creator;
        string _body;
        string _url;
        readonly Palette _p;
        Image _bg;
        Rectangle _closeRect = Rectangle.Empty;
        Rectangle _linkRect = Rectangle.Empty;
        bool _hoverClose;
        bool _hoverLink;

        public ThemedAboutForm(string skin, string displayName, string aboutText)
        {
            _p = PaletteFor(skin);
            ParseText(aboutText, displayName);

            Text = "关于 " + displayName;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(640, 462);
            BackColor = _p.Back;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            if (_p.Wave)
            {
                try
                {
                    string bg = Path.Combine(Application.StartupPath, "skins", "black-gold-bg.jpg");
                    if (File.Exists(bg)) _bg = Image.FromFile(bg);
                }
                catch { _bg = null; }
            }
        }

        static Palette PaletteFor(string skin)
        {
            Palette p = new Palette();
            skin = String.IsNullOrEmpty(skin) ? "dark" : skin.Trim().ToLowerInvariant();
            if (skin == "light")
            {
                p.Back = Color.FromArgb(244, 243, 239); p.Panel = Color.FromArgb(255, 255, 255);
                p.Text = Color.FromArgb(32, 30, 27); p.Muted = Color.FromArgb(108, 101, 90);
                p.Accent = Color.FromArgb(151, 105, 43); p.Border = Color.FromArgb(198, 183, 160); p.Wave = false;
            }
            else if (skin == "frost")
            {
                p.Back = Color.FromArgb(248, 246, 250); p.Panel = Color.FromArgb(255, 255, 255);
                p.Text = Color.FromArgb(37, 32, 45); p.Muted = Color.FromArgb(118, 108, 132);
                p.Accent = Color.FromArgb(132, 92, 164); p.Border = Color.FromArgb(214, 204, 228); p.Wave = false;
            }
            else if (skin == "aurora")
            {
                p.Back = Color.FromArgb(233, 246, 249); p.Panel = Color.FromArgb(250, 254, 255);
                p.Text = Color.FromArgb(25, 42, 51); p.Muted = Color.FromArgb(88, 111, 120);
                p.Accent = Color.FromArgb(0, 135, 151); p.Border = Color.FromArgb(164, 203, 211); p.Wave = false;
            }
            else if (skin == "smoke")
            {
                p.Back = Color.FromArgb(11, 21, 35); p.Panel = Color.FromArgb(15, 29, 47);
                p.Text = Color.FromArgb(238, 245, 255); p.Muted = Color.FromArgb(166, 185, 207);
                p.Accent = Color.FromArgb(122, 181, 255); p.Border = Color.FromArgb(62, 87, 118); p.Wave = false;
            }
            else if (skin == "eva01")
            {
                p.Back = Color.FromArgb(16, 9, 28); p.Panel = Color.FromArgb(25, 15, 42);
                p.Text = Color.FromArgb(241, 235, 255); p.Muted = Color.FromArgb(183, 168, 207);
                p.Accent = Color.FromArgb(185, 255, 61); p.Border = Color.FromArgb(112, 86, 145); p.Wave = false;
            }
            else if (skin == "eva00")
            {
                p.Back = Color.FromArgb(7, 17, 29); p.Panel = Color.FromArgb(11, 27, 44);
                p.Text = Color.FromArgb(235, 245, 255); p.Muted = Color.FromArgb(160, 185, 208);
                p.Accent = Color.FromArgb(106, 176, 255); p.Border = Color.FromArgb(55, 83, 112); p.Wave = false;
            }
            else if (skin == "eva02")
            {
                p.Back = Color.FromArgb(22, 7, 8); p.Panel = Color.FromArgb(34, 10, 12);
                p.Text = Color.FromArgb(255, 240, 235); p.Muted = Color.FromArgb(210, 172, 159);
                p.Accent = Color.FromArgb(255, 106, 61); p.Border = Color.FromArgb(125, 61, 47); p.Wave = false;
            }
            else if (skin == "blackgold")
            {
                p.Back = Color.FromArgb(5, 4, 3); p.Panel = Color.FromArgb(10, 8, 5);
                p.Text = Color.FromArgb(242, 232, 207); p.Muted = Color.FromArgb(185, 167, 127);
                p.Accent = Color.FromArgb(232, 200, 120); p.Border = Color.FromArgb(201, 164, 93); p.Wave = true;
            }
            else
            {
                p.Back = Color.FromArgb(28, 28, 30); p.Panel = Color.FromArgb(36, 36, 40);
                p.Text = Color.FromArgb(232, 232, 235); p.Muted = Color.FromArgb(156, 156, 164);
                p.Accent = Color.FromArgb(124, 170, 255); p.Border = Color.FromArgb(72, 72, 80); p.Wave = false;
            }
            return p;
        }

        void ParseText(string text, string displayName)
        {
            text = String.IsNullOrEmpty(text) ? "" : text.Replace("\r", "");
            string[] lines = text.Split('\n');
            _appTitle = (lines.Length > 0 && lines[0].Trim().Length > 0) ? lines[0].Trim() : displayName;
            _version = lines.Length > 1 ? lines[1].Trim() : "";
            _creator = "";
            _url = "";
            StringBuilder body = new StringBuilder();
            bool inBody = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (!inBody && line.StartsWith("制作者", StringComparison.Ordinal))
                {
                    _creator = line;
                    continue;
                }
                if (!inBody && line.StartsWith("GitHub", StringComparison.OrdinalIgnoreCase))
                {
                    _url = line;
                    continue;
                }
                if (line.StartsWith("免责声明", StringComparison.Ordinal))
                {
                    inBody = true;
                    continue;
                }
                if (!inBody) continue;
                if (line.StartsWith("GitHub", StringComparison.OrdinalIgnoreCase))
                {
                    _url = line;
                    break;
                }
                if (body.Length > 0) body.Append('\n');
                body.Append(line);
            }
            _body = body.ToString();
            if (_body.Length == 0)
            {
                // 结构意外变化时仍然把完整内容呈现出来，不丢免责声明。
                _body = text;
            }
        }

        static string UrlPart(string raw)
        {
            if (String.IsNullOrEmpty(raw)) return "";
            int idx = raw.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? raw.Substring(idx) : raw;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 整个背景在 OnPaint 中一次画完，避免先刷灰底再盖图。
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;

            if (_bg != null)
            {
                g.DrawImage(_bg, new Rectangle(0, 0, w, h));
                using (LinearGradientBrush veil = new LinearGradientBrush(
                    new Rectangle(0, 0, w, h),
                    Color.FromArgb(150, _p.Back), Color.FromArgb(222, _p.Back), 90f))
                    g.FillRectangle(veil, 0, 0, w, h);
            }
            else
            {
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new Rectangle(0, 0, w, h),
                    Color.FromArgb(255, _p.Panel), Color.FromArgb(255, _p.Back), 90f))
                    g.FillRectangle(bg, 0, 0, w, h);
            }

            using (Pen border = new Pen(_p.Border, 1))
                g.DrawRectangle(border, 0, 0, w - 1, h - 1);

            // 标题栏
            using (SolidBrush bar = new SolidBrush(Color.FromArgb(_p.Wave ? 236 : 255, _p.Panel)))
                g.FillRectangle(bar, 1, 1, w - 2, 47);
            using (Pen line = new Pen(_p.Border, 1))
                g.DrawLine(line, 1, 48, w - 2, 48);
            using (Font titleFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold))
            using (SolidBrush titleBrush = new SolidBrush(_p.Accent))
                g.DrawString("关于 " + AppModule.DisplayName, titleFont, titleBrush, 22, 16);

            _closeRect = new Rectangle(w - 45, 11, 29, 26);
            using (SolidBrush closeBg = new SolidBrush(_hoverClose ? Color.FromArgb(32, _p.Accent) : Color.Transparent))
                g.FillRectangle(closeBg, _closeRect);
            using (Pen closePen = new Pen(_hoverClose ? _p.Accent : _p.Border, 1))
                g.DrawRectangle(closePen, _closeRect);
            using (Font closeFont = new Font("Microsoft YaHei UI", 10f))
            using (SolidBrush closeBrush = new SolidBrush(_hoverClose ? _p.Accent : _p.Muted))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("✕", closeFont, closeBrush, _closeRect, sf);
                sf.Dispose();
            }

            // 内容
            int left = 30;
            using (Font appFont = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold))
            using (SolidBrush appBrush = new SolidBrush(_p.Text))
                g.DrawString(_appTitle, appFont, appBrush, left, 63);
            if (_version.Length > 0)
            {
                using (Font small = new Font("Microsoft YaHei UI", 9.5f))
                using (SolidBrush muted = new SolidBrush(_p.Muted))
                    g.DrawString(_version, small, muted, left, 101);
            }
            if (_creator.Length > 0)
            {
                using (Font small = new Font("Microsoft YaHei UI", 9.5f))
                using (SolidBrush text = new SolidBrush(_p.Text))
                    g.DrawString(_creator, small, text, left, 126);
            }
            using (Pen div = new Pen(_p.Border, 1))
                g.DrawLine(div, left, 154, w - 30, 154);

            using (Font head = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
            using (SolidBrush accent = new SolidBrush(_p.Accent))
                g.DrawString("免责声明", head, accent, left, 168);
            using (Font bodyFont = new Font("Microsoft YaHei UI", 9f))
            using (SolidBrush bodyBrush = new SolidBrush(_p.Text))
            {
                Rectangle bodyRect = new Rectangle(left, 193, w - 60, 155);
                StringFormat bodyFormat = new StringFormat();
                bodyFormat.Trimming = StringTrimming.None;
                bodyFormat.FormatFlags = StringFormatFlags.LineLimit;
                g.DrawString(_body, bodyFont, bodyBrush, bodyRect, bodyFormat);
                bodyFormat.Dispose();
            }

            string link = UrlPart(_url);
            if (link.Length > 0)
            {
                using (Font labelFont = new Font("Microsoft YaHei UI", 8.5f))
                using (SolidBrush labelBrush = new SolidBrush(_p.Muted))
                    g.DrawString("GitHub", labelFont, labelBrush, left, 358);
                using (Font linkFont = new Font("Microsoft YaHei UI", 9.2f))
                using (SolidBrush linkBrush = new SolidBrush(_hoverLink ? _p.Accent : _p.Muted))
                {
                    SizeF size = g.MeasureString(link, linkFont);
                    _linkRect = new Rectangle(left, 377, (int)Math.Ceiling(size.Width), 22);
                    g.DrawString(link, linkFont, linkBrush, left, 377);
                    if (_hoverLink)
                        using (Pen underline = new Pen(_p.Accent, 1))
                            g.DrawLine(underline, left, 396, left + Math.Min((int)size.Width, w - 60), 396);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool overClose = _closeRect.Contains(e.Location);
            bool overLink = _linkRect.Contains(e.Location);
            if (overClose != _hoverClose || overLink != _hoverLink)
            {
                _hoverClose = overClose;
                _hoverLink = overLink;
                Invalidate();
            }
            Cursor = (overClose || overLink) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_closeRect.Contains(e.Location)) { Close(); return; }
            if (_linkRect.Contains(e.Location)) { OpenLink(); return; }
            if (e.Y < 48)
            {
                try
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
                }
                catch { }
            }
        }

        void OpenLink()
        {
            string url = UrlPart(_url);
            if (url.Length == 0) return;
            try { Process.Start(url); } catch { }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _bg != null)
            {
                try { _bg.Dispose(); } catch { }
                _bg = null;
            }
            base.Dispose(disposing);
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
        // 修改缓存位置后，等旧窗口彻底关闭，再由 Program 启动新实例完成迁移。
        public static bool RestartAfterCacheMove = false;
        public static string RestartMode = "album";
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE = 0xF020;
        // v2.33：WebView2 透明页不能用 TransparencyKey（会把整层鼠标命中测试跳过），
        // 改用整窗 LWA_ALPHA 半透明；页面仍透明，窗口可正常点击。
        // IBT_AURORA_GLASS=0 可退回完全不透明的液态玻璃（仍可点击）。
        // v2.38 起液态玻璃使用固定磨砂玻璃底图，页面完全不透明，无需桌面抓屏，
        // 也不再启用整窗 alpha / 鼠标桥接；窗口按普通不透明窗口呈现。
        static readonly bool AuroraWindowGlass = false;
        int auroraAlpha = AppModule.IsAlbum ? 244 : 238;
        bool webPageReady = false;
        volatile bool allowMinimizeOnce = false;
        WebView2 web;
        WebView2 dedupWeb;
        AlbumMeta albMeta;
        Form dedupForm;
        GlassBackdrop mainGlass;
        GlassMouseBridge glassBridge;
        bool glassBridgeReady;
        GlassBackdrop dedupGlass;
        System.Windows.Forms.Timer skinSyncTimer;
        System.Windows.Forms.Timer skinFadeTimer;
        bool skinFadeClosing;
        bool dedupFadeClosing;
        volatile string lastSkinSync = "";
        CoreWebView2Environment webEnv;
        public SplashForm Splash;
        public string StartupMode = AppModule.Id;
        // v2.41：从相册右键启动图片编辑时，通过命令行把选中图片带入独立编辑器。
        public string[] StartupPaths = new string[0];
        bool startupPathsApplied = false;
        readonly List<string> files = new List<string>();
        readonly HashSet<string> fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> lutFiles = new List<string>();
        // 字符串参数（输出命名模板等，double 字典放不下）
        readonly Dictionary<string, string> parS = new Dictionary<string, string>();
        // 批量暂停 / 中止 / 失败清单
        volatile bool pauseFlag = false;
        volatile bool stopFlag = false;
        readonly List<string[]> failList = new List<string[]>();
        volatile bool running = false;
        int selected = -1;
        volatile bool genEditInstalling = false;
        volatile string genEditSource = "";
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
        Dictionary<string, Dictionary<string, double>> batchParams = new Dictionary<string, Dictionary<string, double>>();

        // 预览防抖：拖滑块时取消上一次未完成的渲染
        int previewToken = 0;
        // D3D11 immediate context 不是线程安全的：预览线程与导出线程会同时走 GPU，
        // 并发进入原生 gpu_* 函数会导致 AccessViolation（进程直接崩溃）。
        static readonly object GpuLock = new object();
        // QA/诊断用：--force-cpu-color / IBT_FORCE_CPU_COLOR=1 跳过 GPU 色彩路径；
        // --force-csharp-color 继续跳过 imgproc.dll，验证最底层 C# 兜底。
        // 正常用户不会触发。
        static readonly bool ForceCpuColor = HasForceColorArg("--force-cpu-color", "IBT_FORCE_CPU_COLOR");
        static readonly bool ForceCSharpColor = HasForceColorArg("--force-csharp-color", "IBT_FORCE_CSHARP_COLOR");
        static bool HasForceColorArg(string argName, string envName)
        {
            if (Environment.GetEnvironmentVariable(envName) == "1") return true;
            foreach (string a in Environment.GetCommandLineArgs())
                if (a == argName) return true;
            return false;
        }
        // 预览共享 cachedBase/cachedSmall，多个 QueuePreview 任务不能并行执行。
        readonly object PreviewLock = new object();
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
                string name = AppModule.IconFile;
                string p = Path.Combine(Application.StartupPath, name);
                if (File.Exists(p)) Icon = new System.Drawing.Icon(p);
            }
            catch { }
        }

        public MainForm()
        {
            try { CacheStore.ApplyPendingMove(); }
            catch (Exception ex)
            {
                try { Log("缓存位置迁移失败: " + ex.Message); } catch { }
                try { MessageBox.Show("缓存位置迁移失败，仍继续使用原缓存位置：\n" + ex.Message, "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            }
            Text = AppModule.WindowTitle;
            try { albMeta = new AlbumMeta(Path.Combine(Application.StartupPath, "album_meta.json")); } catch { }
            try
            {
                string dtf = Path.Combine(Application.StartupPath, "dup_threshold.txt");
                if (File.Exists(dtf)) dupThreshold = int.Parse(File.ReadAllText(dtf).Trim());
            }
            catch { }
            Width = 1400; Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            string startupSkin = "dark";
            try { startupSkin = ReadSkinName(); } catch { }
            // 透明色键 + 分层窗口与 Opacity/DoubleBuffer 混用会丢掉色键合成。
            if (startupSkin == "aurora") DoubleBuffered = false;
            if (startupSkin != "aurora") Opacity = 0;
            // v2.38：液态玻璃改用固定磨砂玻璃贴图，窗口保持普通不透明合成，
            // 不再抓取桌面、不再推送 glassbg 帧，因此也不会出现底图帧率与合成卡顿。
            BackColor = startupSkin == "aurora" ? Color.FromArgb(214, 230, 243) : OpaqueBackFor(startupSkin);
            TransparencyKey = Color.Empty;
            auroraAlpha = AppModule.IsAlbum ? 244 : 238;

            if (AppModule.IsAlbum) { try { MigrateLegacyCache(); } catch {} }
            web = new WebView2();
            web.Dock = DockStyle.Fill;
            Controls.Add(web);
            mainGlass = new GlassBackdrop(this, web);
            // 静态底图模式不再预抓桌面；GlassBackdrop 保留仅为兼容旧配置，不进入 Enable 状态。
            try { if (startupSkin != "aurora") mainGlass.PrimeDesktop(); } catch { }
            try { MainForm.Log("构造 startupSkin=" + startupSkin); } catch { }
            // WebView2 的透明合成必须等窗体真正显示后再初始化；在构造函数里提前初始化时，
            // DWM 毛玻璃与 WebView2 的 DirectComposition 会互相顶掉，页面会整层不可见。
            this.Shown += (s, e) => { try { if (web != null && web.CoreWebView2 == null) InitWeb(); } catch { } };

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
            // v2.19 编辑器扩展
            par["curve_on"]=0; par["curve_m0"]=0; par["curve_m1"]=25; par["curve_m2"]=50; par["curve_m3"]=75; par["curve_m4"]=100;
            par["curve_r0"]=0; par["curve_r1"]=25; par["curve_r2"]=50; par["curve_r3"]=75; par["curve_r4"]=100;
            par["curve_g0"]=0; par["curve_g1"]=25; par["curve_g2"]=50; par["curve_g3"]=75; par["curve_g4"]=100;
            par["curve_b0"]=0; par["curve_b1"]=25; par["curve_b2"]=50; par["curve_b3"]=75; par["curve_b4"]=100;
            par["hsl_on"]=0;
            for (int hi = 0; hi < 6; hi++) { par["hsl_s"+hi]=0; par["hsl_l"+hi]=0; }
            par["lens_on"]=0; par["lens_dist"]=0; par["lens_ca"]=0; par["lens_vig"]=0;
            par["level_on"]=0; par["level_angle"]=0;


            if (!AppModule.IsAlbum)
            {
                LoadLuts(Path.Combine(Application.StartupPath, "luts"));
                MigrateRefLuts();
                LoadLuts(Path.Combine(Application.StartupPath, "luts"));
            }

            this.Load += (s, e) => { ApplyNativeSkin(ReadSkinName()); ApplyRoundedCorner(); };
            this.Resize += (s, e) => ApplyRoundedCorner();
            this.Move += (s, e) => { if (glassBridge != null && glassBridge.Visible) glassBridge.Sync(); };
            this.Activated += (s, e) => ShowGlassBridge();
            this.Deactivate += (s, e) => HideGlassBridge();
            this.FormClosed += (s, e) => {
                try { if (glassBridge != null) { glassBridge.Close(); glassBridge.Dispose(); glassBridge = null; } } catch {}
                try { if (mainGlass != null) { mainGlass.Dispose(); mainGlass = null; } } catch {}
                try { if (skinSyncTimer != null) { skinSyncTimer.Stop(); skinSyncTimer.Dispose(); skinSyncTimer = null; } } catch {}
            };
            if (AppModule.IsAlbum) this.FormClosed += (s, e) => CloseDupWindow();
        }

        // Windows 任务栏点击已激活窗口的图标默认会发送 SC_MINIMIZE。
        // 这里只拦截任务栏触发的这一下，让它保持在前台；标题栏的最小化按钮仍可正常最小化。
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && ((int)(m.WParam.ToInt64() & 0xFFF0)) == SC_MINIMIZE && !allowMinimizeOnce)
            {
                try
                {
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Show();
                    Activate();
                }
                catch { }
                return;
            }
            base.WndProc(ref m);
        }

        void ShowGlassBridge()
        {
            try
            {
                if (!AuroraWindowGlass || !glassBridgeReady) return;
                if (NormalizeSkin(ReadSkinName()) != "aurora") { HideGlassBridge(); return; }
                if (!Visible || WindowState == FormWindowState.Minimized) return;
                if (glassBridge == null) glassBridge = new GlassMouseBridge(this);
                glassBridge.Sync();
                if (!glassBridge.Visible) glassBridge.Show();
            }
            catch { }
        }

        void HideGlassBridge()
        {
            try { if (glassBridge != null && glassBridge.Visible) glassBridge.Hide(); } catch { }
        }

        void MinimizeFromUi()
        {
            try
            {
                allowMinimizeOnce = true;
                WindowState = FormWindowState.Minimized;
            }
            catch { }
            finally { allowMinimizeOnce = false; }
        }

        void ApplyRoundedCorner()
        {
            try
            {
                if (WindowState == FormWindowState.Maximized)
                {
                    this.Region = null;
                    return;
                }
                if (ReadSkinName() == "aurora")
                {
                    // v2.34：液态玻璃改用不透明合成，不能再跳过 GDI 裁剪；
                    // DWM 在分层/透明窗口上不会稳定给圆角，这里直接裁窗口区域。
                    int radius = 14;
                    if (Width <= radius * 2 || Height <= radius * 2) return;
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
                        path.AddArc(Width - radius * 2, 0, radius * 2, radius * 2, 270, 90);
                        path.AddArc(Width - radius * 2, Height - radius * 2, radius * 2, radius * 2, 0, 90);
                        path.AddArc(0, Height - radius * 2, radius * 2, radius * 2, 90, 90);
                        path.CloseAllFigures();
                        var old = this.Region;
                        this.Region = new Region(path);
                        if (old != null) old.Dispose();
                    }
                    return;
                }
                // 其它皮肤继续交给 DWM 绘制系统圆角。
                this.Region = null;
                int attr = 2;
                Native.DwmSetWindowAttribute(this.Handle, 33, ref attr, sizeof(int));
            }
            catch { }
        }

        async void InitWeb()
        {
            try
            {
                // 必须在创建 CoreWebView2Environment 之前设置，WebView2 才会真正画出透明底。
                try { Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0"); } catch { }
                string webData = CacheSub("webview2");
                CoreWebView2Environment env = null;
                try
                {
                    CoreWebView2EnvironmentOptions envOpts = null;
                    string debugPort = "";
                    foreach (string arg in Environment.GetCommandLineArgs())
                    {
                        if (arg == "--debug-webview") debugPort = "9222";
                    }
                    if (!string.IsNullOrEmpty(debugPort))
                    {
                        envOpts = new CoreWebView2EnvironmentOptions();
                        envOpts.AdditionalBrowserArguments = "--remote-debugging-port=" + debugPort;
                    }
                    env = await CoreWebView2Environment.CreateAsync(null, webData, envOpts);
                }
                catch (Exception ex)
                {
                    Log("WebView2 环境创建失败: " + ex.Message);
                }
                webEnv = env;
                if (env != null) await web.EnsureCoreWebView2Async(env);
                else await web.EnsureCoreWebView2Async(null);
                int tries = 0;
                while (web.CoreWebView2 == null && tries < 20) { await Task.Delay(100); tries++; }
                if (web.CoreWebView2 == null) { MessageBox.Show("WebView2 初始化超时"); return; }
                // 关键：透明背景必须在首次 Navigate 之前写入，否则旧导航留下的不透明表面不会重绘。
                ApplyNativeSkin(ReadSkinName(), this, web);
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
                // 相册缩略图走本地虚拟站点；图片编辑版不需要相册缩略图站点。
                if (AppModule.IsAlbum)
                {
                    try
                    {
                        string thumbDir = AlbThumbDir();
                        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "thumb.local", thumbDir, CoreWebView2HostResourceAccessKind.Allow);
                        thumbHostReady = true;
                    }
                    catch (Exception ex) { Log("缩略图虚拟站点创建失败: " + ex.Message); }
                }
                web.CoreWebView2.WebMessageReceived += Web_Message;
                web.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    Log("页面加载完成 success=" + e.IsSuccess + " url=" + web.Source);
                    // 兜底：即使页面没有发出 ready，也不能让窗口一直停在 Opacity=0
                    await Task.Delay(1800);
                    try { if (Opacity < 1) { Opacity = 1; Show(); Activate(); } } catch { }
                };
                string skin = ReadSkinName();
                if (Environment.GetEnvironmentVariable("IBT_BLANK") == "1") { web.CoreWebView2.NavigateToString("<!doctype html><html><head><style>html,body{background:transparent!important;margin:0;height:100%}</style></head><body></body></html>"); return; }
                // 各模块加载自己的入口页面，不再通过同一个 ui.html 切模式。
                web.CoreWebView2.Navigate("http://app.local/" + AppModule.HtmlFile + "?skin=" + skin + "&mode=" + AppModule.Id + "&standalone=" + AppModule.Id + "&v=" + DateTime.Now.Ticks);
                StartSkinSync();
                if (!AppModule.IsAlbum) SendState();
            }
            catch (Exception ex) { MessageBox.Show("WebView2 初始化失败：\n" + ex.Message); }
        }

        void Send(string json)
        {
            PostToWeb(web, json);
            // 去重窗口接收去重消息和皮肤同步消息，避免预览等大消息重复传输。
            if (json != null && (json.StartsWith("{\"type\":\"dup", StringComparison.Ordinal) ||
                json.StartsWith("{\"type\":\"skin\"", StringComparison.Ordinal)))
            {
                string dupJson = json;
                if (json.StartsWith("{\"type\":\"skin\"", StringComparison.Ordinal) && json.Contains("\"aurora\""))
                    dupJson = "{\"type\":\"skin\",\"skin\":\"dark\"}";
                PostToWeb(dedupWeb, dupJson);
            }
        }

        string ReadSkinName()
        {
            try {
                string shared = AppModule.SharedSkinFile();
                if (File.Exists(shared)) {
                    string s = File.ReadAllText(shared).Trim().ToLowerInvariant();
                    if (s.Length > 0) return NormalizeSkin(s);
                }
            } catch {}
            try {
                string sf = Path.Combine(Application.StartupPath, "skin.txt");
                if (File.Exists(sf)) {
                    string s = File.ReadAllText(sf).Trim().ToLowerInvariant();
                    if (s.Length > 0) return NormalizeSkin(s);
                }
            } catch {}
            return "dark";
        }

        // 独立进程之间通过 skin.txt 同步：任一窗口切换后，另一窗口 0.7 秒内跟随。
        void StartSkinSync()
        {
            if (skinSyncTimer != null) return;
            lastSkinSync = ReadSkinName();
            skinSyncTimer = new System.Windows.Forms.Timer();
            skinSyncTimer.Interval = 700;
            skinSyncTimer.Tick += (s, e) =>
            {
                try {
                    string cur = ReadSkinName();
                    if (!string.Equals(cur, lastSkinSync, StringComparison.OrdinalIgnoreCase)) {
                        lastSkinSync = cur;
                        ApplyNativeSkin(cur);
                        if (dedupForm != null && !dedupForm.IsDisposed) ApplyNativeSkin(cur, dedupForm, dedupWeb);
                        Send("{\"type\":\"skin\",\"skin\":" + JsonStr(cur) + "}");
                    }
                } catch {}
            };
            skinSyncTimer.Start();
        }

        static string NormalizeSkin(string skin)
        {
            if (skin == null) return "dark";
            string s = skin.Trim().ToLowerInvariant();
            if (s == "liquid" || s == "glass" || s == "liquid-glass" || s == "liquidglass"
                || s == "old-liquid" || s == "dark-glass" || s == "ink") return "smoke";
            if (s == "liquid2" || s == "new-liquid" || s == "realtime-liquid" || s == "clear-glass") return "aurora";
            if (s == "black-gold" || s == "gold" || s == "gold-black") return "blackgold";
            if (s == "eva-01" || s == "unit01" || s == "unit-01") return "eva01";
            if (s == "eva-00" || s == "unit00" || s == "unit-00") return "eva00";
            if (s == "eva-02" || s == "unit02" || s == "unit-02") return "eva02";
            if (s == "frostwhite" || s == "frosted-white") return "frost";
            if (s == "white") return "light";
            if (s == "black") return "dark";
            return s.Length == 0 ? "dark" : s;
        }

        Color OpaqueBackFor(string skin)
        {
            if (skin == "light") return Color.FromArgb(245, 245, 247);
            if (skin == "frost") return Color.FromArgb(246, 244, 250);
            if (skin == "blackgold") return Color.FromArgb(5, 4, 3);
            if (skin == "eva00") return Color.FromArgb(7, 17, 29);
            if (skin == "eva02") return Color.FromArgb(16, 5, 7);
            if (skin == "eva01") return Color.FromArgb(7, 5, 13);
            return Color.FromArgb(32, 32, 36);
        }

        void ApplyNativeSkin(string skin)
        {
            ApplyNativeSkin(NormalizeSkin(skin), this, web);
        }

        // v2.34 液态玻璃改为“窗口内桌面模糊底图”：窗口本身保持不透明，
        // 页面背景是模糊桌面，照片/缩略图因此不会再被整窗 alpha 变透明。
        int AuroraAlphaFor(Form targetForm)
        {
            return 255;
        }

        void SetAuroraAlpha(int alpha)
        {
            auroraAlpha = Math.Max(225, Math.Min(255, alpha));
            // 页面里旧的 glassalpha 消息只保留协议兼容，不再改变窗口透明度。
            try { Log("Aurora glassalpha 忽略（模糊底图模式） alpha=" + auroraAlpha); } catch { }
        }

        void ApplyNativeSkin(string skin, Form targetForm, WebView2 targetWeb)
        {
            if (targetForm == null || targetForm.IsDisposed) return;
            skin = NormalizeSkin(skin);
            // 去重窗口是独立顶层窗口，没有玻璃鼠标桥接层；液态玻璃的透明页会让它既难读也点不中，
            // 因此去重窗口统一用不透明的深色皮肤。
            if (targetForm == dedupForm && skin == "aurora") skin = "dark";
            try
            {
                if (!targetForm.IsHandleCreated) return;
                bool aurora = skin == "aurora";
                try { Log("原生皮肤=" + skin + " aurora=" + aurora + " key=" + targetForm.TransparencyKey + " back=" + targetForm.BackColor + " op=" + targetForm.Opacity); } catch { }
                if (aurora)
                {
                    // v2.38：固定磨砂玻璃底图。窗口使用与页面同色系的浅蓝实底，
                    // 页面加载后直接显示 skins/aurora-bg.jpg；不再抓屏、不推帧。
                    GlassBackdrop glass0 = targetForm == this ? mainGlass : (targetForm == dedupForm ? dedupGlass : null);
                    targetForm.TransparencyKey = Color.Empty;
                    targetForm.BackColor = Color.FromArgb(214, 230, 243);
                    targetForm.Opacity = 1;
                    try { Native.ClearLayered(targetForm.Handle); } catch { }
                    try
                    {
                        var margins = new Native.MARGINS { Left = 0, Top = 0, Right = 0, Bottom = 0 };
                        Native.DwmExtendFrameIntoClientArea(targetForm.Handle, ref margins);
                        int none = 1;
                        Native.DwmSetWindowAttribute(targetForm.Handle, 38, ref none, sizeof(int));
                    }
                    catch { }
                    if (targetWeb != null)
                    {
                        // 页面自身是不透明静态底图，WebView 不能透明，否则会先露出窗口底色。
                        targetWeb.DefaultBackgroundColor = Color.FromArgb(214, 230, 243);
                    }
                    if (glass0 != null)
                    {
                        try { glass0.ClearFrame(); } catch { }
                        try { glass0.Disable(); } catch { }
                    }
                    bool webReady = webPageReady && targetWeb != null && targetWeb.CoreWebView2 != null;
                    try { Log("Aurora 静态磨砂底图模式 key=" + targetForm.TransparencyKey + " alpha=255 webReady=" + webReady + " glass=disabled"); } catch { }
                }
                else
                {
                    GlassBackdrop glass = targetForm == this ? mainGlass : (targetForm == dedupForm ? dedupGlass : null);
                    if (glass != null) glass.Disable();
                    targetForm.TransparencyKey = Color.Empty;
                    targetForm.BackColor = OpaqueBackFor(skin);
                    targetForm.Opacity = 1;
                    var margins = new Native.MARGINS { Left = 0, Top = 0, Right = 0, Bottom = 0 };
                    try { Native.DwmExtendFrameIntoClientArea(targetForm.Handle, ref margins); } catch { }
                    try { int v = 1; Native.DwmSetWindowAttribute(targetForm.Handle, 38, ref v, sizeof(int)); } catch { }
                    try { int v = 2; Native.DwmSetWindowAttribute(targetForm.Handle, 33, ref v, sizeof(int)); } catch { }
                    if (targetWeb != null)
                        targetWeb.DefaultBackgroundColor = skin == "light" ? Color.FromArgb(255, 255, 255) : skin == "frost" ? Color.FromArgb(250, 250, 252) : OpaqueBackFor(skin);
                    Native.SetAlpha(targetForm.Handle, 255);
                }
                targetForm.Invalidate(true);
                if (targetForm == this) { try { ApplyRoundedCorner(); } catch { } }
            }
            catch (Exception ex) { try { Log("应用原生皮肤失败 " + skin + ": " + ex.Message); } catch { } }
        }
        // 黑金专属窗口开关过渡：黑金是唯一不透明实体装甲主题，整窗淡入/淡出最自然；
        // 其他皮肤保持原有行为，不做整窗动画，避免影响液态玻璃 / 浅色皮肤。
        void BeginBlackGoldOpenFade(string skin)
        {
            if (NormalizeSkin(skin) != "blackgold") { if (Opacity < 1) Opacity = 1; return; }
            if (skinFadeTimer != null) { try { skinFadeTimer.Stop(); skinFadeTimer.Dispose(); } catch { } skinFadeTimer = null; }
            skinFadeClosing = false;
            Opacity = 0;
            int step = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 16 };
            skinFadeTimer = timer;
            timer.Tick += (s, e) =>
            {
                if (IsDisposed || skinFadeClosing) { timer.Stop(); timer.Dispose(); return; }
                step++;
                try { Opacity = Math.Min(1.0, step / 16.0); } catch { }
                if (step >= 16) { timer.Stop(); timer.Dispose(); if (ReferenceEquals(skinFadeTimer, timer)) skinFadeTimer = null; }
            };
            timer.Start();
        }

        void CloseWithBlackGoldFade()
        {
            if (skinFadeClosing) return;
            if (NormalizeSkin(ReadSkinName()) != "blackgold") { Close(); return; }
            if (skinFadeTimer != null) { try { skinFadeTimer.Stop(); skinFadeTimer.Dispose(); } catch { } skinFadeTimer = null; }
            skinFadeClosing = true;
            int step = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 16 };
            skinFadeTimer = timer;
            timer.Tick += (s, e) =>
            {
                if (IsDisposed) { timer.Stop(); timer.Dispose(); return; }
                step++;
                try { Opacity = Math.Max(0.0, 1.0 - step / 14.0); } catch { }
                if (step >= 14)
                {
                    timer.Stop(); timer.Dispose();
                    if (ReferenceEquals(skinFadeTimer, timer)) skinFadeTimer = null;
                    try { Close(); } catch { }
                }
            };
            timer.Start();
        }

        static void PostToWeb(WebView2 target, string json)
        {
            if (target == null || json == null) return;
            try {
                if (target.InvokeRequired)
                    target.Invoke((Action)(() => { try { if (target.CoreWebView2 != null) target.CoreWebView2.PostWebMessageAsJson(json); } catch {} }));
                else if (target.CoreWebView2 != null)
                    target.CoreWebView2.PostWebMessageAsJson(json);
            } catch {}
        }

        // ---- v2.28 本地生成式修图（FLUX.2 Klein 4B / stable-diffusion.cpp）----
        string GenEditRuntimeExe()
        {
            return Path.Combine(Application.StartupPath, "ai", "genedit", "sd-cli.exe");
        }

        string GenEditModelDir()
        {
            return Path.Combine(Application.StartupPath, "ai", "models", "genedit");
        }

        string GenEditModelPath(string name)
        {
            return Path.Combine(GenEditModelDir(), name);
        }

        void DetectGpu(out string gpuName, out double vramGb)
        {
            gpuName = "";
            vramGb = 0;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=name,memory.total --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                var p = Process.Start(psi);
                string so = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                string line = "";
                foreach (string ln in (so ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                { line = ln.Trim(); if (line.Length > 0) break; }
                if (!string.IsNullOrEmpty(line))
                {
                    int comma = line.LastIndexOf(',');
                    if (comma > 0)
                    {
                        gpuName = line.Substring(0, comma).Trim();
                        string mem = line.Substring(comma + 1).Trim();
                        double mb;
                        if (double.TryParse(mem, NumberStyles.Any, CultureInfo.InvariantCulture, out mb) && mb > 0)
                            vramGb = Math.Round(mb / 1024.0, 1);
                    }
                }
            }
            catch { }
        }

        void SendGenEditStatus()
        {
            string exe = GenEditRuntimeExe();
            string modelDir = GenEditModelDir();
            string diff = GenEditModelPath("flux-2-klein-4b-Q4_0.gguf");
            string te = GenEditModelPath("Qwen3-4B-Q4_K_M.gguf");
            string vae = GenEditModelPath("flux2-vae.safetensors");
            bool runtimeOk = File.Exists(exe);
            bool diffOk = File.Exists(diff) && new FileInfo(diff).Length > 2200L * 1024 * 1024;
            bool teOk = File.Exists(te) && new FileInfo(te).Length > 2200L * 1024 * 1024;
            bool vaeOk = File.Exists(vae) && new FileInfo(vae).Length > 280L * 1024 * 1024;
            bool ready = runtimeOk && diffOk && teOk && vaeOk;
            var missing = new StringBuilder();
            Action<string> addMissing = s => { if (missing.Length > 0) missing.Append("，"); missing.Append(s); };
            if (!runtimeOk) addMissing("推理运行时");
            if (!diffOk) addMissing("扩散模型");
            if (!teOk) addMissing("文本编码器");
            if (!vaeOk) addMissing("VAE");
            string gpu; double vram;
            DetectGpu(out gpu, out vram);
            string backend = (gpu ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ? "cuda0" : "vulkan0";
            int maxSide = vram > 0 && vram <= 6.5 ? 768 : (vram > 0 && vram <= 10.5 ? 1024 : 1280);
            long total = 0;
            try { if (File.Exists(diff)) total += new FileInfo(diff).Length; } catch { }
            try { if (File.Exists(te)) total += new FileInfo(te).Length; } catch { }
            try { if (File.Exists(vae)) total += new FileInfo(vae).Length; } catch { }
            Send("{\"type\":\"genedit_status\",\"ready\":" + (ready ? "true" : "false") +
                 ",\"runtimeOk\":" + (runtimeOk ? "true" : "false") +
                 ",\"modelsOk\":" + ((diffOk && teOk && vaeOk) ? "true" : "false") +
                 ",\"missing\":" + JsonStr(missing.ToString()) +
                 ",\"runtime\":" + JsonStr(exe) +
                 ",\"modelDir\":" + JsonStr(modelDir) +
                 ",\"modelBytes\":" + total +
                 ",\"gpu\":" + JsonStr(gpu) +
                 ",\"vramGb\":" + vram.ToString("0.0", CultureInfo.InvariantCulture) +
                 ",\"backend\":" + JsonStr(backend) +
                 ",\"recommendedMaxSide\":" + maxSide +
                 ",\"model\":\"FLUX.2 Klein 4B Q4_0\"}");
        }

        string GenEditPreviewData(Bitmap bmp, int maxSide)
        {
            int w = bmp.Width, h = bmp.Height;
            if (w > maxSide || h > maxSide)
            {
                double s = Math.Min(maxSide / (double)w, maxSide / (double)h);
                w = Math.Max(1, (int)Math.Round(w * s));
                h = Math.Max(1, (int)Math.Round(h * s));
            }
            using (var small = new Bitmap(w, h))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, w, h);
                }
                using (var ms = new MemoryStream())
                {
                    small.Save(ms, ImageFormat.Jpeg);
                    return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        void GenEditRun(string json)
        {
            int idx = selected;
            if (idx < 0 || idx >= files.Count)
            {
                Send("{\"type\":\"genedit\",\"data\":{\"ok\":false,\"error\":\"请先在左侧选中一张图片。\"}}");
                return;
            }
            string srcPath = files[idx];
            genEditSource = srcPath;
            Send("{\"type\":\"genedit_start\"}");
            new Thread(() =>
            {
                string inFile = Path.Combine(Path.GetTempPath(), "ibt_gen_" + Guid.NewGuid().ToString("N") + ".json");
                string outFile = Path.Combine(Path.GetTempPath(), "ibt_gen_" + Guid.NewGuid().ToString("N") + ".out.json");
                string snap = CacheStore.TempFile("genedit_in_", ".png");
                string outPath = Path.Combine(CacheStore.Sub("genedit"),
                    "gen_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                try
                {
                    Bitmap snapBmp = null;
                    InvokeUi(() =>
                    {
                        try
                        {
                            if (cachedBase != null && cachedFile == srcPath)
                                snapBmp = (Bitmap)cachedBase.Clone();
                            else
                                snapBmp = LoadBase(srcPath);
                        }
                        catch (Exception ex) { Log("生成式修图取底图失败: " + ex.Message); }
                    });
                    if (snapBmp == null) throw new Exception("无法读取当前图片底图。");
                    using (snapBmp) snapBmp.Save(snap, ImageFormat.Png);

                    string cfg = (json ?? "{}").Trim();
                    if (cfg.Length == 0) cfg = "{}";
                    if (!cfg.StartsWith("{")) throw new Exception("生成参数格式错误。");
                    if (cfg.EndsWith("}")) cfg = cfg.Substring(0, cfg.Length - 1).TrimEnd();
                    if (cfg.Length > 1) cfg += ",";
                    string gpu; double vram;
                    DetectGpu(out gpu, out vram);
                    string backend = (gpu ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ? "cuda0" : "vulkan0";
                    cfg += "\"input\":" + JsonStr(snap) + ",\"output\":" + JsonStr(outPath) +
                           ",\"vram_gb\":" + vram.ToString("0.0", CultureInfo.InvariantCulture) +
                           ",\"backend\":" + JsonStr(backend) + "}";
                    File.WriteAllText(inFile, cfg, new UTF8Encoding(false));

                    string err;
                    string stdout = PyTools.Run(BeautyRunner.PythonExe, "gen_edit.py",
                        new string[] { "run", inFile, outFile }, 1800000, out err);
                    if (!File.Exists(outFile))
                        throw new Exception("生成引擎没有返回结果：" + (err ?? stdout ?? ""));
                    string result = File.ReadAllText(outFile, Encoding.UTF8);
                    Send("{\"type\":\"genedit\",\"data\":" + result + "}");
                    if (result.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0 ||
                        result.IndexOf("\"ok\": true", StringComparison.Ordinal) >= 0)
                    {
                        if (!File.Exists(outPath)) throw new Exception("结果文件未生成。");
                        string orig64, res64;
                        using (var ob = new Bitmap(snap)) orig64 = GenEditPreviewData(ob, 1000);
                        using (var rb = new Bitmap(outPath)) res64 = GenEditPreviewData(rb, 1000);
                        Send("{\"type\":\"genedit_preview\",\"path\":" + JsonStr(outPath) +
                             ",\"orig\":\"" + orig64 + "\",\"result\":\"" + res64 + "\"}");
                    }
                    Log("生成式修图完成: " + Path.GetFileName(outPath));
                }
                catch (Exception ex)
                {
                    string e = (ex.Message ?? "").Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                    Send("{\"type\":\"genedit\",\"data\":{\"ok\":false,\"error\":" + JsonStr(e) + "}}");
                    Log("生成式修图失败: " + e);
                }
                finally
                {
                    try { if (File.Exists(inFile)) File.Delete(inFile); } catch { }
                    try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                    try { if (File.Exists(snap)) File.Delete(snap); } catch { }
                }
            }) { IsBackground = true }.Start();
        }

        void GenEditAdd(string path)
        {
            try
            {
                if (!File.Exists(path)) { Send("{\"type\":\"toast\",\"msg\":\"结果文件不存在\"}"); return; }
                if (!AddFileUnique(path)) { Send("{\"type\":\"toast\",\"msg\":\"这张图已经在列表里\"}"); return; }
                selected = files.Count - 1;
                cachedBase = null;
                cachedFile = null;
                SendState();
                QueuePreview();
                Send("{\"type\":\"toast\",\"msg\":\"已作为新图片加入编辑器，原图未改动\"}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":\"加入列表失败：" + ex.Message.Replace("\"", "'") + "\"}"); }
        }

        void GenEditSave(string path)
        {
            try
            {
                if (!File.Exists(path)) { Send("{\"type\":\"toast\",\"msg\":\"结果文件不存在\"}"); return; }
                string dir = outputDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    string src = genEditSource;
                    if (!string.IsNullOrEmpty(src) && File.Exists(src))
                        dir = Path.GetDirectoryName(src);
                    else
                        dir = selected >= 0 && selected < files.Count ? Path.GetDirectoryName(files[selected]) : Application.StartupPath;
                }
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Application.StartupPath;
                string baseName = (!string.IsNullOrEmpty(genEditSource) && File.Exists(genEditSource))
                    ? Path.GetFileNameWithoutExtension(genEditSource)
                    : (selected >= 0 && selected < files.Count ? Path.GetFileNameWithoutExtension(files[selected]) : "genedit");
                string dst = Path.Combine(dir, baseName + "_genedit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                int n = 2;
                while (File.Exists(dst))
                    dst = Path.Combine(dir, baseName + "_genedit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + (n++) + ".png");
                File.Copy(path, dst, false);
                Send("{\"type\":\"toast\",\"msg\":\"已保存：" + Path.GetFileName(dst).Replace("\"", "'") + "\"}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":\"保存失败：" + ex.Message.Replace("\"", "'") + "\"}"); }
        }

        void AiToolRun(string json)
        {
            string preOp = ReadJsonString(json ?? "{}", "op");
            bool searchOnly0 = preOp.Equals("a6_search", StringComparison.OrdinalIgnoreCase);
            int idx = selected;
            if (!searchOnly0 && (idx < 0 || idx >= files.Count))
            {
                Send("{\"type\":\"aitool\",\"op\":\"\",\"data\":{\"ok\":false,\"error\":\"请先在左侧选中一张图片。\"}}");
                return;
            }
            string srcPath = (idx >= 0 && idx < files.Count) ? files[idx] : "";
            if (!string.IsNullOrEmpty(srcPath)) genEditSource = srcPath;
            Send("{\"type\":\"aitool_start\"}");
            new Thread(() =>
            {
                string inFile = Path.Combine(Path.GetTempPath(), "ibt_ai_" + Guid.NewGuid().ToString("N") + ".json");
                string outFile = Path.Combine(Path.GetTempPath(), "ibt_ai_" + Guid.NewGuid().ToString("N") + ".out.json");
                string snap = CacheStore.TempFile("aitool_in_", ".png");
                string outPath = Path.Combine(CacheStore.Sub("aitools"),
                    "ai_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                string op = "";
                string maskTemp = "";
                try
                {
                    string cfg = (json ?? "{}").Trim();
                    if (cfg.Length == 0) cfg = "{}";
                    if (!cfg.StartsWith("{")) throw new Exception("AI 工具参数格式错误。");
                    op = ReadJsonString(cfg, "op");
                    bool searchOnly = op.Equals("a6_search", StringComparison.OrdinalIgnoreCase);
                    bool noPreview = searchOnly || op.Equals("a5_score", StringComparison.OrdinalIgnoreCase);

                    if (!searchOnly)
                    {
                        Bitmap snapBmp = null;
                        InvokeUi(() =>
                        {
                            try
                            {
                                if (cachedBase != null && cachedFile == srcPath)
                                    snapBmp = (Bitmap)cachedBase.Clone();
                                else
                                    snapBmp = LoadBase(srcPath);
                            }
                            catch (Exception ex) { Log("AI 工具取底图失败: " + ex.Message); }
                        });
                        if (snapBmp == null) throw new Exception("无法读取当前图片底图。");
                        using (snapBmp) snapBmp.Save(snap, ImageFormat.Png);

                        if (cfg.EndsWith("}")) cfg = cfg.Substring(0, cfg.Length - 1).TrimEnd();
                        if (cfg.Length > 1) cfg += ",";
                        cfg += "\"input\":" + JsonStr(snap) + ",\"output\":" + JsonStr(outPath) + "}";

                        string maskData = ReadJsonString((json ?? "{}"), "mask_data");
                        if (!string.IsNullOrEmpty(maskData))
                        {
                            int comma = maskData.IndexOf(',');
                            string b64 = comma >= 0 ? maskData.Substring(comma + 1) : maskData;
                            byte[] bytes = Convert.FromBase64String(b64);
                            maskTemp = CacheStore.TempFile("aitool_mask_", ".png");
                            File.WriteAllBytes(maskTemp, bytes);
                            if (cfg.EndsWith("}")) cfg = cfg.Substring(0, cfg.Length - 1).TrimEnd();
                            if (cfg.Length > 1) cfg += ",";
                            cfg += "\"mask\":" + JsonStr(maskTemp) + "}";
                        }
                    }
                    else
                    {
                        // 找图不需要当前底图，paths 由页面传入。
                        if (!cfg.EndsWith("}")) cfg += "}";
                    }
                    File.WriteAllText(inFile, cfg, new UTF8Encoding(false));

                    string engine = ReadJsonString(cfg, "engine");
                    string script = engine.Equals("flux", StringComparison.OrdinalIgnoreCase) ? "gen_edit.py" : "ai_tools.py";
                    int timeout = engine.Equals("flux", StringComparison.OrdinalIgnoreCase) ? 1800000 : 300000;
                    string err;
                    string stdout = PyTools.Run(BeautyRunner.PythonExe, script,
                        new string[] { "run", inFile, outFile }, timeout, out err);
                    if (!File.Exists(outFile))
                        throw new Exception("AI 工具没有返回结果：" + (err ?? stdout ?? ""));
                    string result = File.ReadAllText(outFile, Encoding.UTF8);
                    Send("{\"type\":\"aitool\",\"op\":" + JsonStr(op) + ",\"data\":" + result + "}");
                    if (!noPreview && (result.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0 ||
                        result.IndexOf("\"ok\": true", StringComparison.Ordinal) >= 0))
                    {
                        if (!File.Exists(outPath)) throw new Exception("结果文件未生成。");
                        string orig64, res64;
                        using (var ob = new Bitmap(snap)) orig64 = GenEditPreviewData(ob, 1000);
                        using (var rb = new Bitmap(outPath)) res64 = GenEditPreviewData(rb, 1000);
                        Send("{\"type\":\"aitool_preview\",\"op\":" + JsonStr(op) + ",\"path\":" + JsonStr(outPath) +
                             ",\"orig\":\"" + orig64 + "\",\"result\":\"" + res64 + "\"}");
                    }
                    Log("AI 工具 " + op + " 完成: " + Path.GetFileName(outPath));
                }
                catch (Exception ex)
                {
                    string e = (ex.Message ?? "").Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                    Send("{\"type\":\"aitool\",\"op\":" + JsonStr(op) + ",\"data\":{\"ok\":false,\"error\":" + JsonStr(e) + "}}");
                    Log("AI 工具失败: " + e);
                }
                finally
                {
                    try { if (File.Exists(inFile)) File.Delete(inFile); } catch { }
                    try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                    try { if (File.Exists(snap)) File.Delete(snap); } catch { }
                    try { if (!string.IsNullOrEmpty(maskTemp) && File.Exists(maskTemp)) File.Delete(maskTemp); } catch { }
                }
            }) { IsBackground = true }.Start();
        }

        void AiToolPickBackground()
        {
            try
            {
                using (var d = new OpenFileDialog())
                {
                    d.Title = "选择背景图片";
                    d.Filter = "图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff";
                    if (d.ShowDialog() == DialogResult.OK)
                        Send("{\"type\":\"aitool_bgpath\",\"path\":" + JsonStr(d.FileName) + "}");
                }
            }
            catch (Exception ex) { Log("选择背景图失败: " + ex.Message); }
        }

        void AiToolMaskBase()
        {
            int idx = selected;
            if (idx < 0 || idx >= files.Count)
            {
                Send("{\"type\":\"toast\",\"msg\":\"请先在左侧选中一张图片。\"}");
                return;
            }
            string srcPath = files[idx];
            Bitmap snapBmp = null;
            InvokeUi(() =>
            {
                try
                {
                    if (cachedBase != null && cachedFile == srcPath)
                        snapBmp = (Bitmap)cachedBase.Clone();
                    else
                        snapBmp = LoadBase(srcPath);
                }
                catch (Exception ex) { Log("消除底图读取失败: " + ex.Message); }
            });
            if (snapBmp == null)
            {
                Send("{\"type\":\"toast\",\"msg\":\"无法读取当前图片底图。\"}");
                return;
            }
            try
            {
                using (snapBmp)
                {
                    int ow = snapBmp.Width, oh = snapBmp.Height;
                    string data = GenEditPreviewData(snapBmp, 1200);
                    Send("{\"type\":\"aitool_maskbase\",\"orig\":" + JsonStr(data) +
                         ",\"width\":" + ow + ",\"height\":" + oh + "}");
                }
            }
            catch (Exception ex) { Log("消除底图发送失败: " + ex.Message); }
        }

        void GenEditInstall()
        {
            if (genEditInstalling) { Send("{\"type\":\"toast\",\"msg\":\"模型正在下载，请稍候\"}"); return; }
            string py = BeautyRunner.PythonExe;
            if (string.IsNullOrEmpty(py))
            {
                Send("{\"type\":\"genedit_install\",\"done\":true,\"data\":{\"ok\":false,\"error\":\"未找到便携 Python，无法下载模型。\"}}");
                return;
            }
            genEditInstalling = true;
            new Thread(() =>
            {
                string outFile = CacheStore.TempFile("genedit_install_", ".json");
                try
                {
                    string script = Path.Combine(Application.StartupPath, "ai", "genedit_install.py");
                    if (!File.Exists(script)) throw new Exception("缺少下载脚本 genedit_install.py");
                    string args = "\"" + script + "\" install \"" + outFile + "\"";
                    var psi = new ProcessStartInfo(py, args);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                    psi.WorkingDirectory = Path.Combine(Application.StartupPath, "ai");
                    try { psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; psi.EnvironmentVariables["PYTHONUTF8"] = "1"; } catch { }
                    var proc = Process.Start(psi);
                    proc.OutputDataReceived += (s, e) =>
                    {
                        string line = e.Data;
                        if (string.IsNullOrEmpty(line) || !line.StartsWith("PROGRESS|")) return;
                        string[] p = line.Split(new[] { '|' }, 4);
                        double pct = 0;
                        if (p.Length >= 3) double.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out pct);
                        string detail = p.Length >= 4 ? p[3] : "";
                        Send("{\"type\":\"genedit_install\",\"done\":false,\"phase\":" + JsonStr(p.Length >= 2 ? p[1] : "") +
                             ",\"percent\":" + pct.ToString("0.0", CultureInfo.InvariantCulture) +
                             ",\"detail\":" + JsonStr(detail) + "}");
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        string line = e.Data;
                        if (!string.IsNullOrEmpty(line) && line.Length < 300)
                            Send("{\"type\":\"genedit_install\",\"done\":false,\"detail\":" + JsonStr(line) + "}");
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(2400000))
                    {
                        try { proc.Kill(); } catch { }
                        throw new Exception("下载超时（40 分钟）。");
                    }
                    proc.WaitForExit();
                    string result = File.Exists(outFile) ? File.ReadAllText(outFile, Encoding.UTF8)
                        : "{\"ok\":false,\"error\":\"下载脚本没有返回结果。\"}";
                    Send("{\"type\":\"genedit_install\",\"done\":true,\"data\":" + result + "}");
                    SendGenEditStatus();
                }
                catch (Exception ex)
                {
                    Send("{\"type\":\"genedit_install\",\"done\":true,\"data\":{\"ok\":false,\"error\":" + JsonStr(ex.Message) + "}}");
                }
                finally
                {
                    genEditInstalling = false;
                    try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                }
            }) { IsBackground = true }.Start();
        }

        void OpenDupWindow()
        {
            try
            {
                if (dedupForm != null && !dedupForm.IsDisposed)
                {
                    if (dedupForm.WindowState == FormWindowState.Minimized) dedupForm.WindowState = FormWindowState.Normal;
                    dedupForm.TopMost = true;
                    dedupForm.Show();
                    dedupForm.BringToFront();
                    dedupForm.Activate();
                    return;
                }
                var f = new Form
                {
                    Text = "照片去重",
                    Width = 1000,
                    Height = 720,
                    MinimumSize = new Size(760, 520),
                    StartPosition = FormStartPosition.CenterScreen,
                    BackColor = Color.FromArgb(32, 32, 36),
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = true,
                    TopMost = true,
                    Icon = this.Icon
                };
                var w = new WebView2 { Dock = DockStyle.Fill };
                try { w.DefaultBackgroundColor = Color.FromArgb(0, 0, 0, 0); } catch { }
                f.Controls.Add(w);
                dedupForm = f;
                dedupWeb = w;
                dedupGlass = new GlassBackdrop(f, w);
                f.FormClosed += (s, e) =>
                {
                    try { if (dedupGlass != null) { dedupGlass.Dispose(); dedupGlass = null; } } catch { }
                    if (dedupWeb == w) dedupWeb = null;
                    if (dedupForm == f) dedupForm = null;
                };
                f.Show();
                ApplyNativeSkin(ReadSkinName(), f, w);
                InitDupWeb(w);
            }
            catch (Exception ex)
            {
                Log("打开去重窗口失败: " + ex.Message);
            }
        }

        async void InitDupWeb(WebView2 w)
        {
            try
            {
                Log("去重窗口 InitDupWeb 开始");
                if (webEnv != null) await w.EnsureCoreWebView2Async(webEnv);
                else await w.EnsureCoreWebView2Async(null);
                Log("去重窗口 EnsureCoreWebView2 完成 core=" + (w.CoreWebView2 != null));
                int tries = 0;
                while (w.CoreWebView2 == null && tries < 20) { await Task.Delay(100); tries++; }
                if (w.CoreWebView2 == null) { Log("去重窗口 WebView2 初始化超时"); return; }
                ApplyNativeSkin(ReadSkinName(), dedupForm, w);
                w.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app.local", Application.StartupPath, CoreWebView2HostResourceAccessKind.Allow);
                w.CoreWebView2.WebMessageReceived += DupWeb_Message;
                w.CoreWebView2.NavigationCompleted += (s, e) => Log("去重窗口页面加载完成 success=" + e.IsSuccess + " url=" + w.Source);
                string skin = ReadSkinName();
                if (skin == "aurora") skin = "dark";
                Log("去重窗口开始导航");
                w.CoreWebView2.Navigate("http://app.local/dedup.html?skin=" + skin + "&v=" + DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Log("去重窗口 WebView2 初始化失败: " + ex.Message);
                try { MessageBox.Show("去重窗口初始化失败：\n" + ex.Message, "照片去重", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            }
        }

        void CloseDupWindow()
        {
            var f = dedupForm;
            if (f == null || f.IsDisposed) return;
            if (NormalizeSkin(ReadSkinName()) == "blackgold" && !dedupFadeClosing)
            {
                dedupFadeClosing = true;
                int step = 0;
                var timer = new System.Windows.Forms.Timer { Interval = 16 };
                timer.Tick += (s, e) =>
                {
                    if (f.IsDisposed) { timer.Stop(); timer.Dispose(); return; }
                    step++;
                    try { f.Opacity = Math.Max(0.0, 1.0 - step / 14.0); } catch { }
                    if (step >= 14)
                    {
                        timer.Stop(); timer.Dispose();
                        dedupFadeClosing = false;
                        try { f.Close(); } catch { }
                    }
                };
                try { f.Opacity = 1; } catch { }
                timer.Start();
                return;
            }
            try { f.Close(); } catch { }
        }

        void DupWeb_Message(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string msg = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(msg)) return;
                string[] parts = msg.Split('|');
                switch (parts[0])
                {
                    case "dupshow":
                        Send(dupLastGroupsJson);
                        SendDupState();
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
                        BinChooseDir();
                        break;
                    case "dupscan":
                        StartDupScan();
                        break;
                    case "dupset":
                        if (parts.Length > 1)
                        {
                            int nth; int.TryParse(parts[1], out nth);
                            dupThreshold = Math.Max(2, Math.Min(14, nth));
                            try { File.WriteAllText(Path.Combine(Application.StartupPath, "dup_threshold.txt"), dupThreshold.ToString()); } catch { }
                            SendDupState();
                        }
                        break;
                    case "duppause":
                        dupPaused = !dupPaused;
                        SendDupState();
                        Send("{\"type\":\"dup\",\"phase\":\"pause\",\"done\":" + dupDone + ",\"total\":" + dupTotal + "}");
                        break;
                    case "dupcancel":
                        dupCancel = true; dupPaused = false;
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
                    case "dupwinclose":
                        CloseDupWindow();
                        break;
                    case "dupwinmin":
                        InvokeUi(() => { try { if (dedupForm != null) dedupForm.WindowState = FormWindowState.Minimized; } catch { } });
                        break;
                    case "dupwinmax":
                        InvokeUi(() => {
                            try {
                                if (dedupForm == null) return;
                                dedupForm.WindowState = dedupForm.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                            } catch { }
                        });
                        break;
                    case "dupdragstart":
                        InvokeUi(() => {
                            try {
                                if (dedupForm == null) return;
                                Native.ReleaseCapture();
                                Native.SendMessage(dedupForm.Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
                            } catch { }
                        });
                        break;
                    case "dupclose":
                        CloseDupWindow();
                        break;
                }
            }
            catch (Exception ex) { Log("去重窗口消息处理异常: " + ex.Message); }
        }

        static bool IsImageFile(string f)
        {
            string ext = Path.GetExtension(f == null ? "" : f).ToLower();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".tif" || ext == ".tiff" || ext == ".webp"
                || ext == ".cr2" || ext == ".cr3" || ext == ".nef" || ext == ".nrw" || ext == ".arw" || ext == ".srf" || ext == ".sr2"
                || ext == ".dng" || ext == ".orf" || ext == ".rw2" || ext == ".raf" || ext == ".srw" || ext == ".pef" || ext == ".raw";
        }

        // 统一入口：把文件/文件夹路径加入列表（拖放与消息共用）
        static string FileKey(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p; }
        }

        bool AddFileUnique(string p)
        {
            if (!IsImageFile(p)) return false;
            string key = FileKey(p);
            if (!fileKeys.Add(key)) return false;
            files.Add(key);
            return true;
        }

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
                            if (AddFileUnique(f)) added++;
                    }
                    else if (AddFileUnique(p)) added++;
                }
                catch (Exception ex) { Log("添加路径失败 " + p + ": " + ex.Message); }
            }
            return added;
        }

        // v2.41：相册右键“编辑”改为拉起独立的图片编辑程序。
        // v2.12 拆分后 album.html 里仍保留一套旧内嵌编辑界面，继续走 albtoedit 会进入旧 UI。
        void LaunchEditor(List<string> paths)
        {
            try
            {
                string parent = null;
                try { parent = Path.GetDirectoryName(Application.StartupPath.TrimEnd(Path.DirectorySeparatorChar)); } catch { }
                string exe = null;
                if (!string.IsNullOrEmpty(parent))
                {
                    string preferred = Path.Combine(parent, "图片编辑", "图片编辑.exe");
                    if (File.Exists(preferred)) exe = preferred;
                }
                if (exe == null)
                {
                    string sibling = Path.Combine(Application.StartupPath, "图片编辑.exe");
                    if (File.Exists(sibling)) exe = sibling;
                }
                if (exe == null)
                {
                    Send("{\"type\":\"toast\",\"msg\":\"未安装图片编辑组件，请重新运行安装包并勾选「图片编辑」\"}");
                    Log("打开图片编辑失败：未找到图片编辑.exe");
                    return;
                }
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                if (paths != null && paths.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (string p in paths)
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append('"').Append(p.Replace("\"", "\\\"")).Append('"');
                    }
                    psi.Arguments = sb.ToString();
                }
                Process.Start(psi);
                Log("从相册启动图片编辑: " + exe + " files=" + (paths == null ? 0 : paths.Count));
                Send("{\"type\":\"toast\",\"msg\":\"已打开图片编辑" +
                     (paths != null && paths.Count > 0 ? "（" + paths.Count + " 张）" : "") + "\"}");
            }
            catch (Exception ex)
            {
                Log("打开图片编辑失败: " + ex.Message);
                Send("{\"type\":\"toast\",\"msg\":\"打开图片编辑失败：" + ex.Message.Replace("\"", "'") + "\"}");
            }
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
                        bool firstReady = !webPageReady;
                        webPageReady = true;
                        if (Splash != null) { Splash.Close(); Splash.Dispose(); Splash = null; }
                        string readySkin = ReadSkinName();
                        bool deferGlass = AuroraWindowGlass && NormalizeSkin(readySkin) == "aurora";
                        if (!deferGlass) ApplyNativeSkin(readySkin);
                        Show(); Activate();
                        if (firstReady) BeginBlackGoldOpenFade(readySkin);
                        else if (Opacity < 1) Opacity = 1;
                        if (deferGlass)
                        {
                            // 先让 WebView2 真正画出一帧，再加分层透明；过早设置会让整块网页不显示。
                            var glassTimer = new System.Windows.Forms.Timer { Interval = 260 };
                            glassTimer.Tick += (s2, e2) =>
                            {
                                glassTimer.Stop(); glassTimer.Dispose();
                                try { ApplyNativeSkin(readySkin); if (Opacity < 1) Opacity = 1; } catch { }
                                glassBridgeReady = true;
                                ShowGlassBridge();
                            };
                            glassTimer.Start();
                        }
                        if (AppModule.IsAlbum)
                        {
                            LoadAlbRoots();
                            LoadAiPerf();
                            SendAlbRoots();
                        }
                        else
                        {
                            Send("{\"type\":\"presets\",\"items\":" + Presets.List() + "}");
                            // 页面这时才真正能收消息：补发一次完整状态（LUT 列表、文件列表、输出目录）
                            if (!startupPathsApplied && StartupPaths != null && StartupPaths.Length > 0)
                            {
                                startupPathsApplied = true;
                                int startupAdded = AddPaths(StartupPaths);
                                if (startupAdded > 0) AutoSelectFirst();
                                Log("启动参数导入 " + startupAdded + " 张");
                            }
                            SendState();
                            QueuePreview();
                        }
                        Send("{\"type\":\"appmode\",\"mode\":" + JsonStr(AppModule.Id) + "}");
                        break;
                    case "addfolder":
                        InvokeUi(() => {
                            using (var d = new FolderBrowserDialog())
                                if (d.ShowDialog() == DialogResult.OK)
                                    foreach (var f in Directory.GetFiles(d.SelectedPath, "*.*", SearchOption.AllDirectories))
                                        AddFileUnique(f);
                        });
                        AutoSelectFirst(); SendState();
                        QueuePreview();
                        break;
                    case "addfiles":
                        InvokeUi(() => {
                            using (var d = new OpenFileDialog()) {
                                d.Multiselect = true;
                                d.Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp;*.cr2;*.cr3;*.nef;*.nrw;*.arw;*.srf;*.sr2;*.dng;*.orf;*.rw2;*.raf;*.srw;*.pef|RAW 预览|*.cr2;*.cr3;*.nef;*.nrw;*.arw;*.dng;*.raf;*.orf;*.rw2";
                                if (d.ShowDialog() == DialogResult.OK)
                                    foreach (var f in d.FileNames) AddFileUnique(f);
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
                            fileKeys.Remove(FileKey(files[selected]));
                            files.RemoveAt(selected);
                            selected = files.Count > 0 ? Math.Min(selected, files.Count - 1) : -1;
                            cachedBase = null;
                        }
                        SendState();
                        if (selected < 0) Send("{\"type\":\"clearpreview\"}");
                        else QueuePreview();
                        break;
                    case "clear":
                        files.Clear(); fileKeys.Clear(); selected = -1; cachedBase = null;
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
                            // 只有裁切/水平校正才需要重新解码原图
                            if (parts[1].StartsWith("crop_") || parts[1].StartsWith("level_")) cachedBase = null;
                        }
                        QueuePreview();
                        break;
                    case "glassalpha": {
                            int ga = 238;
                            try { if (parts.Length > 1) int.TryParse(parts[1], out ga); } catch { }
                            SetAuroraAlpha(ga);
                        }
                        break;
                    case "setbatchparams": {
                            var map = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(B64Text(parts[1])) as Dictionary<string, object>;
                            var bp = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
                            if (map != null) foreach (var kv in map) {
                                var pd = kv.Value as Dictionary<string, object>;
                                if (pd == null) continue;
                                var one = new Dictionary<string, double>();
                                foreach (var pv in pd) one[pv.Key] = Convert.ToDouble(pv.Value);
                                bp[kv.Key] = one;
                            }
                            batchParams = bp;
                        }
                        break;
                    case "setparams": {
                            var pd = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(B64Text(parts[1])) as Dictionary<string, object>;
                            if (pd != null) {
                                bool geometry = false;
                                foreach (var kv in pd) {
                                    if (kv.Key == "lut_mode") { lutMode = Convert.ToString(kv.Value); lutAnalyzed = false; aiParamsCached = false; continue; }
                                    par[kv.Key] = Convert.ToDouble(kv.Value);
                                    if (kv.Key.StartsWith("crop_") || kv.Key.StartsWith("level_")) geometry = true;
                                }
                                if (geometry) cachedBase = null;
                                QueuePreview();
                            }
                        }
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
                    case "setout": {
                            if (parts.Length < 2) break;
                            outputDir = msg.Substring(action.Length + 1).Trim();
                            SendState();
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
                    case "dupset":
                        if (parts.Length > 1)
                        {
                            int nth; int.TryParse(parts[1], out nth);
                            dupThreshold = Math.Max(2, Math.Min(14, nth));
                            try { File.WriteAllText(Path.Combine(Application.StartupPath, "dup_threshold.txt"), dupThreshold.ToString()); } catch { }
                            SendDupState();
                        }
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
                    case "dupwindow":
                        OpenDupWindow();
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
                    case "openeditor":
                        {
                            string rest = msg.Length > action.Length + 1 ? msg.Substring(action.Length + 1) : "";
                            var ps = new List<string>();
                            foreach (var s in rest.Split('\n')) { string t = s.Trim(); if (t.Length > 0) ps.Add(t); }
                            LaunchEditor(ps);
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
                    case "albfacesstart":
                        StartFaceScan();
                        break;
                    case "albfacespause":
                        FacePause();
                        break;
                    case "albfacesresume":
                        FaceResume();
                        break;
                    case "albfacesrestart":
                        FaceRestart();
                        break;
                    case "albaiensure":
                        {
                            bool forceAi = parts.Length > 1 && parts[1] == "1";
                            AiEnsureSearch(forceAi);
                        }
                        break;
                    case "albaiindexstart":
                        AiEnsureSearch(true);
                        break;
                    case "albaiindexpause":
                        AiPauseIndex();
                        break;
                    case "albaiindexresume":
                        AiResumeIndex();
                        break;
                    case "albaiindexrestart":
                        AiRestartIndex();
                        break;
                    case "albaiensureidle":
                        AiScheduleIdleIndex();
                        break;
                    case "albperf":
                        if (parts.Length > 1) ApplyAiPerf(parts[1]);
                        break;
                    case "albaisearch":
                        {
                            string qAi = msg.Length > action.Length + 1 ? msg.Substring(action.Length + 1) : "";
                            try { qAi = Uri.UnescapeDataString(qAi); } catch { }
                            AiSearchQuery(qAi);
                        }
                        break;
                    case "albfacesstop":
                        faceStop = true;
                        // 不再直接杀进程：写停止标记，让人脸脚本把当前断点写入状态文件后自行退出。
                        try { if (!string.IsNullOrEmpty(faceStopFile)) File.WriteAllText(faceStopFile, "stop"); } catch { }
                        break;
                    case "albrefresh":
                        if (albRoots.Count > 0) StartAlbumScan(false, true);
                        break;
                    case "albscanstart":
                        AlbScanStartManual();
                        break;
                    case "albscanpause":
                        AlbScanPause();
                        break;
                    case "albscanresume":
                        AlbScanResume();
                        break;
                    case "albscanrestart":
                        AlbScanRestartManual();
                        break;
                    case "albclearcache":
                        ClearAlbCache();
                        break;
                    case "albchoosecache":
                        // 文件夹对话框和模态确认都放到消息回调之后，避免 WebView2 重入。
                        try { Log("收到缓存位置命令"); } catch { }
                        BeginInvoke((Action)(() => ChooseCacheLocation()));
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
                    case "albtags":
                        {
                            string pTag = msg.Length > action.Length + 1 ? msg.Substring(action.Length + 1) : "";
                            try { pTag = Uri.UnescapeDataString(pTag); } catch { }
                            AiQueryTags(pTag);
                        }
                        break;
                    case "albmeta":
                        Send("{\"type\":\"albmeta\",\"data\":" + (albMeta == null ? "{}" : albMeta.ToJson()) + "}");
                        break;
                    case "albmetaitem":
                        if (parts.Length > 2 && albMeta != null)
                        {
                            string mp = B64Text(parts[1]);
                            string mj = B64Text(parts[2]);
                            var md = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(mj) as Dictionary<string, object>;
                            if (!string.IsNullOrEmpty(mp)) albMeta.SetItem(mp, md);
                        }
                        break;
                    case "albmetaitems":
                        if (parts.Length > 1 && albMeta != null) albMeta.SetItems(B64Text(parts[1]));
                        break;
                    case "albstate":
                        if (parts.Length > 1 && albMeta != null) albMeta.MergeState(B64Text(parts[1]));
                        break;
                    case "albfolders":
                        SendAlbFolders();
                        break;
                    case "albrename":
                        if (parts.Length > 1) AlbumBatchRename(B64Text(parts[1]));
                        break;
                    case "albrenameundo":
                        AlbumBatchRenameUndo();
                        break;
                    case "albmove":
                        if (parts.Length > 1) AlbumBatchMove(B64Text(parts[1]));
                        break;
                    case "albimportpick":
                        if (parts.Length > 1) AlbumImportPick(parts[1]);
                        break;
                    case "albimport":
                        if (parts.Length > 1) AlbumImport(B64Text(parts[1]));
                        break;
                    case "albfaceadmin":
                        if (parts.Length > 1) AlbumFaceAdmin(B64Text(parts[1]));
                        break;
                    case "albprivhash":
                        if (parts.Length > 2) SendPrivHash(B64Text(parts[1]), B64Text(parts[2]));
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
                    case "proopenfolder":
                        if (parts.Length > 1)
                        {
                            try
                            {
                                string f = B64Text(parts[1]);
                                if (File.Exists(f)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + f + "\"");
                                else if (Directory.Exists(f)) System.Diagnostics.Process.Start("explorer.exe", "\"" + f + "\"");
                            }
                            catch { }
                        }
                        break;
                    case "albexport":
                        {
                            var exportPaths = new List<string>();
                            if (parts.Length > 1)
                            {
                                foreach (var s in parts[1].Split('\n'))
                                {
                                    string t = s.Trim();
                                    if (t.Length > 0 && File.Exists(t)) exportPaths.Add(t);
                                }
                            }
                            if (exportPaths.Count > 0)
                                new Thread(() => DoExport(exportPaths)) { IsBackground = true }.Start();
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
                        try { new ThemedAboutForm(ReadSkinName(), AppModule.DisplayName, AppModule.AboutText).ShowDialog(this); }
                        catch { MessageBox.Show(AppModule.AboutText, "关于 " + AppModule.DisplayName); }
                        break;
                    case "setskin":
                        try {
                            string skin = NormalizeSkin(parts.Length > 1 ? parts[1] : "dark");
                            File.WriteAllText(Path.Combine(Application.StartupPath, "skin.txt"), skin);
                            File.WriteAllText(AppModule.SharedSkinFile(), skin);
                            ApplyNativeSkin(skin);
                            glassBridgeReady = skin == "aurora";
                            if (skin == "aurora") ShowGlassBridge(); else HideGlassBridge();
                            if (dedupForm != null && !dedupForm.IsDisposed) ApplyNativeSkin(skin, dedupForm, dedupWeb);
                        } catch {}
                        break;
                    case "getskin":
                        Send("{\"type\":\"skin\",\"skin\":" + JsonStr(ReadSkinName()) + "}");
                        break;
                    case "winclose": InvokeUi(() => CloseWithBlackGoldFade()); break;
                    case "winmin": InvokeUi(() => MinimizeFromUi()); break;
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
                    case "genedit":
                        if (parts.Length > 1) GenEditRun(B64Text(parts[1]));
                        break;
                    case "aitool":
                        if (parts.Length > 1) AiToolRun(B64Text(parts[1]));
                        break;
                    case "aitool_pickbg":
                        AiToolPickBackground();
                        break;
                    case "aitool_maskbase":
                        AiToolMaskBase();
                        break;
                    case "genedit_status":
                        SendGenEditStatus();
                        break;
                    case "genedit_install":
                        GenEditInstall();
                        break;
                    case "genedit_add":
                        if (parts.Length > 1) GenEditAdd(B64Text(parts[1]));
                        break;
                    case "genedit_save":
                        if (parts.Length > 1) GenEditSave(B64Text(parts[1]));
                        break;
                    case "genedit_openmodels":
                        try
                        {
                            string gmd = GenEditModelDir();
                            if (!Directory.Exists(gmd)) Directory.CreateDirectory(gmd);
                            Process.Start("explorer.exe", "\"" + gmd + "\"");
                        }
                        catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("打开模型目录失败：" + ex.Message) + "}"); }
                        break;
                    case "getorig":
                        new Thread(() => SendOrig()) { IsBackground = true }.Start();
                        break;
                    case "reflutdialog": {
                            string pickedImg = null;
                            InvokeUi(() => {
                                using (var d = new OpenFileDialog()) {
                                    d.Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp;*.cr2;*.cr3;*.nef;*.nrw;*.arw;*.srf;*.sr2;*.dng;*.orf;*.rw2;*.raf;*.srw;*.pef|RAW 预览|*.cr2;*.cr3;*.nef;*.nrw;*.arw;*.dng;*.raf;*.orf;*.rw2";
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
                    lock (PreviewLock)
                    {
                        if (Volatile.Read(ref previewToken) != my) return;
                        SendPreview();
                    }
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
            int mi = json.IndexOf("\"mode\":\"");
            if (mi > 0) {
                int s2 = mi + 8; int e3 = json.IndexOf('"', s2);
                if (e3 > s2) lutMode = json.Substring(s2, e3 - s2);
            }
            Send("{\"type\":\"presetapplied\",\"lut\":" + JsonStr(activeLutPath == null ? "" : activeLutPath) +
                 ",\"mode\":" + JsonStr(lutMode == null ? "log" : lutMode) + ",\"vals\":{" + sb.ToString() + "}}");
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
                sb.Append("{\"name\":").Append(JsonStr(Path.GetFileName(files[i]))).Append(",\"path\":").Append(JsonStr(files[i])).Append(",\"size\":").Append(sz).Append("}");
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
            sb.Append(",\"out\":").Append(JsonStr(outputDir));
            sb.Append(",\"par\":").Append(ParJson());
            sb.Append(",\"lut\":").Append(JsonStr(activeLutPath ?? ""));
            sb.Append(",\"lutmode\":").Append(JsonStr(lutMode ?? "log"));
            sb.Append("}");
            Send(sb.ToString());
        }

        string ParJson()
        {
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            foreach (var kv in par)
            {
                if (!first) sb.Append(","); first = false;
                sb.Append(JsonStr(kv.Key)).Append(":");
                double v = kv.Value;
                if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
                sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append("}");
            return sb.ToString();
        }

        // ==================== 照片去重 ====================
        class DupInfo
        {
            public string path;
            public long size;
            public long mtime;
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
        int dupThreshold = 6;
        int dupDone = 0, dupTotal = 0;
        int DupThreshold()
        {
            if (dupThreshold < 2) dupThreshold = 2;
            if (dupThreshold > 14) dupThreshold = 14;
            return dupThreshold;
        }
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

        static int BandOf(ulong h, int band)
        {
            return (int)((h >> (band * 16)) & 0xFFFFUL);
        }

        static IEnumerable<int> BandVariants(int v)
        {
            yield return v;
            for (int bit = 0; bit < 16; bit++) yield return v ^ (1 << bit);
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
                    ",\"warm\":" + (dupWarmDone ? "true" : "false") + ",\"ready\":" + (dupReady ? "true" : "false") +
                    ",\"threshold\":" + DupThreshold() + "}");
        }

        List<List<int>> DupCollectGroups(List<DupInfo> list, Func<int, int> find)
        {
            var map = new Dictionary<int, List<int>>();
            for (int i = 0; i < list.Count; i++)
            {
                int r = find(i);
                List<int> g;
                if (!map.TryGetValue(r, out g)) { g = new List<int>(); map[r] = g; }
                g.Add(i);
            }
            var groups = new List<List<int>>();
            foreach (var kv in map) if (kv.Value.Count >= 2) groups.Add(kv.Value);
            groups.Sort(delegate(List<int> x, List<int> y) {
                long px = (long)list[x[0]].w * list[x[0]].h, py = (long)list[y[0]].w * list[y[0]].h;
                return py.CompareTo(px);
            });
            // 组内按像素数/体积降序，第一张作为默认保留
            foreach (var g in groups)
            {
                g.Sort(delegate(int a, int b) {
                    long pa = (long)list[a].w * list[a].h, pb = (long)list[b].w * list[b].h;
                    if (pa != pb) return pb.CompareTo(pa);
                    return list[b].size.CompareTo(list[a].size);
                });
            }
            return groups;
        }

        string BuildDupGroupsJson(List<DupInfo> list, List<List<int>> outGroups, bool partial)
        {
            var jsb = new System.Text.StringBuilder();
            jsb.Append("{\"type\":\"dupgroups\",\"total\":").Append(outGroups.Count).Append(",\"files\":").Append(list.Count);
            if (partial) jsb.Append(",\"partial\":true");
            jsb.Append(",\"items\":[");
            int sent = 0;
            for (int gi = 0; gi < outGroups.Count; gi++)
            {
                var g = outGroups[gi];
                if (gi >= 300) break;
                if (gi > 0) jsb.Append(",");
                bool exactGroup = true;
                int minDist = 64;
                for (int q = 1; q < g.Count; q++)
                {
                    var a = list[g[0]];
                    var b = list[g[q]];
                    if (!string.Equals(a.sha, b.sha, StringComparison.Ordinal) || a.size != b.size) exactGroup = false;
                    int hd = Hamming(a.hash, b.hash); if (hd < minDist) minDist = hd;
                }
                if (g.Count > 1 && minDist == 64) minDist = 0;
                jsb.Append("{\"k\":").Append(JsonStr(exactGroup ? "exact" : "similar"))
                   .Append(",\"sim\":").Append(minDist).Append(",\"items\":[");
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
                    var md = albMeta == null ? null : albMeta.GetItem(it.path);
                    string metaJson = md == null ? "{}" : new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(md);
                    jsb.Append("{\"p\":").Append(JsonStr(it.path))
                      .Append(",\"n\":").Append(JsonStr(Path.GetFileName(it.path)))
                      .Append(",\"s\":").Append(it.size)
                      .Append(",\"mt\":").Append(it.mtime)
                      .Append(",\"fp\":").Append(JsonStr(Path.GetDirectoryName(it.path) ?? ""))
                      .Append(",\"w\":").Append(it.w)
                      .Append(",\"h\":").Append(it.h)
                      .Append(",\"d\":").Append(it.decoded ? "1" : "0")
                      .Append(",\"c\":").Append(compsJson)
                      .Append(",\"m\":").Append(metaJson)
                      .Append(",\"t\":").Append(thumb == null ? "null" : JsonStr("data:image/jpeg;base64," + thumb))
                      .Append("}");
                }
                jsb.Append("]}");
                sent++;
            }
            jsb.Append("]}");
            return jsb.ToString();
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
                var parent = new List<int>();
                Func<int, int> find = delegate(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
                bool unionChanged = false;
                Action<int, int> union = delegate(int a, int b) { int ra = find(a), rb = find(b); if (ra != rb) { parent[rb] = ra; unionChanged = true; } };
                var exact = new Dictionary<string, int>();
                var vsIndex = new Dictionary<string, Dictionary<int, List<int>>[]>();
                long lastPartialTicks = 0;
                bool partialDirty = false;

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
                            info.mtime = fi.LastWriteTimeUtc.Ticks;
                            info.w = ent.w; info.h = ent.h; info.hash = ent.hash;
                            info.sha = ent.sha ?? ""; info.decoded = ent.decoded;
                            info.comps = DupCompanions(p);
                            int idx = list.Count;
                            info.parent = idx;
                            list.Add(info);
                            parent.Add(idx);

                            // 边扫边分组：新图片一进来就和已发现的候选比较，命中后立刻推送当前分组。
                            unionChanged = false;
                            string exactKey = info.size + ":" + info.sha;
                            int exactIdx;
                            if (exact.TryGetValue(exactKey, out exactIdx)) union(idx, exactIdx);
                            else exact[exactKey] = idx;

                            if (info.w > 0 && info.h > 0)
                            {
                                string bucketKey = ((int)Math.Round((double)info.w / info.h * 100)).ToString();
                                Dictionary<int, List<int>>[] vi;
                                if (!vsIndex.TryGetValue(bucketKey, out vi))
                                {
                                    vi = new Dictionary<int, List<int>>[4];
                                    for (int bi = 0; bi < 4; bi++) vi[bi] = new Dictionary<int, List<int>>();
                                    vsIndex[bucketKey] = vi;
                                }
                                // 64 位 dHash 拆成 4 个 16 位 band。若两张图汉明距离 <= 14，
                                // 必有一个 band 的距离 <= 3；这里枚举 band 内 <=1 位差异的候选，
                                // 再做完整距离校验，避免大相册同宽高比桶的 O(n²) 爆炸。
                                var cand = new HashSet<int>();
                                for (int bi = 0; bi < 4; bi++)
                                {
                                    int band = BandOf(info.hash, bi);
                                    foreach (int mv in BandVariants(band))
                                    {
                                        List<int> lst;
                                        if (vi[bi].TryGetValue(mv, out lst))
                                            for (int q = 0; q < lst.Count; q++) cand.Add(lst[q]);
                                    }
                                }
                                int thr = DupThreshold();
                                foreach (int ib in cand)
                                {
                                    if (ib == idx || find(idx) == find(ib)) continue;
                                    var B = list[ib];
                                    if (B.w <= 0 || B.h <= 0) continue;
                                    double ar1 = (double)info.w / info.h, ar2 = (double)B.w / B.h;
                                    if (Math.Abs(ar1 - ar2) > 0.05) continue;
                                    if (Hamming(info.hash, B.hash) > thr) continue;
                                    union(idx, ib);
                                }
                                for (int bi = 0; bi < 4; bi++)
                                {
                                    int band = BandOf(info.hash, bi);
                                    List<int> lst;
                                    if (!vi[bi].TryGetValue(band, out lst)) { lst = new List<int>(); vi[bi][band] = lst; }
                                    lst.Add(idx);
                                }
                            }
                            if (unionChanged) partialDirty = true;
                        }
                    }
                    catch { }
                    if (dupDone % 15 == 0 || dupDone == dupTotal)
                        DupSend("{\"type\":\"dup\",\"phase\":\"scan\",\"done\":" + dupDone + ",\"total\":" + dupTotal + "}");
                    if (partialDirty)
                    {
                        long nowTicks = DateTime.UtcNow.Ticks;
                        if (nowTicks - lastPartialTicks >= 250 * TimeSpan.TicksPerMillisecond)
                        {
                            var partialGroups = DupCollectGroups(list, find);
                            DupSend(BuildDupGroupsJson(list, partialGroups, true));
                            lastPartialTicks = nowTicks;
                            partialDirty = false;
                        }
                    }
                }

                SaveDupCache();

                int n = list.Count;
                var outGroups = DupCollectGroups(list, find);
                dupLastGroupsJson = BuildDupGroupsJson(list, outGroups, false);
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
            public bool takenKnown;
            public int w, h;
            public double dur, lat, lon;
        }

        List<AlbItem> albItems = new List<AlbItem>();
        List<string> albRoots = new List<string>();
        volatile bool albRunning = false;
        volatile bool albScanPaused = false;
        DateTime albLastCkpt = DateTime.MinValue;

        // ===== AI 语义搜索（Chinese-CLIP 本地索引 + 常驻查询进程）=====
        // 索引/查询都由安装目录内 python 运行；模型缺失时自动退回原有元数据搜索。
        Process aiIndexProc, aiQueryProc;
        StreamWriter aiQueryIn;
        volatile bool aiIndexRunning = false;
        readonly object aiIndexGate = new object();
        volatile bool aiServerStarting = false;
        volatile bool aiReady = false;
        volatile bool aiIndexDone = false;
        volatile int aiIndexTotal = 0;
        volatile int aiIndexDoneCount = 0;
        volatile int aiIndexItemCount = 0;
        volatile bool aiIndexUnavailable = false;
        volatile bool aiReindexPending = false;
        volatile bool aiIndexPaused = false;
        volatile bool aiIndexResumePending = false;
        volatile bool aiIndexRestartPending = false;
        string aiIndexDb = null;
        string aiIndexStop = null;
        string aiLastQuery = "";
        // 正在进行的相册扫描。AI 索引在扫描阶段读取这份实时清单，边扫边建索引。
        volatile AlbScanCtx albLiveScanCtx = null;
        // 扫描阶段已经通知过 AI 的条目数，避免每两秒都重复启动索引。
        int aiScanKickCount = 0;
        // 详情页打开时如果查询进程尚未就绪，把路径排队，ready 后自动补查一次。
        string aiPendingTagPath = "";
        // 索引完成并 reload 后，用当前搜索词自动重查一次，避免新索引项不显示。
        volatile bool aiReloadQueryPending = false;
        // 扫描完成后不立刻抢占 CPU/GPU，等一小段空闲再增量建索引；打开搜索时立即启动。
        System.Threading.Timer aiIdleTimer = null;
        int aiIdleSeq = 0;
        // 索引性能档：low=省资源，mid=均衡，high=性能拉满。
        string aiPerfMode = "mid";
        volatile bool aiShuttingDown = false;

        string AlbRootsFile()
        {
            try
            {
                string dir = AppModule.ConfigDir();
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
            public bool takenKnown;
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
            public HashSet<string> itemPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    // 新版缓存第 18 列记录“拍摄时间是否真实来自 EXIF/视频元数据”。
                    // 旧缓存没有该列时用 mtime 反推，避免升级后把所有 EXIF 时间都当成未知。
                    if (p.Length >= 18) e.takenKnown = p[17] == "1";
                    else
                    {
                        long mtSec = e.mtime > 0 ? (long)((e.mtime - 621355968000000000L) / 10000000L) : 0;
                        e.takenKnown = e.taken > 0 && e.taken != mtSec;
                    }
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
                      .Append(e.iso).Append('\t').Append(e.takenKnown ? "1" : "0").AppendLine();
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
                    it.size = e.size; it.taken = e.taken; it.takenKnown = e.takenKnown; it.w = e.w; it.h = e.h;
                    it.lat = e.lat; it.lon = e.lon; it.dur = e.dur;
                    it.make = e.make; it.model = e.model; it.lens = e.lens;
                    it.fnum = e.fnum; it.exposure = e.exposure; it.focal = e.focal; it.iso = e.iso;
                    items.Add(it);
                }
                if (items.Count == 0) return;
                items.Sort(delegate(AlbItem a, AlbItem b) {
                    // 有拍摄时间的永远排在未知时间前面；未知项再按文件创建/修改时间排。
                    if (a.takenKnown != b.takenKnown) return a.takenKnown ? -1 : 1;
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

        // ---- 统一缓存根目录：默认安装目录 cache，也可由用户在相册选项里自定义 ----
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

        // 递归合并旧缓存目录到当前缓存根目录，避免升级后旧缩略图继续留在 AppData/TEMP。
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

        // 把旧版本的临时缩略图 / AppData / TEMP 缓存迁移到当前缓存根目录
        static void MigrateLegacyCache()
        {
            if (!AppModule.IsAlbum) return;
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
            "$RECYCLE.BIN", "$WinREAgent", "System Volume Information",
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
                        w.InternalBufferSize = 256 * 1024;
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
            sb.Append("{\"type\":\"albpaths\",\"perf\":").Append(JsonStr(aiPerfMode)).Append(",\"items\":[");
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
            SendAlbFolders();
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

        // 相册扫描暂停：工作线程在每个目录/每批文件前等在这里。
        // 不用挂起线程，避免退出扫描时留下挂起线程无法回收。
        void AlbScanWaitIfPaused()
        {
            while (albScanPaused && albRunning)
            {
                try { Thread.Sleep(120); } catch { break; }
            }
        }

        void SendAlbScanControl(string phase)
        {
            long done = 0;
            var ctx = albLiveScanCtx;
            if (ctx != null) { try { lock (ctx.gate) done = ctx.done; } catch { } }
            Send("{\"type\":\"albscan\",\"phase\":" + JsonStr(phase) + ",\"done\":" + done + ",\"total\":0}");
        }

        void AlbScanPause()
        {
            if (!albRunning) { Send("{\"type\":\"toast\",\"msg\":\"相册扫描当前没有运行\"}"); return; }
            albScanPaused = true;
            SendAlbScanControl("pause");
            Send("{\"type\":\"toast\",\"msg\":\"已暂停相册扫描\"}");
        }

        void AlbScanResume()
        {
            if (!albRunning || !albScanPaused) { Send("{\"type\":\"toast\",\"msg\":\"相册扫描当前没有暂停\"}"); return; }
            albScanPaused = false;
            SendAlbScanControl("resume");
            Send("{\"type\":\"toast\",\"msg\":\"已继续相册扫描\"}");
        }

        void AlbScanStartManual()
        {
            if (albRunning)
            {
                if (albScanPaused) AlbScanResume();
                else Send("{\"type\":\"toast\",\"msg\":\"相册扫描正在进行\"}");
                return;
            }
            StartAlbumScan(false, false);
        }

        void AlbScanRestartManual()
        {
            albScanPaused = false;
            if (albRunning)
            {
                albRescanPending = true;
                albRescanFullPending = true;
                Send("{\"type\":\"toast\",\"msg\":\"已排队重新扫描，当前扫描结束后开始\"}");
                return;
            }
            StartAlbumScan(false, true);
        }

        void StartAlbumScan(bool auto, bool full)
        {
            if (albRoots.Count == 0) { if (!auto) Send("{\"type\":\"toast\",\"msg\":\"请先添加相册路径\"}"); return; }
            AiCancelIdleIndex();
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
                albScanPaused = false;
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
            it.size = ce.size; it.taken = ce.taken; it.takenKnown = ce.takenKnown; it.w = ce.w; it.h = ce.h;
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
              .Append(",\"tk\":").Append(it.takenKnown ? 1 : 0)
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
              .Append(",\"m\":").Append(albMeta == null ? "{}" : albMeta.ItemJson(it.path, "{}"))
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
                var deduped = new List<AlbItem>(batch.Count);
                foreach (var it in batch)
                {
                    // 相册根目录允许重叠（例如同时添加 D:\Pictures 和它的子目录）。
                    // 同一物理文件会被多个根目录扫到，必须在合并时按路径去重。
                    if (ctx.itemPaths.Add(it.path)) deduped.Add(it);
                }
                if (deduped.Count == 0) return;
                ctx.items.AddRange(deduped);
                ctx.pending.AddRange(deduped);
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
                AlbScanWaitIfPaused();
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
                            if ((ctx.done & 31) == 0) AlbScanWaitIfPaused();
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
                        if ((ctx.done & 31) == 0) AlbScanWaitIfPaused();
                        string f = fi.FullName;
                        bool isImg = IsAlbImage(f);
                        string bn = Path.GetFileNameWithoutExtension(f);
                        if (!isImg && imgBase.ContainsKey(bn)) continue;
                        long fsize = 0, mt = 0, ctTicks = 0;
                        try { fsize = fi.Length; mt = fi.LastWriteTimeUtc.Ticks; ctTicks = fi.CreationTimeUtc.Ticks; } catch { }
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
                            it.taken = ce.taken; it.takenKnown = ce.takenKnown; it.w = ce.w; it.h = ce.h;
                            it.lat = ce.lat; it.lon = ce.lon; it.dur = ce.dur;
                            it.make = ce.make; it.model = ce.model; it.lens = ce.lens;
                            it.fnum = ce.fnum; it.exposure = ce.exposure; it.focal = ce.focal; it.iso = ce.iso;
                        }
                        else
                        {
                            ctx.changed = true;
                            // 没有 EXIF/视频元数据时，用文件创建时间兜底，并标记为“未知拍摄时间”。
                            // 这样排序时未知项永远排在已拍摄项之后，而不是混进拍摄时间序列里。
                            it.taken = ctTicks > 0 ? (long)((ctTicks - 621355968000000000L) / 10000000L)
                                : (mt > 0 ? (long)((mt - 621355968000000000L) / 10000000L) : 0);
                            it.takenKnown = false;
                            if (isImg)
                            {
                                ExifMeta meta;
                                if (ExifDetail(f, out meta))
                                {
                                    if (meta.taken > 0) { it.taken = meta.taken; it.takenKnown = true; }
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
                                if (Mp4Info(f, out du, out ct)) { it.dur = du; if (ct > 0) { it.taken = ct; it.takenKnown = true; } }
                            }
                            ce = new AlbCacheEnt();
                            ce.realPath = f; ce.size = fsize; ce.mtime = mt; ce.type = it.type;
                            ce.taken = it.taken; ce.takenKnown = it.takenKnown; ce.w = it.w; ce.h = it.h; ce.lat = it.lat; ce.lon = it.lon; ce.dur = it.dur;
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
                    "$RECYCLE.BIN", "System Volume Information", "$WinREAgent",
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
                        AlbScanWaitIfPaused();
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
                albLiveScanCtx = ctx;
                aiScanKickCount = 0;
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
                    if (albScanPaused) { AlbScanWaitIfPaused(); continue; }
                    AlbScanSendProgress(ctx, total);
                    AlbScanCheckpointNow(ctx);
                    AiKickScanIndex(ctx);
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
                    if (a.takenKnown != b.takenKnown) return a.takenKnown ? -1 : 1;
                    if (a.taken != b.taken) return b.taken.CompareTo(a.taken);
                    return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                });

                bool autoUpdate = albScanAuto || albItems.Count > 0;
                albItems = items;
                if (albLiveScanCtx == ctx) albLiveScanCtx = null;
                aiScanKickCount = 0;
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
                albLiveScanCtx = null;
                aiScanKickCount = 0;
                bool rescan = false;
                bool rescanFull = false;
                lock (albScanStateGate)
                {
                    albRunning = false;
                    albScanPaused = false;
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

        void SendAlbFolders()
        {
            try
            {
                var counts = new Dictionary<string, int>();
                foreach (var it in albItems)
                {
                    string k = (it.root ?? "") + "\t" + (it.folder ?? ".");
                    if (counts.ContainsKey(k)) counts[k]++; else counts[k] = 1;
                }
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"type\":\"albfolders\",\"items\":[");
                int n = 0;
                foreach (var kv in counts)
                {
                    if (n++ > 0) sb.Append(",");
                    string[] r = kv.Key.Split('\t');
                    sb.Append("{\"r\":").Append(JsonStr(r[0])).Append(",\"f\":").Append(JsonStr(r.Length > 1 ? r[1] : "."))
                      .Append(",\"n\":").Append(kv.Value).Append("}");
                }
                sb.Append("]}");
                Send(sb.ToString());
            }
            catch { }
        }

        void SendAlbMeta()
        {
            try { Send("{\"type\":\"albmeta\",\"data\":" + (albMeta == null ? "{}" : albMeta.ToJson()) + "}"); }
            catch { }
        }

        void SendPrivHash(string token, string password)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    byte[] d = sha.ComputeHash(Encoding.UTF8.GetBytes(password ?? ""));
                    var sb = new StringBuilder("sha256_");
                    foreach (byte b in d) sb.Append(b.ToString("x2"));
                    Send("{\"type\":\"albprivhash\",\"token\":" + JsonStr(token) + ",\"hash\":" + JsonStr(sb.ToString()) + "}");
                }
            }
            catch { }
        }

        string RenameOne(string src, string tpl, int seq)
        {
            string ext = Path.GetExtension(src);
            string t = (tpl ?? "").Trim();
            if (t.Length == 0) t = "{name}";
            string made = ExpandName(t, src, seq);
            // 模板自己带扩展名时不重复追加；否则沿用原扩展名。
            if (Path.GetExtension(made).Length == 0) made += ext;
            return made;
        }

        Dictionary<string, string> lastRenameUndo = null;
        void AlbumBatchRename(string payload)
        {
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(payload) as Dictionary<string, object>;
                if (d == null) return;
                var paths = new List<string>();
                var arr = JList(d["paths"]);
                if (arr != null) foreach (var o in arr) { string s = o as string; if (s != null) paths.Add(s); }
                string tpl = d.ContainsKey("template") ? Convert.ToString(d["template"]) : "{name}";
                int start = d.ContainsKey("start") ? Convert.ToInt32(d["start"]) : 1;
                bool preview = d.ContainsKey("preview") && Convert.ToBoolean(d["preview"]);
                var rows = new List<Dictionary<string, object>>();
                var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths.Count; i++)
                {
                    string src = paths[i];
                    if (!File.Exists(src)) continue;
                    string dir = Path.GetDirectoryName(src);
                    string nn = RenameOne(src, tpl, start + i);
                    string dst = Path.Combine(dir, nn);
                    bool conflict = dst.Equals(src, StringComparison.OrdinalIgnoreCase) == false && File.Exists(dst);
                    if (seen.Contains(dst.ToLowerInvariant())) conflict = true;
                    seen.Add(dst.ToLowerInvariant());
                    var row = new Dictionary<string, object>();
                    row["old"] = src; row["newPath"] = dst; row["name"] = nn; row["conflict"] = conflict;
                    rows.Add(row);
                    if (!conflict) mappings[src] = dst;
                }
                if (preview)
                {
                    Send("{\"type\":\"albrenamepreview\",\"items\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(rows) + "}");
                    return;
                }
                var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int ok = 0, fail = 0;
                foreach (var kv in mappings)
                {
                    try
                    {
                        if (File.Exists(kv.Value)) { fail++; continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(kv.Value));
                        File.Move(kv.Key, kv.Value);
                        done[kv.Key] = kv.Value;
                        ok++;
                    }
                    catch { fail++; }
                }
                lastRenameUndo = done;
                if (done.Count > 0)
                {
                    foreach (var kv in done)
                    {
                        var it = albItems.Find(x => string.Equals(x.path, kv.Key, StringComparison.OrdinalIgnoreCase));
                        if (it != null) { it.path = kv.Value; it.name = Path.GetFileName(kv.Value); }
                        if (albMeta != null)
                        {
                            var md = albMeta.GetItem(kv.Key);
                            if (md != null) { albMeta.SetItem(kv.Value, md); albMeta.SetItem(kv.Key, null); }
                        }
                    }
                    Send("{\"type\":\"albrenamed\",\"items\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(done) + ",\"ok\":" + ok + ",\"fail\":" + fail + "}");
                    Send("{\"type\":\"toast\",\"msg\":" + JsonStr("已重命名 " + ok + " 项" + (fail > 0 ? "，失败 " + fail + " 项" : "")) + "}");
                    StartAlbumScan(false, true);
                }
                else Send("{\"type\":\"toast\",\"msg\":\"没有可重命名的文件\"}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("批量重命名失败: " + ex.Message) + "}"); }
        }

        void AlbumBatchRenameUndo()
        {
            if (lastRenameUndo == null || lastRenameUndo.Count == 0) { Send("{\"type\":\"toast\",\"msg\":\"没有可撤销的重命名\"}"); return; }
            int ok = 0;
            var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in lastRenameUndo)
            {
                try
                {
                    if (File.Exists(kv.Value) && !File.Exists(kv.Key)) { File.Move(kv.Value, kv.Key); reverse[kv.Value] = kv.Key; ok++; }
                }
                catch { }
            }
            if (ok > 0)
            {
                foreach (var kv in reverse)
                {
                    var it = albItems.Find(x => string.Equals(x.path, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (it != null) { it.path = kv.Value; it.name = Path.GetFileName(kv.Value); }
                    if (albMeta != null)
                    {
                        var md = albMeta.GetItem(kv.Key);
                        if (md != null) { albMeta.SetItem(kv.Value, md); albMeta.SetItem(kv.Key, null); }
                    }
                }
                Send("{\"type\":\"albrenamed\",\"items\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(reverse) + ",\"ok\":" + ok + ",\"fail\":0}");
                StartAlbumScan(false, true);
            }
            lastRenameUndo = null;
            Send("{\"type\":\"toast\",\"msg\":" + JsonStr("已撤销重命名 " + ok + " 项") + "}");
        }

        void AlbumBatchMove(string payload)
        {
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(payload) as Dictionary<string, object>;
                if (d == null) return;
                string dst = d.ContainsKey("dst") ? Convert.ToString(d["dst"]) : "";
                bool copy = d.ContainsKey("copy") && Convert.ToBoolean(d["copy"]);
                if (string.IsNullOrEmpty(dst)) { Send("{\"type\":\"toast\",\"msg\":\"请先选择目标文件夹\"}"); return; }
                Directory.CreateDirectory(dst);
                var arr = JList(d["paths"]);
                var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int ok = 0, fail = 0;
                if (arr != null)
                {
                    foreach (var o in arr)
                    {
                        string src = o as string; if (src == null || !File.Exists(src)) continue;
                        string name = Path.GetFileName(src);
                        string target = Path.Combine(dst, name);
                        int n = 1;
                        while (File.Exists(target))
                        {
                            target = Path.Combine(dst, Path.GetFileNameWithoutExtension(name) + "_" + n + Path.GetExtension(name)); n++;
                        }
                        try
                        {
                            if (copy) File.Copy(src, target, false); else File.Move(src, target);
                            done[src] = target; ok++;
                        }
                        catch { fail++; }
                    }
                }
                if (done.Count > 0)
                {
                    foreach (var kv in done)
                    {
                        var it = albItems.Find(x => string.Equals(x.path, kv.Key, StringComparison.OrdinalIgnoreCase));
                        if (it != null && !copy) { it.path = kv.Value; it.name = Path.GetFileName(kv.Value); it.root = AlbRootOf(kv.Value); }
                        if (albMeta != null)
                        {
                            var md = albMeta.GetItem(kv.Key);
                            if (md != null && !copy) { albMeta.SetItem(kv.Value, md); albMeta.SetItem(kv.Key, null); }
                        }
                    }
                    Send("{\"type\":\"albrenamed\",\"items\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(done) + ",\"ok\":" + ok + ",\"fail\":" + fail + ",\"copy\":" + (copy ? "true" : "false") + "}");
                    bool underRoot = false;
                    foreach (var r in albRoots) if (dst.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { underRoot = true; break; }
                    if (!underRoot) AddAlbRoot(dst); else StartAlbumScan(false, true);
                }
                Send("{\"type\":\"toast\",\"msg\":" + JsonStr((copy ? "已复制 " : "已移动 ") + ok + " 项" + (fail > 0 ? "，失败 " + fail + " 项" : "")) + "}");
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("批量移动失败: " + ex.Message) + "}"); }
        }

        void AlbumImportPick(string kind)
        {
            BeginInvoke((Action)(() =>
            {
                string picked = "";
                try
                {
                    using (var d = new FolderBrowserDialog())
                    {
                        d.Description = kind == "src" ? "选择要导入的文件夹（SD 卡、手机、U 盘或普通文件夹）" : "选择导入目标文件夹";
                        d.ShowNewFolderButton = kind == "dst";
                        if (d.ShowDialog(this) == DialogResult.OK) picked = d.SelectedPath;
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(picked))
                    Send("{\"type\":\"albimportpath\",\"kind\":" + JsonStr(kind) + ",\"path\":" + JsonStr(picked) + "}");
                Send("{\"type\":\"appmode\",\"mode\":\"album\"}");
                try { web.CoreWebView2.ExecuteScriptAsync("try{setAppMode('album');openAlbum();}catch(e){}"); } catch { }
            }));
        }

        void AlbumImport(string payload)
        {
            var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(payload) as Dictionary<string, object>;
            if (d == null) return;
            string src = Convert.ToString(d["src"] ?? ""), dst = Convert.ToString(d["dst"] ?? "");
            bool move = d.ContainsKey("move") && Convert.ToBoolean(d["move"]);
            bool byDate = d.ContainsKey("byDate") && Convert.ToBoolean(d["byDate"]);
            bool skipDup = d.ContainsKey("skipDup") && Convert.ToBoolean(d["skipDup"]);
            if (!Directory.Exists(src) || string.IsNullOrEmpty(dst)) { Send("{\"type\":\"toast\",\"msg\":\"请先选择来源和目标文件夹\"}"); return; }
            new Thread(() =>
            {
                int total = 0, done = 0, copied = 0, skipped = 0, failed = 0;
                try
                {
                    Directory.CreateDirectory(dst);
                    var files = new List<string>();
                    string[] exts = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".mp4", ".mov", ".avi", ".mkv", ".m4v", ".3gp" };
                    foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                        if (Array.IndexOf(exts, Path.GetExtension(f).ToLowerInvariant()) >= 0) files.Add(f);
                    total = files.Count;
                    foreach (var f in files)
                    {
                        try
                        {
                            DateTime ft = File.GetLastWriteTime(f);
                            string sub = dst;
                            if (byDate) { sub = Path.Combine(dst, ft.ToString("yyyy"), ft.ToString("yyyy-MM-dd")); Directory.CreateDirectory(sub); }
                            string name = Path.GetFileName(f), target = Path.Combine(sub, name);
                            if (skipDup && File.Exists(target) && new FileInfo(target).Length == new FileInfo(f).Length) { skipped++; done++; continue; }
                            int n = 1;
                            while (File.Exists(target))
                            {
                                target = Path.Combine(sub, Path.GetFileNameWithoutExtension(name) + "_" + n + Path.GetExtension(name)); n++;
                            }
                            if (move) File.Move(f, target); else File.Copy(f, target, false);
                            copied++;
                        }
                        catch { failed++; }
                        done++;
                        if (done % 50 == 0 || done == total)
                            Send("{\"type\":\"albimportprogress\",\"done\":" + done + ",\"total\":" + total + ",\"copied\":" + copied + ",\"skipped\":" + skipped + ",\"failed\":" + failed + "}");
                    }
                    if (!string.IsNullOrEmpty(dst)) Send("{\"type\":\"albimported\",\"dst\":" + JsonStr(dst) + ",\"copied\":" + copied + ",\"skipped\":" + skipped + ",\"failed\":" + failed + "}");
                    if (albRoots.Count > 0) StartAlbumScan(false, true); else AddAlbRoot(dst);
                }
                catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("导入失败: " + ex.Message) + "}"); }
            }) { IsBackground = true }.Start();
        }

        void AlbumFaceAdmin(string payload)
        {
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(payload) as Dictionary<string, object>;
                if (d == null) return;
                string action = Convert.ToString(d["action"] ?? "");
                int gid = d.ContainsKey("gid") ? Convert.ToInt32(d["gid"]) : -1;
                int from = d.ContainsKey("from") ? Convert.ToInt32(d["from"]) : -1;
                int to = d.ContainsKey("to") ? Convert.ToInt32(d["to"]) : -1;
                string name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : "";
                bool hidden = d.ContainsKey("hidden") && Convert.ToBoolean(d["hidden"]);
                if (action == "rename" && gid >= 0 && albMeta != null) albMeta.SetPersonName(gid, name);
                else if (action == "hide" && gid >= 0 && albMeta != null) albMeta.SetPersonHidden(gid, hidden);
                else if ((action == "merge" || action == "split") && File.Exists(FaceStateFile()))
                {
                    var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var st = ser.DeserializeObject(File.ReadAllText(FaceStateFile())) as Dictionary<string, object>;
                    var images = st == null ? null : st["images"] as Dictionary<string, object>;
                    if (images == null) return;
                    if (action == "merge" && from >= 0 && to >= 0)
                    {
                        foreach (var kv in images)
                        {
                            var rec = kv.Value as Dictionary<string, object>;
                            var faces = rec == null ? null : JList(rec["faces"]);
                            if (faces == null) continue;
                            foreach (var fo in faces)
                            {
                                var face = fo as Dictionary<string, object>;
                                if (face != null && Convert.ToInt32(face["gid"]) == from) face["gid"] = to;
                            }
                        }
                        var thumbs = st["thumbs"] as Dictionary<string, object>;
                        if (thumbs != null && thumbs.ContainsKey(from.ToString()) && !thumbs.ContainsKey(to.ToString())) thumbs[to.ToString()] = thumbs[from.ToString()];
                    }
                    else if (action == "split")
                    {
                        var sel = JList(d["faces"]);
                        int maxGid = 0;
                        foreach (var kv in images)
                        {
                            var rec = kv.Value as Dictionary<string, object>;
                            var faces = rec == null ? null : JList(rec["faces"]);
                            if (faces == null) continue;
                            foreach (var fo in faces) { try { maxGid = Math.Max(maxGid, Convert.ToInt32((fo as Dictionary<string, object>)["gid"])); } catch { } }
                        }
                        int ng = maxGid + 1;
                        if (sel != null)
                        {
                            foreach (var so in sel)
                            {
                                var s = so as Dictionary<string, object>; if (s == null) continue;
                                string sp = Convert.ToString(s["p"] ?? "");
                                var rec = images[sp] as Dictionary<string, object>;
                                var faces = rec == null ? null : JList(rec["faces"]);
                                if (faces == null) continue;
                                foreach (var fo in faces)
                                {
                                    var face = fo as Dictionary<string, object>; if (face == null) continue;
                                    bool match = Convert.ToInt32(face["gid"]) == gid;
                                    foreach (string k in new[] { "x", "y", "w", "h" })
                                        if (match && s.ContainsKey(k) && Convert.ToInt32(face[k]) != Convert.ToInt32(s[k])) match = false;
                                    if (match) face["gid"] = ng;
                                }
                            }
                        }
                    }
                    string tmp = FaceStateFile() + ".tmp";
                    File.WriteAllText(tmp, ser.Serialize(st), new UTF8Encoding(false));
                    try { File.Replace(tmp, FaceStateFile(), null); } catch { try { File.Delete(FaceStateFile()); File.Move(tmp, FaceStateFile()); } catch { } }
                    try { File.Delete(FaceResultFile()); } catch { }
                }
                RebuildFaceGroupsFromState();
            }
            catch (Exception ex) { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("人物管理失败: " + ex.Message) + "}"); }
        }

        void RebuildFaceGroupsFromState()
        {
            try
            {
                if (!File.Exists(FaceStateFile())) return;
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var st = ser.DeserializeObject(File.ReadAllText(FaceStateFile())) as Dictionary<string, object>;
                if (st == null) return;
                var images = st["images"] as Dictionary<string, object>;
                var thumbsObj = st["thumbs"] as Dictionary<string, object>;
                var faces = new Dictionary<int, List<string>>();
                var thumbs = new Dictionary<int, string>();
                int total = 0;
                if (images != null)
                {
                    foreach (var kv in images)
                    {
                        var rec = kv.Value as Dictionary<string, object>;
                        var fl = rec == null ? null : JList(rec["faces"]);
                        if (fl == null) continue;
                        foreach (var fo in fl)
                        {
                            var face = fo as Dictionary<string, object>; if (face == null) continue;
                            int gid = Convert.ToInt32(face["gid"]);
                            if (!faces.ContainsKey(gid)) faces[gid] = new List<string>();
                            faces[gid].Add("{\"p\":" + JsonStr(kv.Key) + ",\"x\":" + Convert.ToInt32(face["x"]) + ",\"y\":" + Convert.ToInt32(face["y"]) +
                                            ",\"w\":" + Convert.ToInt32(face["w"]) + ",\"h\":" + Convert.ToInt32(face["h"]) + "}");
                            total++;
                        }
                    }
                }
                if (thumbsObj != null)
                {
                    foreach (var kv in thumbsObj)
                    {
                        var t = kv.Value as Dictionary<string, object>;
                        string b64 = t == null ? "" : Convert.ToString(t["b64"] ?? "");
                        if (b64.Length > 0) thumbs[Convert.ToInt32(kv.Key)] = b64;
                    }
                }
                string json = BuildFaceGroupsJson(faces, thumbs, albItems.Count, total, "done");
                faceLastJson = json;
                try { File.WriteAllText(FaceResultFile(), json, new UTF8Encoding(false)); } catch { }
                Send(json);
            }
            catch (Exception ex) { Log("人物分组重建失败: " + ex.Message); }
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
                int w = 0, h = 0; long sz = 0; long dt = 0; double du = 0, la = 0, lo = 0; bool tk = false;
                if (hit != null) { sz = hit.size; dt = hit.taken; tk = hit.takenKnown; du = hit.dur; la = hit.lat; lo = hit.lon; }
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
                     ",\"dt\":" + dt + ",\"tk\":" + (tk ? 1 : 0) + ",\"d\":" + (long)du +
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
        volatile bool facePaused = false;
        volatile bool faceResumePending = false;
        volatile bool faceRestartPending = false;
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

        void ResetFaceCache()
        {
            try { if (File.Exists(FaceStateFile())) File.Delete(FaceStateFile()); } catch { }
            try { if (File.Exists(FaceResultFile())) File.Delete(FaceResultFile()); } catch { }
            try { if (File.Exists(FaceSigFile())) File.Delete(FaceSigFile()); } catch { }
            faceLastJson = "";
        }

        void FacePause()
        {
            if (!faceRunning) { Send("{\"type\":\"toast\",\"msg\":\"人物分析当前没有运行\"}"); return; }
            Log("人物分析: 暂停请求");
            facePaused = true;
            faceStop = true;
            try { if (!string.IsNullOrEmpty(faceStopFile)) File.WriteAllText(faceStopFile, "stop"); } catch { }
            Send("{\"type\":\"albfaces\",\"phase\":\"pausing\"}");
        }

        void FaceResume()
        {
            Log("人物分析: 继续请求 running=" + faceRunning + " paused=" + facePaused);
            if (faceRunning && facePaused)
            {
                faceResumePending = true;
                Send("{\"type\":\"albfaces\",\"phase\":\"resume\"}");
                return;
            }
            facePaused = false;
            StartFaceScan(false);
        }

        void FaceRestart()
        {
            Log("人物分析: 重新请求 running=" + faceRunning + " paused=" + facePaused);
            facePaused = false;
            faceResumePending = false;
            if (faceRunning)
            {
                faceRestartPending = true;
                faceStop = true;
                try { if (!string.IsNullOrEmpty(faceStopFile)) File.WriteAllText(faceStopFile, "stop"); } catch { }
                Send("{\"type\":\"albfaces\",\"phase\":\"restarting\"}");
                return;
            }
            ResetFaceCache();
            StartFaceScan(false);
        }

        void StartFaceScan(bool auto)
        {
            if (faceRunning) { if (auto) faceAgain = true; else Send("{\"type\":\"toast\",\"msg\":\"人脸分析正在进行\"}"); return; }
            if (albItems.Count == 0) { if (!auto) Send("{\"type\":\"toast\",\"msg\":\"相册还没有照片\"}"); return; }
            bool cached = TryLoadFaceResult();
            if (cached)
            {
                Log("人物分析: 使用缓存结果");
                Send(faceLastJson);
                if (!auto) Send("{\"type\":\"toast\",\"msg\":\"人物分组已是最新\"}");
                return;
            }
            Log("人物分析: 启动新扫描 items=" + albItems.Count + " auto=" + auto);
            faceRunning = true; faceStop = false; facePaused = false;
            faceStopFile = FaceStateFile() + ".stop";
            try { if (File.Exists(faceStopFile)) File.Delete(faceStopFile); } catch { }
            var th = new Thread(FaceScanThread); th.IsBackground = true; th.Start();
        }

        string BuildFaceGroupsJson(Dictionary<int, List<string>> faces, Dictionary<int, string> thumbs, int photos, int facesTotal, string phase)
        {
            var order = new List<int>();
            foreach (var kv in faces) order.Add(kv.Key);
            order.Sort(delegate(int a, int b) { return faces[b].Count.CompareTo(faces[a].Count); });
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"type\":\"albfaces\",\"phase\":\"").Append(phase).Append("\",\"photos\":").Append(photos)
              .Append(",\"faces\":").Append(facesTotal).Append(",\"groups\":[");
            int shown = 0;
            foreach (var gid in order)
            {
                if (shown >= 80) break;
                if (shown > 0) sb.Append(",");
                var fl = faces[gid];
                string pname = albMeta == null ? "" : albMeta.PeopleName(gid);
                bool phide = albMeta != null && albMeta.PersonHidden(gid);
                sb.Append("{\"id\":").Append(gid).Append(",\"name\":").Append(JsonStr(pname))
                  .Append(",\"hidden\":").Append(phide ? "true" : "false")
                  .Append(",\"thumb\":")
                  .Append(thumbs.ContainsKey(gid) ? JsonStr("data:image/jpeg;base64," + thumbs[gid]) : "null")
                  .Append(",\"faces\":[").Append(string.Join(",", fl.ToArray())).Append("]}");
                shown++;
            }
            sb.Append("]}");
            return sb.ToString();
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
                Log("人物分析: 线程开始 photos=" + list.Count + " py=" + py + " script=" + script);
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
                var faceKeys = new HashSet<string>();
                int facesTotal = 0;
                long lastPartialTicks = 0;
                bool partialDirty = false;
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
                        partialDirty = true;
                    }
                    else if (f[0] == "F" && f.Length >= 7)
                    {
                        int gid = int.Parse(f[1]);
                        string fkey = f[1] + "|" + f[2] + "|" + f[3] + "|" + f[4] + "|" + f[5] + "|" + f[6];
                        if (faceKeys.Add(fkey))
                        {
                            if (!faces.ContainsKey(gid)) faces[gid] = new List<string>();
                            faces[gid].Add("{\"p\":" + JsonStr(f[2]) + ",\"x\":" + f[3] + ",\"y\":" + f[4] +
                                           ",\"w\":" + f[5] + ",\"h\":" + f[6] + "}");
                            facesTotal++;
                            partialDirty = true;
                        }
                    }
                    else if (f[0] == "E")
                    {
                        Log("人物分析脚本: " + line);
                        Send("{\"type\":\"toast\",\"msg\":" + JsonStr("人脸分析: " + line) + "}");
                    }
                    if (partialDirty && facesTotal > 0)
                    {
                        long nowTicks = DateTime.UtcNow.Ticks;
                        if (nowTicks - lastPartialTicks >= 250 * TimeSpan.TicksPerMillisecond)
                        {
                            Send(BuildFaceGroupsJson(faces, thumbs, list.Count, facesTotal, "partial"));
                            lastPartialTicks = nowTicks;
                            partialDirty = false;
                        }
                    }
                }
                int exitCode = -1;
                try { proc.WaitForExit(5000); exitCode = proc.ExitCode; } catch { }
                Log("人物分析: 进程结束 code=" + exitCode + " faces=" + facesTotal + " stop=" + faceStop + " paused=" + facePaused + " restartPending=" + faceRestartPending + " resumePending=" + faceResumePending);

                if (faceStop)
                {
                    Log("人物分析: 被停止 paused=" + facePaused + " restartPending=" + faceRestartPending);
                    Send("{\"type\":\"albfaces\",\"phase\":\"" + (facePaused ? "pause" : "stop") + "\"}");
                    return;
                }
                // 模型缺失等原因导致脚本异常退出时，不要把空结果写成缓存，否则以后会一直显示 0 个人物。
                if (exitCode != 0 && facesTotal == 0)
                {
                    Log("人物分析: 脚本失败 code=" + exitCode);
                    Send("{\"type\":\"albfaces\",\"phase\":\"error\",\"msg\":" + JsonStr("人脸分析失败，脚本退出码 " + exitCode) + "}");
                    return;
                }

                string faceJson = BuildFaceGroupsJson(faces, thumbs, list.Count, facesTotal, "done");
                faceLastJson = faceJson;
                Log("人物分析: 发送结果 groups=" + faces.Count + " faces=" + facesTotal + " photos=" + list.Count);
                Send(faceJson);
                SaveFaceResult(faceJson, scanSig);
            }
            catch (Exception ex)
            {
                Log("人物分析异常: " + ex);
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
                if (faceRestartPending)
                {
                    faceRestartPending = false;
                    facePaused = false;
                    ResetFaceCache();
                    StartFaceScan(false);
                }
                else if (faceResumePending)
                {
                    faceResumePending = false;
                    facePaused = false;
                    StartFaceScan(false);
                }
                else if (faceAgain)
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
            string dir = AppModule.ConfigDir();
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

        // 相册「选项 → 缓存位置」：选择新的缓存根目录。
        // 实际迁移在下次启动、WebView2 尚未打开缓存目录时执行，避免运行中移动被锁定的 webview2 文件。
        void ChooseCacheLocation()
        {
            try { Log("打开缓存位置选择对话框"); } catch { }
            string current = "";
            try { current = CacheStore.Root(); } catch { }
            string picked = null;
            try
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "选择新的缓存文件夹（所有缩略图、索引、WebView2 等缓存都会迁移到这里）";
                    d.ShowNewFolderButton = true;
                    if (Directory.Exists(current)) d.SelectedPath = current;
                    if (d.ShowDialog(this) == DialogResult.OK) picked = d.SelectedPath;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开缓存位置选择框失败：\n" + ex.Message, "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (string.IsNullOrEmpty(picked)) return;

            try
            {
                string selectedRoot = Path.GetFullPath(picked).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string oldRoot = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(selectedRoot, oldRoot, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("新缓存位置与当前位置相同，无需修改。", "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (string.Equals(selectedRoot, Path.GetFullPath(Application.StartupPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("不能把软件安装目录本身设为缓存目录，请选择其他文件夹。", "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (File.Exists(selectedRoot))
                {
                    MessageBox.Show("选择的位置是一个文件，请选择一个文件夹。", "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string driveRoot = null;
                try { driveRoot = Path.GetPathRoot(selectedRoot); } catch { }
                if (driveRoot != null &&
                    string.Equals(selectedRoot, driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("不能把磁盘根目录设为缓存目录，请选择磁盘内的一个文件夹。", "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!Directory.Exists(selectedRoot)) Directory.CreateDirectory(selectedRoot);
                bool selectedNonEmpty = Directory.Exists(selectedRoot) &&
                                        Directory.GetFileSystemEntries(selectedRoot).Length > 0;
                string newRoot = selectedRoot;
                string locationNote = "";
                if (selectedNonEmpty)
                {
                    // 用户选中的文件夹可能已经有照片或其他文件。为避免混在一起，
                    // 自动使用其下的专用子目录，而不是直接向这个文件夹写缓存。
                    newRoot = Path.Combine(selectedRoot, "ImageBatchToolCache");
                    if (File.Exists(newRoot))
                    {
                        MessageBox.Show("所选文件夹下存在同名文件 ImageBatchToolCache，请换一个文件夹。", "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Directory.CreateDirectory(newRoot);
                    locationNote = "\n\n所选文件夹不是空的，缓存将放在其下的 ImageBatchToolCache 子文件夹。";
                    if (Directory.GetFileSystemEntries(newRoot).Length > 0)
                        locationNote += "\n该子文件夹已有内容，将合并旧缓存。";
                }
                if (string.Equals(newRoot, oldRoot, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("新缓存位置与当前位置相同，无需修改。", "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (newRoot.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    oldRoot.StartsWith(newRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("新缓存位置不能位于当前缓存目录内部，也不能包含当前缓存目录。\n请选择其他文件夹。", "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 先验证目标目录可写，避免重启后才发现迁移不了。
                try
                {
                    Directory.CreateDirectory(newRoot);
                    string probe = Path.Combine(newRoot, ".ibt_cache_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("目标文件夹不可写：\n" + ex.Message, "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var confirm = MessageBox.Show(
                    "缓存位置将从：\n" + current + "\n\n迁移到：\n" + newRoot + locationNote +
                    "\n\n缓存位置修改后程序会自动重启，并在启动时移动旧缓存。是否继续？",
                    "缓存位置", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;

                CacheStore.RequestMove(newRoot);
                Log("缓存位置已安排迁移: " + current + " -> " + newRoot);
                MainForm.RestartMode = StartupMode;
                MainForm.RestartAfterCacheMove = true;
                // 让用户知道不是崩溃，而是按计划重新启动。
                try { Send("{\"type\":\"toast\",\"msg\":" + JsonStr("缓存位置已记录，正在重启并迁移缓存…") + "}"); } catch { }
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置缓存位置失败：\n" + ex.Message, "缓存位置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
                // 1.5) AI 语义搜索索引：先停掉索引/查询进程，再清掉 index.db 与 manifest
                try { AiCancelIdleIndex(); } catch { }
                try { AiStopAll(); } catch { }
                clearDir(Path.Combine(CacheRoot(), "search"));
                aiReady = false;
                aiIndexDone = false;
                aiIndexRunning = false;
                aiServerStarting = false;
                aiIndexDb = null;
                aiIndexStop = null;
                aiIndexUnavailable = false;
                aiLastQuery = "";
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
                    EditorFx.Apply(small, par);
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
                        } else Send("{\"type\":\"status\",\"text\":\"景深虚化失败: " + (BeautyRunner.LastError ?? "").Replace("\"","'") + "\"}");
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
                            Send("{\"type\":\"status\",\"text\":\"美颜相机: " + (BeautyRunner.LastError ?? "").Replace("\"","'") + "\"}");
                        }
                        else
                        {
                            Send("{\"type\":\"status\",\"text\":\"美颜相机失败: " + (BeautyRunner.LastError ?? "").Replace("\"","'") + "\"}");
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
                            Send("{\"type\":\"status\",\"text\":\"AI美颜: " + (BeautyRunner.LastError ?? "").Replace("\"","'") + "\"}");
                        }
                        else
                        {
                            Send("{\"type\":\"status\",\"text\":\"AI美颜失败: " + (BeautyRunner.LastError ?? "").Replace("\"","'") + "\"}");
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
            try
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
            catch
            {
                // 无系统 RAW 解码器时，读取 RAW 内嵌的 JPEG 预览（CR2/NEF/ARW/DNG 等常见）
                var raw = LoadEmbeddedJpeg(path);
                if (raw != null) return raw;
                throw;
            }
        }

        Bitmap LoadEmbeddedJpeg(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            Bitmap best = null;
            long bestArea = 0;
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;
                int end = -1;
                for (int j = i + 2; j + 1 < data.Length; j++)
                    if (data[j] == 0xFF && data[j + 1] == 0xD9) { end = j + 2; break; }
                if (end <= i + 1024) continue;
                try
                {
                    using (var ms = new MemoryStream(data, i, end - i))
                    using (var img = Image.FromStream(ms, true, true))
                    {
                        long area = (long)img.Width * img.Height;
                        if (area > bestArea)
                        {
                            if (best != null) best.Dispose();
                            best = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                            using (var g = Graphics.FromImage(best)) g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
                            bestArea = area;
                        }
                    }
                }
                catch { }
                i = end - 1;
            }
            if (best != null) Log("RAW 预览回退: " + Path.GetFileName(path) + " " + best.Width + "x" + best.Height);
            return best;
        }

        // 加载 + 裁切（不套 LUT，缓存这个原图）
        Bitmap LoadBase(string path)
        {
            var src = LoadOriented(path);
            double lvlAngle = par.ContainsKey("level_angle") ? par["level_angle"] : 0;
            if (par.ContainsKey("level_on") && par["level_on"] > 0.5 && Math.Abs(lvlAngle) > 0.01)
                src = LevelRotate(src, lvlAngle);
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

        Bitmap LevelRotate(Bitmap src, double angleDeg)
        {
            double a = angleDeg * Math.PI / 180.0;
            int w = src.Width, h = src.Height;
            double ca = Math.Abs(Math.Cos(a)), sa = Math.Abs(Math.Sin(a));
            int rw = Math.Max(2, (int)Math.Ceiling(w * ca + h * sa));
            int rh = Math.Max(2, (int)Math.Ceiling(w * sa + h * ca));
            var canvas = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(rw / 2f, rh / 2f);
                g.RotateTransform((float)angleDeg);
                g.DrawImage(src, new RectangleF(-w / 2f, -h / 2f, w, h));
            }
            src.Dispose();

            double lo = 0, hi = 1;
            for (int it = 0; it < 24; it++)
            {
                double mid = (lo + hi) * 0.5;
                double cw2 = w * mid * 0.5, ch2 = h * mid * 0.5;
                bool inside = true;
                double cosA = Math.Cos(a), sinA = Math.Sin(a);
                double[] xs = { -cw2, cw2, cw2, -cw2 }, ys = { -ch2, -ch2, ch2, ch2 };
                for (int k = 0; k < 4; k++)
                {
                    double x0 = xs[k] * cosA + ys[k] * sinA;
                    double y0 = -xs[k] * sinA + ys[k] * cosA;
                    if (Math.Abs(x0) > w * 0.5 - 1 || Math.Abs(y0) > h * 0.5 - 1) { inside = false; break; }
                }
                if (inside) lo = mid; else hi = mid;
            }
            int cw3 = Math.Max(2, (int)(w * lo)), ch3 = Math.Max(2, (int)(h * lo));
            var cropped = new Bitmap(cw3, ch3, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(canvas, new Rectangle(0, 0, cw3, ch3), new Rectangle((rw - cw3) / 2, (rh - ch3) / 2, cw3, ch3), GraphicsUnit.Pixel);
            }
            canvas.Dispose();
            return cropped;
        }
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

        // v2.41：AI 优化只由 ai_opt 显式开关触发；有 LUT 时在 LUT 之后执行，无 LUT 时直接执行。
        void ApplyAiOptimize(Bitmap src)
        {
            AiRunner.LutIsLogHint = false;
            AiRunner.LutBlackHint = 0.0;
            AiRunner.LutWhiteHint = 1.0;
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var data = src.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            byte[] buf = new byte[stride * src.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            int W = src.Width, H = src.Height;
            bool aiOk = AiRunner.ProcessImage(buf, W, H, stride);
            Log("AI优化pass: " + aiOk + " " + AiRunner.LastError);
            if (aiOk)
            {
                double aWbR = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbR));
                double aWbG = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbG));
                double aWbB = Math.Max(0.8, Math.Min(1.2, AiRunner.OutWbB));
                double aExp = Math.Max(0.85, Math.Min(1.2, AiRunner.OutExposure));
                double aCon = Math.Max(0.9, Math.Min(1.15, AiRunner.OutContrast));
                double aSat = Math.Max(0.85, Math.Min(1.15, AiRunner.OutSaturation));
                double aHl = Math.Max(0.85, Math.Min(1.0, AiRunner.OutHighlights));
                double aSh = Math.Max(1.0, Math.Min(1.15, AiRunner.OutShadows));
                bool doHl = aHl < 0.999, doSh = aSh > 1.001;
                for (int y = 0; y < H; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < W; x++)
                    {
                        int i = row + x * 4;
                        double b = buf[i], g = buf[i+1], r = buf[i+2];
                        r *= aWbR * aExp; g *= aWbG * aExp; b *= aWbB * aExp;
                        double lum = 0.299*r + 0.587*g + 0.114*b;
                        if (doHl && lum > 180)
                        {
                            double t = Math.Min(1.0, (lum - 180) / 75.0);
                            double hg = 1.0 - (1.0 - aHl) * t;
                            r *= hg; g *= hg; b *= hg;
                        }
                        if (doSh && lum < 70)
                        {
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
            // v2.41：无 LUT 时不再隐式自动提亮。
            // 「自动优化」只由 color_auto 显式开关在 ApplyColor() 执行；AI 优化只由 ai_opt 显式开关执行。
            if (activeLut == null) {
                if (aiOptOn) ApplyAiOptimize(src);
                Log("ApplyLUT: LUT未加载，跳过隐式增强");
                return;
            }
            Log("ApplyLUT开始: " + src.Width + "x" + src.Height + " mode=" + lutMode);

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
                try {
                    lock (GpuLock) {
                        gpuDone = Native.gpu_lut_apply(buf, W, H, stride, tbl, lutSize, inMin, inMax,
                            lutStrength, chroma, wbGainArr) != 0;
                    }
                } catch { gpuDone = false; }
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
            // 统一量纲：temp/tint 滑块 ±100 → 着色器 ±1；暗部/高光滑块 ±10 → ±1。
            // 旧代码把滑块原值直接传给 GPU 着色器，temp=1 就被放大成 ±0.15（≈±38 级），
            // 红/蓝迅速饱和，只剩绿色，表现为“调整色温后全屏变绿”。
            float tpN = tp / 100f;
            float tiN = ti / 100f;
            float shN = sh / 10f;
            float hlN = hl / 10f;
            bool doVig = vig > 0.001f;
            bool doShadow = Math.Abs(shN) > 0.001f || Math.Abs(hlN) > 0.001f;
            bool doFc = fc > 0.001f;

            int W = bmp.Width, H = bmp.Height;
            var rect = new Rectangle(0,0,W,H);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int bytes = stride * H;
            byte[] buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);

            // GPU 优先：D3D11 compute shader。暗部/高光交给后面统一的 C# pass，
            // 避免着色器与 CPU 回退使用不同的暗部公式。
            bool usedNative = false;
            if (!ForceCpuColor && !ForceCSharpColor) {
                try {
                    lock (GpuLock) {
                        usedNative = Native.gpu_color_adjust(buf, W, H, stride,
                            sat, con, bri, 0f, 0f, tpN, tiN, vig) != 0;
                    }
                } catch { usedNative = false; }
            }
            // CPU 原生 C++ 回退（imgproc.dll 的色温/色调接口以 100 为中性）
            if (!usedNative && !ForceCSharpColor) try {
                Native.color_adjust(buf, W, H, stride,
                    sat * 100f, con * 100f, bri * 100f,
                    100f, 100f,
                    100f + tp, 100f + ti, 100f + vig * 100f);
                usedNative = true;
            } catch { usedNative = false; }

            // C# 兜底：与 GPU/原生保持同一量纲和运算顺序
            if (!usedNative)
            Parallel.For(0, H, y =>
            {
                int row = y * stride;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x*4;
                    double b = buf[i], g = buf[i+1], r = buf[i+2];
                    if (sat != 1.0) {
                        double gray = 0.299*r+0.587*g+0.114*b;
                        r = gray+(r-gray)*sat; g = gray+(g-gray)*sat; b = gray+(b-gray)*sat;
                    }
                    if (con != 1.0) {
                        r = (r-128)*con+128; g = (g-128)*con+128; b = (b-128)*con+128;
                    }
                    if (bri != 1.0) { r *= bri; g *= bri; b *= bri; }
                    if (tpN != 0) { r += tpN*38.25; b -= tpN*38.25; }
                    if (tiN != 0) { g -= tiN*25.5; }
                    if (doVig) {
                        double dx = (x-W/2.0)/(W/2.0);
                        double dy = (y-H/2.0)/(H/2.0);
                        double dist = Math.Sqrt(dx*dx+dy*dy);
                        double f = 1 - vig*dist*dist*0.8;
                        if (f < 0) f = 0;
                        r *= f; g *= f; b *= f;
                    }
                    buf[i]   = (byte)Math.Max(0, Math.Min(255, b));
                    buf[i+1] = (byte)Math.Max(0, Math.Min(255, g));
                    buf[i+2] = (byte)Math.Max(0, Math.Min(255, r));
                }
            });

            // 胶片对比（C# 保留，GPU/CPU 两条路径都执行一次）
            if (doFc) {
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

            // 暗部/高光：加法亮度调整，按亮度遮罩渐进，避免旧 GPU 公式把色度成倍放大。
            if (doShadow) {
                Parallel.For(0, H, y => {
                    int row = y*stride;
                    for (int x = 0; x < W; x++) {
                        int i = row + x*4;
                        double b = buf[i], g = buf[i+1], r = buf[i+2];
                        double lum = (0.299*r+0.587*g+0.114*b)/255.0;
                        double shMask = Math.Max(0, 1.0 - lum*2.0);
                        double hlMask = Math.Max(0, (lum-0.5)*2.0);
                        double adj = shN*0.20*shMask*255.0 - hlN*0.18*hlMask*255.0;
                        r += adj; g += adj; b += adj;
                        buf[i]   = (byte)Math.Max(0, Math.Min(255, b));
                        buf[i+1] = (byte)Math.Max(0, Math.Min(255, g));
                        buf[i+2] = (byte)Math.Max(0, Math.Min(255, r));
                    }
                });
            }

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
                lock (GpuLock) {
                    for (int pass = 0; pass < 3; pass++) {
                        if (Native.gpu_blur(a1, w, h, srcStride, r) == 0) break;
                        if (pass == 2) gpuBlur = true;
                    }
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
                EditorFx.Apply(bmp, par);
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
            string fmt = parS.ContainsKey("out_fmt") ? parS["out_fmt"].Trim().ToLowerInvariant() : "jpg";
            string ext = fmt == "png" ? ".png" : (fmt == "webp" ? ".webp" : ".jpg");
            int mode = par.ContainsKey("dup_mode") ? (int)par["dup_mode"] : 1;
            bool resume = par.ContainsKey("skip_existing") && par["skip_existing"] > 0.5;
            string first = Path.Combine(dir, baseName + ext);
            if (resume && File.Exists(first))
            {
                if (new FileInfo(first).Length > 0) { skip = true; return first; }
                // 0 字节残留视为未完成导出，直接覆盖重导，不再生成 _2。
                return first;
            }
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
                    if (!SaveOutput(outBmp, dst, q)) { RecordFail(f, "编码输出失败"); return false; }

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

        bool SaveOutput(Bitmap bmp, string dst, long q)
        {
            string fmt = parS.ContainsKey("out_fmt") ? parS["out_fmt"].Trim().ToLowerInvariant() : "jpg";
            try
            {
                if (fmt == "png")
                {
                    bmp.Save(dst, ImageFormat.Png);
                    return true;
                }
                if (fmt == "webp")
                {
                    string tmp = CacheStore.TempFile("webp_", ".png");
                    bmp.Save(tmp, ImageFormat.Png);
                    string err;
                    string res = PyTools.Run(BeautyRunner.PythonExe, "savefmt.py",
                        new string[] { tmp, dst, "webp", q.ToString() }, 60000, out err);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    bool ok = res != null && res.Replace(" ", "").Contains("\"ok\":true");
                    if (!ok) Log("WebP 编码失败: " + (err ?? "") + " " + (res ?? ""));
                    return ok;
                }
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, q);
                bmp.Save(dst, LUT.JpegCodec, ep);
                return true;
            }
            catch (Exception ex)
            {
                Log("输出编码失败: " + ex.Message);
                return false;
            }
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
            // 批次运行期间再次点“开始”会重置 stopFlag、并发写同一输出列表，
            // 导致停止提示丢失甚至两个批次互相踩状态；这里直接拒绝第二次启动。
            if (running) {
                Send("{\"type\":\"toast\",\"msg\":\"正在导出，请先等待完成或点击停止\"}");
                return;
            }
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
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception ex) {
                Send("{\"type\":\"toast\",\"msg\":\"无法创建输出目录：" + ex.Message.Replace("\"", "'") + "\"}");
                return;
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
            if (batchParams.Count > 0) workers = 1; // 逐张参数导出必须串行，避免共享 par 竞争
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
                            Dictionary<string, double> savedPar = null;
                            Dictionary<string, double> bp = null;
                            if (batchParams.Count > 0 && batchParams.TryGetValue(list[idx], out bp)) {
                                savedPar = new Dictionary<string, double>(par);
                                var merged = new Dictionary<string, double>(savedPar);
                                foreach (var kv in bp) merged[kv.Key] = kv.Value;
                                par.Clear();
                                foreach (var kv in merged) par[kv.Key] = kv.Value;
                            }
                            try {
                                if (!ExportOne(list[idx], idx + 1)) Interlocked.Increment(ref failCount);
                            } finally {
                                if (savedPar != null) { par.Clear(); foreach (var kv in savedPar) par[kv.Key] = kv.Value; }
                            }
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

            bool wasStopped = stopFlag;
            forceFreshAnalysis = false;
            ResetAiCache();
            running = false; pauseFlag = false;
            Send("{\"type\":\"progress\",\"done\":" + done + ",\"total\":" + total + "}");
            Send("{\"type\":\"runstate\",\"running\":false,\"paused\":false}");
            Log("渲染结束: 用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s，失败 " + failCount);

            int realFails;
            lock (failList) realFails = failList.Count;
            if (wasStopped) {
                Send("{\"type\":\"toast\",\"msg\":\"已停止：完成 " + done + "/" + total + " 张\"}");
            }
            else if (realFails > 0) {
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

        // ===== AI 语义搜索 =====
        string AiModelDir()
        {
            return Path.Combine(Application.StartupPath, "ai", "models", "cnclip");
        }

        string AiYoloDir()
        {
            return Path.Combine(Application.StartupPath, "ai", "models", "yolox");
        }

        string AiSearchDir()
        {
            return CacheStore.Sub("search");
        }

        string AiPythonExe()
        {
            string p = Path.Combine(Application.StartupPath, "python", "python.exe");
            if (File.Exists(p)) return p;
            return "python.exe";
        }

        static string AiQ(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";
        }

        static int AiInt(string s, string key)
        {
            try
            {
                string pat = "\"" + key + "\":";
                int i = s.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return 0;
                i += pat.Length;
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                int start = i;
                if (i < s.Length && s[i] == '-') i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                int v;
                return int.TryParse(s.Substring(start, i - start), out v) ? v : 0;
            }
            catch { return 0; }
        }

        static string AiText(string s, string key)
        {
            try
            {
                string pat = "\"" + key + "\":\"";
                int i = s.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return "";
                i += pat.Length;
                int j = i;
                while (j < s.Length && s[j] != '"') j++;
                return s.Substring(i, j - i);
            }
            catch { return ""; }
        }

        static bool AiBool(string s, string key)
        {
            try
            {
                string pat = "\"" + key + "\":";
                int i = s.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return false;
                i += pat.Length;
                return i + 4 <= s.Length && s.Substring(i, 4) == "true";
            }
            catch { return false; }
        }

        // 索引性能档：low=省资源，mid=均衡，high=性能拉满。
        string AiPerfFile()
        {
            try
            {
                string dir = AppModule.ConfigDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "ai_perf.txt");
            }
            catch { return Path.Combine(CacheSub("config"), "ai_perf.txt"); }
        }

        string NormalizeAiPerf(string mode)
        {
            mode = (mode ?? "").Trim().ToLowerInvariant();
            if (mode == "low" || mode == "high" || mode == "mid") return mode;
            return "mid";
        }

        void LoadAiPerf()
        {
            try
            {
                string f = AiPerfFile();
                if (File.Exists(f)) aiPerfMode = NormalizeAiPerf(File.ReadAllText(f));
            }
            catch { aiPerfMode = "mid"; }
        }

        void SaveAiPerf(string mode)
        {
            try { File.WriteAllText(AiPerfFile(), NormalizeAiPerf(mode), new UTF8Encoding(false)); } catch { }
        }

        int AiPerfThreads()
        {
            int cores = Math.Max(1, Environment.ProcessorCount);
            if (aiPerfMode == "low") return 1;
            if (aiPerfMode == "high") return cores;
            return Math.Max(1, Math.Min(4, cores));
        }

        // 切换档位时如果正在索引：写 stop 让 Python 提交当前事务后退出，
        // 退出事件会用新档位重新启动，已处理项不会重算。
        void ApplyAiPerf(string mode)
        {
            mode = NormalizeAiPerf(mode);
            bool changed = !string.Equals(aiPerfMode, mode, StringComparison.Ordinal);
            aiPerfMode = mode;
            SaveAiPerf(mode);
            Send("{\"type\":\"albperf\",\"mode\":" + JsonStr(mode) + "}");
            if (!changed)
            {
                Send("{\"type\":\"toast\",\"msg\":" + JsonStr("索引性能已是" + (mode == "low" ? "低挡" : mode == "high" ? "高挡" : "中挡")) + "}");
                return;
            }
            if (aiIndexRunning)
            {
                aiReindexPending = true;
                bool stopped = false;
                try
                {
                    if (!string.IsNullOrEmpty(aiIndexStop))
                    {
                        File.WriteAllText(aiIndexStop, "stop");
                        stopped = true;
                    }
                }
                catch { }
                Send("{\"type\":\"toast\",\"msg\":" + JsonStr(stopped
                    ? "已切换索引性能，当前索引将按新挡位继续"
                    : "已切换索引性能，下一轮索引生效") + "}");
            }
            else
            {
                Send("{\"type\":\"toast\",\"msg\":" + JsonStr("索引性能已切换为" + (mode == "low" ? "低挡（省资源）" : mode == "high" ? "高挡（性能拉满）" : "中挡（均衡）")) + "}");
            }
        }

        void AiCancelIdleIndex()
        {
            try
            {
                if (aiIdleTimer != null)
                {
                    aiIdleTimer.Dispose();
                    aiIdleTimer = null;
                }
                aiIdleSeq++;
            }
            catch { }
        }

        // 相册扫描/自动更新完成后，延迟 12 秒启动索引；仍在扫描则继续推迟。
        // 用户打开搜索时会立即调用 AiEnsureSearch，并取消这里等待中的任务。
        void AiScheduleIdleIndex()
        {
            try
            {
                if (aiIndexPaused) return;
                if (albItems == null || albItems.Count == 0) return;
                AiCancelIdleIndex();
                int seq = aiIdleSeq;
                aiIdleTimer = new System.Threading.Timer(delegate
                {
                    try
                    {
                        if (seq != aiIdleSeq) return;
                        if (albRunning)
                        {
                            AiScheduleIdleIndex();
                            return;
                        }
                        AiEnsureSearch(true);
                    }
                    catch { }
                }, null, 12000, System.Threading.Timeout.Infinite);
            }
            catch { }
        }

        // 用一个门闩保证同一时刻只启动一个索引进程，避免扫描线程/详情页/定时器并发打进来。
        bool AiTryBeginIndex()
        {
            lock (aiIndexGate)
            {
                if (aiIndexRunning || aiServerStarting) return false;
                aiReindexPending = false;
                aiIndexRunning = true;
                return true;
            }
        }

        // 扫描主循环每隔 2 秒调用：有第一批结果就开始建索引；索引正在跑则把后续新增项排队。
        void AiKickScanIndex(AlbScanCtx ctx)
        {
            try
            {
                if (ctx == null || aiIndexUnavailable || aiIndexPaused) return;
                int count;                lock (ctx.gate) count = ctx.items.Count;
                if (count <= 0) return;
                if (aiIndexRunning || aiServerStarting)
                {
                    if (count - aiScanKickCount >= 100)
                    {
                        aiScanKickCount = count;
                        aiReindexPending = true;
                    }
                    return;
                }
                aiScanKickCount = count;
                AiEnsureSearch(true);
            }
            catch { }
        }

        // 查看详情时把当前图片写成优先项，索引脚本会在下一张前把它提到队首。
        void AiMarkPriority(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string dir = AiSearchDir();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "priority.txt"), path, new UTF8Encoding(false));
            }
            catch { }
        }

        // 详情页点击后：如果索引还没完成，或库里条目少于当前相册条目，就立即启动/续跑索引。
        void AiEnsureViewerPriority(string path)
        {
            try
            {
                if (aiIndexPaused) return;
                AiMarkPriority(path);
                if (aiIndexRunning || aiServerStarting)
                {
                    // 扫描期间索引脚本拿到的 manifest 可能还不完整；当前图片的
                    // 优先级先写入，等这一轮结束后再用完整清单续跑一次。
                    if (albLiveScanCtx != null) aiReindexPending = true;
                    return;
                }
                int count = AiCurrentItemCount();
                if (count > 0 && (!aiIndexDone || aiIndexItemCount < count)) AiEnsureSearch(true);
            }
            catch { }
        }

        // 在「搜索」打开或相册重新扫描完成后调用。force=true 时即使已有索引也重新核对新增/变更文件。
        void AiEnsureSearch()
        {
            AiEnsureSearch(false);
        }

        void AiPauseIndex()
        {
            if (!aiIndexRunning)
            {
                Send("{\"type\":\"albaiindex\",\"phase\":\"paused\"}");
                Send("{\"type\":\"toast\",\"msg\":\"标签索引当前没有运行\"}");
                return;
            }
            aiIndexPaused = true;
            aiIndexResumePending = false;
            aiIndexRestartPending = false;
            try { if (!string.IsNullOrEmpty(aiIndexStop)) File.WriteAllText(aiIndexStop, "stop"); } catch { }
            Send("{\"type\":\"albaiindex\",\"phase\":\"pausing\"}");
            Send("{\"type\":\"toast\",\"msg\":\"正在暂停标签索引…\"}");
        }

        void AiResumeIndex()
        {
            aiIndexPaused = false;
            if (!aiIndexRunning)
            {
                aiIndexResumePending = false;
                AiEnsureSearch(true);
                return;
            }
            aiIndexResumePending = true;
            Send("{\"type\":\"albaiindex\",\"phase\":\"resume\"}");
            Send("{\"type\":\"toast\",\"msg\":\"正在继续标签索引…\"}");
        }

        void AiRestartIndex()
        {
            aiIndexPaused = false;
            aiIndexResumePending = false;
            if (!aiIndexRunning)
            {
                aiIndexRestartPending = false;
                AiEnsureSearch(true);
                return;
            }
            aiIndexRestartPending = true;
            try { if (!string.IsNullOrEmpty(aiIndexStop)) File.WriteAllText(aiIndexStop, "stop"); } catch { }
            Send("{\"type\":\"albaiindex\",\"phase\":\"restarting\"}");
            Send("{\"type\":\"toast\",\"msg\":\"正在重新启动标签索引…\"}");
        }

        void AiEnsureSearch(bool force)
        {
            try
            {
                if (aiIndexPaused) return;
                AiCancelIdleIndex();
                if (aiIndexRunning || aiServerStarting)
                {
                    if (force) aiReindexPending = true;
                    return;
                }
                if (aiReady && !force) return;
                if (AiCurrentItemCount() == 0)
                {
                    Send("{\"type\":\"albaiindex\",\"phase\":\"idle\",\"done\":0,\"total\":0}");
                    return;
                }
                if (!File.Exists(Path.Combine(AiModelDir(), "cnclip_vitb16.img.fp32.onnx")) ||
                    !File.Exists(Path.Combine(AiModelDir(), "cnclip_vitb16.txt.fp32.onnx")))
                {
                    aiIndexUnavailable = true;
                    Send("{\"type\":\"albaiindex\",\"phase\":\"unavailable\",\"reason\":\"model_missing\",\"done\":0,\"total\":0}");
                    return;
                }
                if (aiIndexDone && !force && aiQueryProc == null)
                {
                    AiStartServer();
                    return;
                }
                // 相册仍在扫描时使用“部分索引”模式：只增量写入当前已扫到的条目，
                // 不删除旧索引；扫描结束后的完整索引再统一清理幽灵项。
                bool partialIndex = albLiveScanCtx != null;
                if (!AiTryBeginIndex())
                {
                    if (force) aiReindexPending = true;
                    return;
                }
                Thread t = new Thread(() =>
                {
                    try
                    {
                        string dir = AiSearchDir();
                        aiIndexDb = Path.Combine(dir, "index.db");
                        aiIndexStop = Path.Combine(dir, "index.stop");
                        string priorityFile = Path.Combine(dir, "priority.txt");
                        try { if (File.Exists(aiIndexStop)) File.Delete(aiIndexStop); } catch { }
                        string manifest = Path.Combine(dir, "manifest.json");
                        AiBuildManifest(manifest);
                        AiStartIndex(manifest, aiIndexDb, aiIndexStop, partialIndex, priorityFile);
                    }
                    catch (Exception ex)
                    {
                        aiIndexRunning = false;
                        Send("{\"type\":\"albaiindex\",\"phase\":\"error\",\"reason\":" + JsonStr(ex.Message) + ",\"done\":0,\"total\":0}");
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex)
            {
                aiIndexRunning = false;
                Send("{\"type\":\"albaiindex\",\"phase\":\"error\",\"reason\":" + JsonStr(ex.Message) + ",\"done\":0,\"total\":0}");
            }
        }

        // 扫描期间优先使用实时扫描清单；扫描结束后再用完整 albItems。
        List<AlbItem> AiManifestItems()
        {
            var live = albLiveScanCtx;
            if (live != null)
            {
                lock (live.gate) return new List<AlbItem>(live.items);
            }
            return albItems;
        }

        int AiCurrentItemCount()
        {
            var live = albLiveScanCtx;
            if (live != null)
            {
                lock (live.gate) return live.items.Count;
            }
            return albItems == null ? 0 : albItems.Count;
        }

        void AiBuildManifest(string manifestPath)
        {
            var items = AiManifestItems();
            var sb = new StringBuilder(Math.Max(1024, (items == null ? 0 : items.Count) * 180));
            sb.Append("{\"items\":[");
            int n = 0;
            if (items != null)
            {
                foreach (var it in items)
                {
                    if (it == null || string.IsNullOrEmpty(it.path)) continue;
                    try
                    {
                        if (!File.Exists(it.path)) continue;
                        var fi = new FileInfo(it.path);
                        string thumb = AlbThumbFileFor(it.path, fi.Length, fi.LastWriteTimeUtc.Ticks);
                        if (thumb == null || !File.Exists(thumb)) thumb = "";
                        if (n > 0) sb.Append(',');
                        sb.Append("{\"p\":").Append(JsonStr(it.path))
                          .Append(",\"mt\":").Append(fi.LastWriteTimeUtc.Ticks)
                          .Append(",\"sz\":").Append(fi.Length)
                          .Append(",\"t\":").Append(JsonStr(it.type ?? ""))
                          .Append(",\"thumb\":").Append(JsonStr(thumb))
                          .Append("}");
                        n++;
                    }
                    catch { }
                }
            }
            sb.Append("]}");
            File.WriteAllText(manifestPath, sb.ToString(), new UTF8Encoding(false));
        }

        void AiStartIndex(string manifest, string db, string stopFile, bool partial, string priorityFile)
        {
            try
            {
                string perfMode = NormalizeAiPerf(aiPerfMode);
                int perfThreads = perfMode == "low" ? 1 : (perfMode == "high" ? Math.Max(1, Environment.ProcessorCount) : Math.Max(1, Math.Min(4, Environment.ProcessorCount)));
                string script = Path.Combine(Application.StartupPath, "ai", "search_index.py");
                if (!File.Exists(script)) throw new Exception("search_index.py 不存在");
                var psi = new ProcessStartInfo();
                psi.FileName = AiPythonExe();
                psi.Arguments = "-u " + AiQ(script) + " --manifest " + AiQ(manifest) + " --db " + AiQ(db) +
                                " --model-dir " + AiQ(AiModelDir()) + " --yolo-dir " + AiQ(AiYoloDir()) + " --stop " + AiQ(stopFile) +
                                " --perf " + AiQ(perfMode) + " --gpu " + (perfMode == "low" ? "0" : "1") +
                                " --partial " + (partial ? "1" : "0") + " --priority " + AiQ(priorityFile);
                psi.WorkingDirectory = Application.StartupPath;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                try { psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; } catch { }
                try { psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1"; } catch { }
                try
                {
                    string nth = perfThreads.ToString();
                    psi.EnvironmentVariables["OMP_NUM_THREADS"] = nth;
                    psi.EnvironmentVariables["OPENBLAS_NUM_THREADS"] = nth;
                    psi.EnvironmentVariables["MKL_NUM_THREADS"] = nth;
                }
                catch { }
                var p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    try
                    {
                        string line = e.Data;
                        string phase = AiText(line, "phase");
                        int done = AiInt(line, "done");
                        int total = AiInt(line, "total");
                        int count = AiInt(line, "count");
                        int added = AiInt(line, "added");
                        int ocrAdded = AiInt(line, "ocr_added");
                        int ocrFlag = AiInt(line, "ocr");
                        int objAdded = AiInt(line, "obj_added");
                        int objFlag = AiInt(line, "obj");
                        if (total > 0) aiIndexTotal = total;
                        if (done >= 0) aiIndexDoneCount = Math.Min(done, Math.Max(total, done));
                        if (count > 0) aiIndexItemCount = count;
                        if (phase == "done")
                        {
                            aiIndexDone = true;
                            aiIndexDoneCount = Math.Max(aiIndexDoneCount, total);
                            if (!string.IsNullOrEmpty(aiPendingTagPath)) AiStartServer();
                        }
                        if (phase == "unavailable" || phase == "error")
                        {
                            aiIndexRunning = false;
                            aiIndexUnavailable = true;
                        }
                        Send("{\"type\":\"albaiindex\",\"phase\":" + JsonStr(phase) +
                             ",\"done\":" + Math.Max(0, done) + ",\"total\":" + Math.Max(0, total) +
                             ",\"count\":" + Math.Max(0, count) + ",\"added\":" + Math.Max(0, added) +
                             ",\"ocr_added\":" + Math.Max(0, ocrAdded) +
                             ",\"obj_added\":" + Math.Max(0, objAdded) +
                             ",\"ocr\":" + Math.Max(0, ocrFlag) +
                             ",\"obj\":" + Math.Max(0, objFlag) + "}");
                        // 第一批图片写完索引后再启动查询进程，避免只加载到空索引。
                        if (phase == "run" && (added > 0 || ocrAdded > 0 || objAdded > 0) && aiQueryProc == null) AiStartServer();
                        if (phase == "done")
                        {
                            // 查询进程可能是在索引中途启动的，加载的是半成品索引；
                            // 完成后必须 reload，否则新增照片/新索引项搜不到。
                            if (aiQueryProc != null)
                            {
                                aiReloadQueryPending = !string.IsNullOrEmpty(aiLastQuery);
                                AiReloadIndex();
                            }
                            else
                            {
                                aiReloadQueryPending = false;
                                AiStartServer();
                            }
                        }
                        if (phase == "done" && aiReindexPending)
                        {
                            aiReindexPending = false;
                            AiEnsureSearch(true);
                        }
                    }
                    catch { }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    try { Log("[AI索引] " + e.Data); } catch { }
                };
                p.Exited += (s, e) =>
                {
                    try
                    {
                        aiIndexRunning = false;
                        if (aiIndexPaused)
                        {
                            Send("{\"type\":\"albaiindex\",\"phase\":\"paused\",\"done\":" + aiIndexDoneCount + ",\"total\":" + aiIndexTotal + "}");
                            return;
                        }
                        if (aiIndexRestartPending)
                        {
                            aiIndexRestartPending = false;
                            AiEnsureSearch(true);
                            return;
                        }
                        if (aiIndexResumePending)
                        {
                            aiIndexResumePending = false;
                            AiEnsureSearch(true);
                            return;
                        }
                        bool restartPending = aiReindexPending && !aiIndexUnavailable && !aiShuttingDown;
                        if (!aiIndexDone && !aiIndexUnavailable && !restartPending)
                            Send("{\"type\":\"albaiindex\",\"phase\":\"error\",\"reason\":\"index_process_exit\",\"done\":" + aiIndexDoneCount + ",\"total\":" + aiIndexTotal + "}");
                        if (restartPending)
                        {
                            aiReindexPending = false;
                            AiEnsureSearch(true);
                        }
                        if (aiQueryProc == null) AiStartServer();
                    }
                    catch { }
                };
                aiIndexProc = p;
                if (!p.Start()) throw new Exception("AI 索引进程启动失败");
                try
                {
                    p.PriorityClass = perfMode == "high" ? ProcessPriorityClass.High
                        : perfMode == "low" ? ProcessPriorityClass.BelowNormal
                        : ProcessPriorityClass.Normal;
                }
                catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                aiIndexRunning = false;
                aiIndexUnavailable = true;
                Send("{\"type\":\"albaiindex\",\"phase\":\"error\",\"reason\":" + JsonStr(ex.Message) + ",\"done\":0,\"total\":0}");
            }
        }

        void AiStartServer()
        {
            if (aiQueryProc != null || aiServerStarting) return;
            if (aiIndexDb == null || !File.Exists(aiIndexDb)) return;
            if (!File.Exists(Path.Combine(AiModelDir(), "cnclip_vitb16.txt.fp32.onnx"))) return;
            aiServerStarting = true;
            try
            {
                string script = Path.Combine(Application.StartupPath, "ai", "search_server.py");
                if (!File.Exists(script)) throw new Exception("search_server.py 不存在");
                var psi = new ProcessStartInfo();
                psi.FileName = AiPythonExe();
                psi.Arguments = "-u " + AiQ(script) + " --db " + AiQ(aiIndexDb) + " --model-dir " + AiQ(AiModelDir());
                psi.WorkingDirectory = Application.StartupPath;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                try { psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; } catch { }
                try { psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1"; } catch { }
                var p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    try
                    {
                        string line = e.Data;
                        string type = AiText(line, "type");
                        if (type == "ready")
                        {
                            aiReady = AiBool(line, "ok");
                            aiServerStarting = false;
                            int count = AiInt(line, "count");
                            if (aiReady)
                            {
                                aiIndexItemCount = count;
                                Send("{\"type\":\"albaiindex\",\"phase\":\"ready\",\"done\":" + aiIndexDoneCount + ",\"total\":" + aiIndexTotal + ",\"count\":" + count +
                                     ",\"ocr\":" + AiInt(line, "ocr") + ",\"obj\":" + AiInt(line, "obj") + "}");
                                if (aiIndexDone && aiQueryProc != null) AiReloadIndex();
                                // 索引刚完成时由 reloaded 事件统一重查，避免 ready 和 reloaded 各发一次。
                                if (!string.IsNullOrEmpty(aiLastQuery) && !aiReloadQueryPending) AiSendQuery(aiLastQuery);
                                if (!string.IsNullOrEmpty(aiPendingTagPath))
                                {
                                    string pendingTag = aiPendingTagPath;
                                    aiPendingTagPath = "";
                                    AiSendTagQuery(pendingTag);
                                }
                                if (aiReindexPending)
                                {
                                    aiReindexPending = false;
                                    AiEnsureSearch(true);
                                }
                            }
                            else
                            {
                                Send("{\"type\":\"albaiindex\",\"phase\":\"unavailable\",\"reason\":" + JsonStr(AiText(line, "reason")) + ",\"done\":0,\"total\":" + aiIndexTotal + "}");
                            }
                        }
                        else if (type == "reloaded")
                        {
                            aiIndexItemCount = AiInt(line, "count");
                            Send("{\"type\":\"albaiindex\",\"phase\":\"ready\",\"done\":" + aiIndexDoneCount + ",\"total\":" + aiIndexTotal + ",\"count\":" + aiIndexItemCount +
                                 ",\"ocr\":" + AiInt(line, "ocr") + ",\"obj\":" + AiInt(line, "obj") + "}");
                            if (aiReloadQueryPending)
                            {
                                aiReloadQueryPending = false;
                                if (!string.IsNullOrEmpty(aiLastQuery)) AiSendQuery(aiLastQuery);
                            }
                        }
                        else if (type == "result")
                        {
                            Send(line.Replace("\"type\":\"result\"", "\"type\":\"albaisearch\""));
                        }
                        else if (type == "tags")
                        {
                            Send(line.Replace("\"type\":\"tags\"", "\"type\":\"albtags\""));
                        }
                        else if (type == "error")
                        {
                            Send("{\"type\":\"albaiindex\",\"phase\":\"error\",\"reason\":" + JsonStr(AiText(line, "reason")) + "}");
                        }
                    }
                    catch { }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    try { Log("[AI搜索] " + e.Data); } catch { }
                };
                p.Exited += (s, e) =>
                {
                    try
                    {
                        aiReady = false;
                        aiServerStarting = false;
                        if (aiQueryProc == null) return;
                        aiQueryProc = null;
                        aiQueryIn = null;
                        Send("{\"type\":\"albaiindex\",\"phase\":\"error\",\"reason\":\"query_process_exit\"}");
                    }
                    catch { }
                };
                aiQueryProc = p;
                if (!p.Start()) throw new Exception("AI 查询进程启动失败");
                // ProcessStartInfo 在 .NET Framework 没有 StandardInputEncoding；
                // 用 UTF-8 StreamWriter 包住 stdin，避免中文 query 在 GBK/UTF-8 间被截成乱码。
                aiQueryIn = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                aiServerStarting = false;
                aiReady = false;
                Send("{\"type\":\"albaiindex\",\"phase\":\"error\",\"reason\":" + JsonStr(ex.Message) + "}");
            }
        }

        void AiReloadIndex()
        {
            try
            {
                if (!aiReady || aiQueryIn == null) return;
                aiQueryIn.WriteLine("{\"cmd\":\"reload\"}");
                aiQueryIn.Flush();
            }
            catch { }
        }

        void AiSearchQuery(string q)
        {
            aiLastQuery = q ?? "";
            if (string.IsNullOrEmpty(aiLastQuery)) return;
            if (!aiReady)
            {
                AiEnsureSearch();
                return;
            }
            AiSendQuery(aiLastQuery);
        }

        // 详情页按路径向常驻查询进程索取这张图的全部 AI 标签。
        void AiQueryTags(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            AiEnsureViewerPriority(path);
            if (!aiReady || aiQueryIn == null)
            {
                aiPendingTagPath = path;
                Send("{\"type\":\"albtags\",\"p\":" + JsonStr(path) + ",\"tags\":[],\"status\":\"indexing\"}");
                if (aiIndexDone) AiStartServer();
                else AiEnsureSearch();
                return;
            }
            AiSendTagQuery(path);
        }

        void AiSendTagQuery(string path)
        {
            try
            {
                if (aiQueryIn == null) return;
                aiQueryIn.WriteLine("{\"cmd\":\"detail\",\"p\":" + JsonStr(path) + "}");
                aiQueryIn.Flush();
            }
            catch
            {
                aiReady = false;
                Send("{\"type\":\"albtags\",\"p\":" + JsonStr(path) + ",\"tags\":[],\"status\":\"error\"}");
            }
        }

        void AiSendQuery(string q)
        {
            try
            {
                if (!aiReady || aiQueryIn == null) return;
                aiQueryIn.WriteLine("{\"cmd\":\"search\",\"q\":" + JsonStr(q) + ",\"limit\":600}");
                aiQueryIn.Flush();
            }
            catch
            {
                aiReady = false;
                AiEnsureSearch();
            }
        }

        // 窗口关闭时尽量让 Python 把当前 SQLite 事务提交后退出；超时再结束进程。
        public void AiStopAll()
        {
            try { aiShuttingDown = true; } catch { }
            try { AiCancelIdleIndex(); } catch { }
            try
            {
                if (!string.IsNullOrEmpty(aiIndexStop))
                    File.WriteAllText(aiIndexStop, "stop");
            }
            catch { }
            try
            {
                if (aiIndexProc != null && !aiIndexProc.HasExited)
                {
                    aiIndexProc.WaitForExit(4000);
                    if (!aiIndexProc.HasExited) aiIndexProc.Kill();
                }
            }
            catch { }
            try
            {
                if (aiQueryIn != null)
                {
                    aiQueryIn.WriteLine("{\"cmd\":\"exit\"}");
                    aiQueryIn.Flush();
                }
            }
            catch { }
            try
            {
                if (aiQueryProc != null && !aiQueryProc.HasExited)
                {
                    aiQueryProc.WaitForExit(2000);
                    if (!aiQueryProc.HasExited) aiQueryProc.Kill();
                }
            }
            catch { }
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

        string ReadJsonString(string json, string key)
        {
            try
            {
                var m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (!m.Success) return "";
                string v = m.Groups[1].Value;
                try { return Regex.Unescape(v); } catch { return v; }
            }
            catch { return ""; }
        }
        static List<object> JList(object o)
        {
            var l = o as List<object>;
            if (l != null) return l;
            var res = new List<object>();
            var en = o as System.Collections.IEnumerable;
            if (en != null && !(o is string)) foreach (var x in en) res.Add(x);
            return res;
        }
        static string B64Text(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { return ""; }
        }
    }
}

