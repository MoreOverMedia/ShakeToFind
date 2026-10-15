using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShakeToFind;

static class Settings
{
    public const float MaxScale = 4.0f;
    public const int HoldMs = 700;          
    public const int ShakeWindowMs = 600;    
    public const int MinSegmentPx = 30;       
    public const int ReversalsNeeded = 4;     
    public const float GrowTauMs = 55f;       
    public const float ShrinkTauMs = 90f;     
}
static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "ShakeToFind.SingleInstance", out bool created);
        if (!created) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new TrayContext());
    }
}

sealed class ShakeDetector
{
    readonly List<(int x, int y, long t)> pts = new();

    public bool Feed(int x, int y, long nowMs)
    {
        pts.Add((x, y, nowMs));

        int cut = 0;
        while (cut < pts.Count && nowMs - pts[cut].t > Settings.ShakeWindowMs) cut++;
        if (cut > 0) pts.RemoveRange(0, cut);

        return Reversals(true) >= Settings.ReversalsNeeded
            || Reversals(false) >= Settings.ReversalsNeeded;
    }

    int Reversals(bool horizontal)
    {
        int count = 0, dir = 0, extreme = 0;
        bool first = true;

        foreach (var p in pts)
        {
            int v = horizontal ? p.x : p.y;
            if (first) { extreme = v; first = false; continue; }

            if (dir == 0)
            {
                if (Math.Abs(v - extreme) >= Settings.MinSegmentPx)
                {
                    dir = Math.Sign(v - extreme);
                    extreme = v;
                }
            }
            else if (dir > 0)
            {
                if (v > extreme) extreme = v;
                else if (extreme - v >= Settings.MinSegmentPx) { count++; dir = -1; extreme = v; }
            }
            else
            {
                if (v < extreme) extreme = v;
                else if (v - extreme >= Settings.MinSegmentPx) { count++; dir = 1; extreme = v; }
            }
        }
        return count;
    }
}

sealed class Overlay : Form
{
    IntPtr memDc, dib, oldBmp;
    Bitmap? canvas;
    Graphics? g;
    int canvasW, canvasH;

    IntPtr cachedCursor;
    Bitmap? cursorBmp;
    int hotX, hotY, hotCx, hotCy;

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1, 1);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered | transparent (click-through) | topmost | toolwindow | noactivate
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000;
            return cp;
        }
    }

    public bool Freeze;

    public bool Render(float scale)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0)
            return false;

        if (!Freeze)
        {
            if (ci.hCursor == IntPtr.Zero) return false;
            if (ci.hCursor != cachedCursor && !LoadCursor(ci.hCursor)) return false;
        }
        else if (cursorBmp == null)
        {
            return false;
        }

        EnsureCanvas();
        g!.Clear(Color.Transparent);

        float w = cursorBmp!.Width * scale;
        float h = cursorBmp.Height * scale;
        g.DrawImage(cursorBmp, new RectangleF(hotCx - hotX * scale, hotCy - hotY * scale, w, h));

        var dst = new Native.POINT { X = ci.ptScreenPos.X - hotCx, Y = ci.ptScreenPos.Y - hotCy };
        var size = new Native.SIZE { cx = canvasW, cy = canvasH };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        return true;
    }

    bool LoadCursor(IntPtr hCursor)
    {
        if (!Native.GetIconInfo(hCursor, out var info)) return false;
        try
        {
            using var icon = Icon.FromHandle(hCursor);
            var bmp = icon.ToBitmap();
            cursorBmp?.Dispose();
            cursorBmp = bmp;
            hotX = info.xHotspot;
            hotY = info.yHotspot;
            cachedCursor = hCursor;
        }
        catch { return false; }
        finally
        {
            if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
        }

        hotCx = (int)Math.Ceiling(hotX * Settings.MaxScale) + 1;
        hotCy = (int)Math.Ceiling(hotY * Settings.MaxScale) + 1;
        return true;
    }

    void EnsureCanvas()
    {
        int w = (int)Math.Ceiling(cursorBmp!.Width * Settings.MaxScale) + 3;
        int h = (int)Math.Ceiling(cursorBmp.Height * Settings.MaxScale) + 3;
        if (canvas != null && w == canvasW && h == canvasH) return;

        FreeCanvas();
        canvasW = w;
        canvasH = h;

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        memDc = Native.CreateCompatibleDC(screenDc);
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        dib = Native.CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        oldBmp = Native.SelectObject(memDc, dib);

        canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        g = Graphics.FromImage(canvas);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
    }

    void FreeCanvas()
    {
        g?.Dispose(); g = null;
        canvas?.Dispose(); canvas = null;
        if (memDc != IntPtr.Zero)
        {
            if (oldBmp != IntPtr.Zero) Native.SelectObject(memDc, oldBmp);
            Native.DeleteDC(memDc);
            memDc = IntPtr.Zero;
        }
        if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { FreeCanvas(); cursorBmp?.Dispose(); }
        base.Dispose(disposing);
    }
}

