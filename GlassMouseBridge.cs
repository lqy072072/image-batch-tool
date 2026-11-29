using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// 液态玻璃窗口的 WebView2 必须保持 WS_EX_TRANSPARENT 才能显示画面，但这样鼠标会穿透。
// 这里在窗口上方铺一层 1% 不透明度的无边框窗口，截住鼠标后把消息原样转给
// Chrome_RenderWidgetHostHWND，既保留透明画面，又恢复完整点击/拖拽/滚轮。
class GlassMouseBridge : Form
{
    const int WM_MOUSEMOVE = 0x0200;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_LBUTTONDBLCLK = 0x0203;
    const int WM_RBUTTONDOWN = 0x0204;
    const int WM_RBUTTONUP = 0x0205;
    const int WM_MOUSEWHEEL = 0x020A;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;

    readonly Form owner;
    IntPtr target;
    bool dragging;
    int lastRegionW, lastRegionH;

    public GlassMouseBridge(Form owner)
    {
        this.owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.012;
        TopMost = false;
        AllowDrop = false;
        if (owner != null && !owner.IsDisposed) Owner = owner;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRoundedRegion();
        try { if (owner != null && !owner.IsDisposed) SetForegroundWindow(owner.Handle); } catch { }
    }

    // 主窗口用 GDI Region 裁出 R 角；桥接层也必须同步裁掉四角，
    // 否则 1.2% 的黑色填充会在圆角之外露出一个方形暗角。
    void ApplyRoundedRegion()
    {
        try
        {
            if (owner == null || owner.IsDisposed || owner.WindowState == FormWindowState.Maximized ||
                Width <= 32 || Height <= 32)
            {
                if (this.Region != null) { this.Region = null; }
                lastRegionW = 0;
                lastRegionH = 0;
                return;
            }
            if (lastRegionW == Width && lastRegionH == Height && this.Region != null) return;
            int radius = 14;
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
            lastRegionW = Width;
            lastRegionH = Height;
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public void Sync()
    {
        try
        {
            if (owner == null || owner.IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) { if (Visible) Hide(); return; }
            Bounds = owner.Bounds;
            ApplyRoundedRegion();
            RefreshTarget();
            if (Visible)
            {
                // 只跟随位置，不抢前台激活。
                SetWindowPos(Handle, IntPtr.Zero, owner.Left, owner.Top, owner.Width, owner.Height, 0x0010 | 0x0004);
            }
        }
        catch { }
    }

    void RefreshTarget()
    {
        target = IntPtr.Zero;
        try
        {
            EnumChildWindows(owner.Handle, (h, l) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() == "Chrome_RenderWidgetHostHWND")
                {
                    RECT r;
                    if (GetWindowRect(h, out r) && r.R > r.L && r.B > r.T) { target = h; return false; }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        try
        {
            if (owner != null && !owner.IsDisposed)
            {
                SetForegroundWindow(owner.Handle);
                owner.Activate();
            }
            // 顶部空白条走原生拖动；左右两侧的交通灯和“皮肤/关于”仍交给网页。
            if (e.Button == MouseButtons.Left && e.Y < 40 && e.X > 60 && e.X < Width - 180)
            {
                ReleaseCapture();
                Point sp = PointToScreen(e.Location);
                int lp = ((sp.Y & 0xFFFF) << 16) | (sp.X & 0xFFFF);
                SendMessage(owner.Handle, 0x00A1, (IntPtr)2, (IntPtr)lp); // WM_NCLBUTTONDOWN / HTCAPTION
                return;
            }
            RefreshTarget();
            if (target == IntPtr.Zero) return;
            Point cp = ChildPoint(e.Location);
            Capture = true;
            dragging = true;
            Send(WM_MOUSEMOVE, 0, cp);
            SendButtonDown(e.Button, cp);
        }
        catch { }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        try
        {
            if (!dragging || target == IntPtr.Zero) return;
            Point cp = ChildPoint(e.Location);
            Send(WM_MOUSEMOVE, MouseFlags(), cp);
        }
        catch { }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        try
        {
            if (target != IntPtr.Zero)
            {
                Point cp = ChildPoint(e.Location);
                SendButtonUp(e.Button, cp);
            }
            dragging = false;
            Capture = false;
        }
        catch { }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        try
        {
            if (target == IntPtr.Zero) return;
            Point cp = ChildPoint(e.Location);
            Send(WM_LBUTTONDBLCLK, MouseFlags(), cp);
        }
        catch { }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        try
        {
            RefreshTarget();
            if (target == IntPtr.Zero) return;
            Point sp = PointToScreen(e.Location);
            Point cp = sp;
            ScreenToClient(target, ref cp);
            int delta = e.Delta;
            Send(WM_MOUSEWHEEL, (delta << 16), cp);
        }
        catch { }
    }

    void SendButtonDown(MouseButtons b, Point cp)
    {
        int msg = b == MouseButtons.Right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN;
        int wp = b == MouseButtons.Right ? 2 : 1;
        Send(msg, wp | (MouseFlags() & 0xFFFF), cp);
    }

    void SendButtonUp(MouseButtons b, Point cp)
    {
        int msg = b == MouseButtons.Right ? WM_RBUTTONUP : WM_LBUTTONUP;
        int wp = 0;
        Send(msg, wp, cp);
    }

    int MouseFlags()
    {
        int w = 0;
        if (Control.ModifierKeys == Keys.Shift) w |= 4;
        if (Control.ModifierKeys == Keys.Control) w |= 8;
        if (dragging) w |= 1;
        return w;
    }

    Point ChildPoint(Point client)
    {
        Point sp = PointToScreen(client);
        ScreenToClient(target, ref sp);
        return sp;
    }

    void Send(int msg, int wparam, Point pt)
    {
        if (target == IntPtr.Zero) return;
        int lp = ((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF);
        SendMessage(target, msg, (IntPtr)wparam, (IntPtr)lp);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref Point p);
}
