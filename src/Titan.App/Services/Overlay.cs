using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Titan.App.Services;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// The in-game FPS counter. A layered, click-through, always-on-top window drawn with the TITAN look
/// (gold number over a near-black pill). It sits over borderless / windowed-fullscreen games — the vast
/// majority today; a game in true exclusive fullscreen owns the screen and no external overlay shows there.
/// Runs on the UI thread but survives the main window being minimized (its own DispatcherQueue timer keeps ticking).
/// </summary>
public sealed class Overlay : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<(double Fps, double Low1)> _sample;
    private nint _hwnd;
    private bool _disposed;

    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;

    public Overlay(DispatcherQueue ui, Func<(double, double)> sample)
    {
        _sample = sample;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Redraw();
    }

    public bool Visible => _hwnd != 0;

    public void Show()
    {
        if (_disposed || _hwnd != 0) return;
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "Static", null, unchecked((int)WS_POPUP), 0, 0, 0, 0, 0, 0, 0, 0);
        if (_hwnd == 0) return;
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        Redraw();
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
    }

    private void Redraw()
    {
        if (_hwnd == 0) return;
        var (fps, low1) = _sample();
        using var bmp = Compose(fps, low1, out int w, out int h);
        Push(bmp, w, h);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);  // stay above the game
    }

    // ---- drawing -------------------------------------------------------

    private static readonly Color Gold = Color.FromArgb(242, 194, 0);
    private static readonly Color Ink = Color.FromArgb(170, 11, 11, 13);
    private static readonly Color Faint = Color.FromArgb(150, 150, 150, 150);

    private static Bitmap Compose(double fps, double low1, out int w, out int h)
    {
        string big = fps > 0 ? $"{fps:0}" : "—";
        string unit = "FPS";
        string low = low1 > 0 ? $"1% {low1:0}" : "1% —";

        using var numFont = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
        using var unitFont = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var lowFont = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);

        SizeF numSz, unitSz, lowSz;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            pg.TextRenderingHint = TextRenderingHint.AntiAlias;
            numSz = pg.MeasureString(big, numFont);
            unitSz = pg.MeasureString(unit, unitFont);
            lowSz = pg.MeasureString(low, lowFont);
        }

        const int padX = 16, padY = 11, gap = 8;
        int topW = (int)Math.Ceiling(numSz.Width) + 6 + (int)Math.Ceiling(unitSz.Width);
        int contentW = Math.Max(topW, (int)Math.Ceiling(lowSz.Width));
        w = contentW + padX * 2;
        h = (int)Math.Ceiling(numSz.Height) + gap + (int)Math.Ceiling(lowSz.Height) + padY * 2;

        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        using (var path = RoundedRect(new Rectangle(0, 0, w - 1, h - 1), 12))
        {
            using var fill = new SolidBrush(Ink);
            using var pen = new Pen(Color.FromArgb(70, 242, 194, 0), 1);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        using (var goldBrush = new SolidBrush(Gold))
        using (var faintBrush = new SolidBrush(Faint))
        {
            g.DrawString(big, numFont, goldBrush, padX, padY);
            g.DrawString(unit, unitFont, faintBrush, padX + numSz.Width + 4, padY + numSz.Height - unitSz.Height - 5);
            g.DrawString(low, lowFont, faintBrush, padX, padY + numSz.Height + gap - 3);
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Blits the bitmap onto the layered window with per-pixel alpha via a DIB section.</summary>
    private unsafe void Push(Bitmap bmp, int w, int h)
    {
        nint screen = GetDC(0);
        nint mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h,                 // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,             // BI_RGB
        };
        nint bits;
        nint dib = CreateDIBSection(mem, ref bi, 0, out bits, 0, 0);
        nint old = SelectObject(mem, dib);
        try
        {
            // Draw the composed bitmap straight into the DIB memory, then premultiply for ULW_ALPHA.
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte* src = (byte*)data.Scan0;
            byte* dst = (byte*)bits;
            int count = w * h;
            for (int i = 0; i < count; i++)
            {
                byte b = src[0], gr = src[1], r = src[2], a = src[3];
                dst[0] = (byte)(b * a / 255);
                dst[1] = (byte)(gr * a / 255);
                dst[2] = (byte)(r * a / 255);
                dst[3] = a;
                src += 4; dst += 4;
            }
            bmp.UnlockBits(data);

            var (x, y) = Position(w, h);
            var ptDst = new POINT { X = x, Y = y };
            var size = new SIZE { Cx = w, Cy = h };
            var ptSrc = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screen, ref ptDst, ref size, mem, ref ptSrc, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    private (int X, int Y) Position(int w, int h)
    {
        const int m = 26;
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        return Corner switch
        {
            OverlayCorner.TopRight => (sw - w - m, m),
            OverlayCorner.BottomLeft => (m, sh - h - m),
            OverlayCorner.BottomRight => (sw - w - m, sh - h - m),
            _ => (m, m),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
    }

    // ---- interop -------------------------------------------------------

    private const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
                       WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_POPUP = 0x80000000;
    private const int SW_SHOWNOACTIVATE = 4, SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const uint ULW_ALPHA = 2;
    private const byte AC_SRC_ALPHA = 1;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string? windowName, int style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(nint hwnd, nint screenDc, ref POINT dst, ref SIZE size,
        nint srcDc, ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
}