sealed class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "ShakeToFind";

    readonly NotifyIcon tray;
    readonly Overlay overlay = new();
    readonly ShakeDetector detector = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 8 };
    readonly Native.HookProc hookProc;
    IntPtr hook;

    long lastShake;
    long lastFrame;
    float scale = 1f;
    bool animating;
    bool cursorHidden;

    static readonly uint[] CursorIds =
    {
        32512, 32513, 32514, 32515, 32516, 32642, 32643,
        32644, 32645, 32646, 32648, 32649, 32650, 32651
    };


    static void HideRealCursor()
    {
        var andMask = new byte[128];
        Array.Fill(andMask, (byte)0xFF);
        var xorMask = new byte[128];
        IntPtr blank = Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
        if (blank == IntPtr.Zero) return;

        foreach (uint id in CursorIds)
            Native.SetSystemCursor(Native.CopyIcon(blank), id);

        Native.DestroyIcon(blank);
    }


    static void RestoreRealCursor() => Native.SystemParametersInfo(0x0057, 0, IntPtr.Zero, 0);

    public TrayContext()
    {
        RestoreRealCursor();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (cursorHidden) RestoreRealCursor(); };

        hookProc = HookCallback;
        hook = Native.SetWindowsHookEx(14, hookProc, Native.GetModuleHandle(null), 0);

        timer.Tick += OnTick;

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsAutoStart(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetAutoStart(startup.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Shake to Find",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)0x0200)
            {
                int x = Marshal.ReadInt32(lParam, 0);
                int y = Marshal.ReadInt32(lParam, 4);
                long now = Environment.TickCount64;
                if (detector.Feed(x, y, now))
                {
                    lastShake = now;
                    if (!animating) StartAnimation(now);
                }
            }
        }
        catch {}
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    void StartAnimation(long now)
    {
        animating = true;
        scale = 1f;
        lastFrame = now;
        overlay.Show();
        timer.Start();
    }

    void StopAnimation()
    {
        animating = false;
        timer.Stop();
        if (cursorHidden)
        {
            RestoreRealCursor();
            cursorHidden = false;
            overlay.Freeze = false;
        }
        overlay.Hide();
        scale = 1f;
    }

    void OnTick(object? sender, EventArgs e)
    {
        if (!cursorHidden)
        {
            if (!overlay.Render(scale)) { StopAnimation(); return; }
            overlay.Freeze = true;
            HideRealCursor();
            cursorHidden = true;
        }

        long now = Environment.TickCount64;
        float dt = Math.Max(1, now - lastFrame);
        lastFrame = now;

        bool active = now - lastShake < Settings.HoldMs;
        float target = active ? Settings.MaxScale : 1f;
        float tau = active ? Settings.GrowTauMs : Settings.ShrinkTauMs;
        scale += (target - scale) * (1f - MathF.Exp(-dt / tau));

        if (!active && scale < 1.03f) { StopAnimation(); return; }
        if (!overlay.Render(scale)) StopAnimation();
    }

    static bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) != null;
    }

    static void SetAutoStart(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (on) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, false);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var gr = Graphics.FromImage(bmp))
        {
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            var pts = new[]
            {
                new Point(7, 3), new Point(7, 27), new Point(13, 21), new Point(17, 30),
                new Point(21, 28), new Point(17, 19), new Point(25, 19)
            };
            gr.FillPolygon(Brushes.White, pts);
            using var pen = new Pen(Color.Black, 2);
            gr.DrawPolygon(pen, pts);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        timer.Stop();
        if (cursorHidden) { RestoreRealCursor(); cursorHidden = false; }
        tray.Visible = false;
        tray.Dispose();
        overlay.Dispose();
        base.ExitThreadCore();
    }
}

static class Native
{
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")] public static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot,
        int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);
    [DllImport("user32.dll")] public static extern IntPtr CopyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}