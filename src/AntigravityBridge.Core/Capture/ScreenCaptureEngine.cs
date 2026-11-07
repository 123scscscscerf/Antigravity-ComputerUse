using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using AntigravityBridge.Core.Models;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core.Capture;

/// <summary>
/// Turbo-charged Screen & Silent Window Capture Engine.
/// Uses pre-allocated GDI+ resources, LockBits perceptual diffing, non-blocking async disk caching,
/// and hardware-accelerated JPEG/PNG encoding in C#.
/// </summary>
public sealed class ScreenCaptureEngine
{
    private static readonly ImageCodecInfo? s_jpegCodec =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // Pre-allocated GDI+ drawing objects to avoid per-frame font/handle allocation overhead
    private static readonly Font s_overlayFont = new("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly SolidBrush s_whiteBrush = new(Color.White);
    private static readonly Pen s_gridPen = new(Color.FromArgb(95, 0, 200, 255), 1f);
    private static readonly SolidBrush s_gridBgBrush = new(Color.FromArgb(195, 0, 80, 130));
    private static readonly Pen s_somPen = new(Color.FromArgb(220, 40, 240, 90), 2f);
    private static readonly SolidBrush s_somBgBrush = new(Color.FromArgb(230, 12, 125, 45));
    private static readonly Pen s_cursorPen = new(Color.FromArgb(235, 255, 55, 55), 2f);
    private static readonly object s_gdiLock = new();

    private byte[]? _lastSignature;
    private Bitmap? _reusableScreenBmp;
    private string? _lastCacheKey;
    private string? _lastEncodedBase64;
    private string? _lastEncodedMime;

    public List<MonitorInfo> GetMonitors()
    {
        Win32.EnsureDefaultDesktop();
        var screens = Screen.AllScreens;
        var list = new List<MonitorInfo>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new MonitorInfo
            {
                Index = i,
                DeviceName = s.DeviceName,
                Primary = s.Primary,
                X = s.Bounds.X,
                Y = s.Bounds.Y,
                Width = s.Bounds.Width,
                Height = s.Bounds.Height
            });
        }
        return list;
    }

    public CaptureResult CaptureScreen(
        int monitorIndex = 0,
        int? regionX = null,
        int? regionY = null,
        int? regionW = null,
        int? regionH = null,
        bool drawGrid = true,
        int gridStep = 200,
        string format = "jpeg",
        int quality = 80,
        string? savePath = null,
        List<UiElementInfo>? uiElements = null)
    {
        Win32.EnsureDefaultDesktop();
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            throw new InvalidOperationException("No displays detected.");

        var screen = (monitorIndex >= 0 && monitorIndex < screens.Length)
            ? screens[monitorIndex]
            : Screen.PrimaryScreen ?? screens[0];

        int x = regionX ?? screen.Bounds.X;
        int y = regionY ?? screen.Bounds.Y;
        int width = regionW ?? screen.Bounds.Width;
        int height = regionH ?? screen.Bounds.Height;

        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Invalid capture dimensions: {width}x{height}");

        lock (s_gdiLock)
        {
            if (_reusableScreenBmp == null || _reusableScreenBmp.Width != width || _reusableScreenBmp.Height != height)
            {
                _reusableScreenBmp?.Dispose();
                _reusableScreenBmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            }

            var bmp = _reusableScreenBmp;
            string captureMethod = "BitBlt(DesktopDC)";

            IntPtr desktopDc = Win32.GetDC(IntPtr.Zero);
            bool captured = false;
            if (desktopDc != IntPtr.Zero)
            {
                try
                {
                    using var g = Graphics.FromImage(bmp);
                    IntPtr destDc = g.GetHdc();
                    try
                    {
                        captured = Win32.BitBlt(destDc, 0, 0, width, height, desktopDc, x, y, Win32.SRCCOPY);
                    }
                    finally
                    {
                        g.ReleaseHdc(destDc);
                    }
                }
                finally
                {
                    Win32.ReleaseDC(IntPtr.Zero, desktopDc);
                }
            }

            if (!captured)
            {
                captureMethod = "CompositePrintWindow";
                CompositeDesktopWindows(bmp, x, y, width, height);
            }

            Win32.GetCursorPos(out var cursor);

            (string frameHash, double diffPercent) = ComputeFastSignatureAndDiff(bmp);

            string cacheKey = $"{frameHash}:{cursor.X}:{cursor.Y}:{drawGrid}:{gridStep}:{format}:{quality}:{(uiElements?.Count ?? 0)}";
            string base64;
            string mimeType;

            if (diffPercent == 0.0 && cacheKey == _lastCacheKey && _lastEncodedBase64 != null && _lastEncodedMime != null)
            {
                base64 = _lastEncodedBase64;
                mimeType = _lastEncodedMime;
            }
            else
            {
                RenderNativeOverlays(
                    bmp,
                    offsetX: x,
                    offsetY: y,
                    cursorX: cursor.X,
                    cursorY: cursor.Y,
                    drawGrid: drawGrid,
                    gridStep: gridStep,
                    useRelativeCoords: false,
                    uiElements: uiElements);

                (base64, mimeType) = EncodeBitmapFast(bmp, format, quality, savePath);
                _lastCacheKey = cacheKey;
                _lastEncodedBase64 = base64;
                _lastEncodedMime = mimeType;
            }

            return new CaptureResult
            {
                ImageBase64 = base64,
                MimeType = mimeType,
                Width = width,
                Height = height,
                OffsetX = x,
                OffsetY = y,
                CaptureMethod = captureMethod,
                CursorX = cursor.X,
                CursorY = cursor.Y,
                FrameHash = frameHash,
                DiffPercent = Math.Round(diffPercent, 2),
                ScreenChanged = diffPercent >= 0.25,
                SavedPath = savePath
            };
        }
    }

    public CaptureResult CaptureWindowSilent(
        long hwndValue,
        bool clientOnly = false,
        bool drawGrid = true,
        int gridStep = 150,
        string format = "jpeg",
        int quality = 80,
        string? savePath = null,
        List<UiElementInfo>? uiElements = null)
    {
        Win32.EnsureDefaultDesktop();
        IntPtr hwnd = new(hwndValue);
        if (!Win32.IsWindow(hwnd))
            throw new ArgumentException($"Invalid window handle: 0x{hwndValue:X}");

        if (Win32.IsIconic(hwnd))
        {
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
            Thread.Sleep(30);
        }

        if (!Win32.GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException($"Failed to get window rect for HWND 0x{hwndValue:X}");

        int width = rect.Width;
        int height = rect.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Window 0x{hwndValue:X} has zero area ({width}x{height}).");

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        string methodUsed = "PrintWindow(PW_RENDERFULLCONTENT)";

        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                uint flags = Win32.PW_RENDERFULLCONTENT | (clientOnly ? Win32.PW_CLIENTONLY : 0);
                bool success = Win32.PrintWindow(hwnd, hdc, flags);
                if (!success)
                {
                    success = Win32.PrintWindow(hwnd, hdc, clientOnly ? Win32.PW_CLIENTONLY : 0);
                    methodUsed = "PrintWindow(Standard)";
                }

                if (!success)
                {
                    IntPtr srcDc = Win32.GetWindowDC(hwnd);
                    if (srcDc != IntPtr.Zero)
                    {
                        try
                        {
                            Win32.BitBlt(hdc, 0, 0, width, height, srcDc, 0, 0, Win32.SRCCOPY);
                            methodUsed = "BitBlt(WindowDC)";
                        }
                        finally
                        {
                            Win32.ReleaseDC(hwnd, srcDc);
                        }
                    }
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        Win32.GetCursorPos(out var cursor);
        (string frameHash, double diffPercent) = ComputeFastSignatureAndDiff(bmp);

        RenderNativeOverlays(
            bmp,
            offsetX: rect.Left,
            offsetY: rect.Top,
            cursorX: cursor.X,
            cursorY: cursor.Y,
            drawGrid: drawGrid,
            gridStep: gridStep,
            useRelativeCoords: true,
            uiElements: uiElements);

        (string base64, string mimeType) = EncodeBitmapFast(bmp, format, quality, savePath);

        return new CaptureResult
        {
            ImageBase64 = base64,
            MimeType = mimeType,
            Width = width,
            Height = height,
            OffsetX = rect.Left,
            OffsetY = rect.Top,
            CaptureMethod = methodUsed,
            CursorX = cursor.X,
            CursorY = cursor.Y,
            FrameHash = frameHash,
            DiffPercent = Math.Round(diffPercent, 2),
            ScreenChanged = diffPercent >= 0.25,
            SavedPath = savePath
        };
    }

    private static void RenderNativeOverlays(
        Bitmap bmp,
        int offsetX,
        int offsetY,
        int cursorX,
        int cursorY,
        bool drawGrid,
        int gridStep,
        bool useRelativeCoords,
        List<UiElementInfo>? uiElements)
    {
        int width = bmp.Width;
        int height = bmp.Height;
        if (width < 16 || height < 16)
            return;

        lock (s_gdiLock)
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.None;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;

            if (drawGrid && gridStep >= 50)
            {
                for (int x = gridStep; x < width; x += gridStep)
                {
                    g.DrawLine(s_gridPen, x, 0, x, height);
                    int labelX = useRelativeCoords ? x : (offsetX + x);
                    DrawBadgeFast(g, $"X:{labelX}", x + 3, 3, s_gridBgBrush);
                }

                for (int y = gridStep; y < height; y += gridStep)
                {
                    g.DrawLine(s_gridPen, 0, y, width, y);
                    int labelY = useRelativeCoords ? y : (offsetY + y);
                    DrawBadgeFast(g, $"Y:{labelY}", 3, y + 3, s_gridBgBrush);
                }
            }

            if (uiElements is { Count: > 0 })
            {
                foreach (var el in uiElements)
                {
                    if (el.Width <= 0 || el.Height <= 0) continue;
                    int rx = el.RelX;
                    int ry = el.RelY;
                    g.DrawRectangle(s_somPen, rx, ry, el.Width, el.Height);
                    DrawBadgeFast(g, $"[{el.Id}]", Math.Max(0, rx), Math.Max(0, ry - 15), s_somBgBrush);
                }
            }

            int localCx = cursorX - offsetX;
            int localCy = cursorY - offsetY;
            if (localCx >= 0 && localCx < width && localCy >= 0 && localCy < height)
            {
                g.DrawEllipse(s_cursorPen, localCx - 9, localCy - 9, 18, 18);
                g.DrawLine(s_cursorPen, localCx - 14, localCy, localCx + 14, localCy);
                g.DrawLine(s_cursorPen, localCx, localCy - 14, localCx, localCy + 14);
            }

            // Claude-style Cyan <-> Royal Blue Shimmering Border & Top HUD Pill on full-screen captures
            if (!useRelativeCoords && width >= 640 && height >= 480)
            {
                DrawCyanBlueAuraBorder(g, width, height);
            }
        }
    }

    private static void DrawCyanBlueAuraBorder(Graphics g, int width, int height)
    {
        double phase = (Environment.TickCount64 % 4000) / 4000.0 * Math.PI * 2.0;
        double t = (Math.Sin(phase) + 1.0) * 0.5;

        Color cyan = Color.FromArgb(225, 0, 238, 255);
        Color royalBlue = Color.FromArgb(225, 37, 99, 235);
        Color c1 = Color.FromArgb(
            225,
            (int)(cyan.R + (royalBlue.R - cyan.R) * t),
            (int)(cyan.G + (royalBlue.G - cyan.G) * t),
            (int)(cyan.B + (royalBlue.B - cyan.B) * t));
        Color c2 = Color.FromArgb(
            225,
            (int)(royalBlue.R + (cyan.R - royalBlue.R) * t),
            (int)(royalBlue.G + (cyan.G - royalBlue.G) * t),
            (int)(royalBlue.B + (cyan.B - royalBlue.B) * t));

        var rect = new Rectangle(0, 0, width, height);
        using var grad = new LinearGradientBrush(rect, c1, c2, 35f);
        using var outerPen = new Pen(grad, 8f);
        using var corePen = new Pen(Color.FromArgb(240, 120, 245, 255), 2f);

        g.DrawRectangle(outerPen, 4, 4, width - 8, height - 8);
        g.DrawRectangle(corePen, 1, 1, width - 2, height - 2);

        // Top-Center Pill Badge
        const string pillText = "ANTIGRAVITY COMPUTER-USE  •  CYAN-BLUE SHIELD ACTIVE";
        int pillW = 390;
        int pillH = 26;
        int pillX = (width - pillW) / 2;
        int pillY = 8;
        var pillRect = new Rectangle(pillX, pillY, pillW, pillH);

        using var pillBg = new SolidBrush(Color.FromArgb(225, 12, 20, 40));
        using var pillBorder = new Pen(grad, 2f);
        g.FillRectangle(pillBg, pillRect);
        g.DrawRectangle(pillBorder, pillRect);

        using var orbBrush = new SolidBrush(Color.FromArgb(255, 0, 240, 255));
        g.FillEllipse(orbBrush, pillX + 10, pillY + 8, 10, 10);
        g.DrawString(pillText, s_overlayFont, s_whiteBrush, pillX + 26, pillY + 5);
    }

    private static void DrawBadgeFast(Graphics g, string text, int x, int y, Brush bgBrush)
    {
        int approxWidth = text.Length * 6 + 6;
        g.FillRectangle(bgBrush, x - 1, y - 1, approxWidth, 14);
        g.DrawString(text, s_overlayFont, s_whiteBrush, x, y);
    }

    private unsafe (string HashHex, double DiffPercent) ComputeFastSignatureAndDiff(Bitmap bmp)
    {
        const int gridW = 32;
        const int gridH = 32;
        byte[] currentSig = new byte[gridW * gridH];

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int w = bmp.Width;
            int h = bmp.Height;

            for (int gy = 0; gy < gridH; gy++)
            {
                int py = (gy * h) / gridH;
                byte* row = scan0 + py * stride;
                for (int gx = 0; gx < gridW; gx++)
                {
                    int px = (gx * w) / gridW;
                    byte* p = row + px * 4;
                    currentSig[gy * gridW + gx] = (byte)((p[2] * 2 + p[1] * 5 + p[0]) >> 3);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < currentSig.Length; i++)
        {
            hash ^= currentSig[i];
            hash *= 1099511628211UL;
        }

        double diffPercent = 100.0;
        var prev = _lastSignature;
        if (prev != null && prev.Length == currentSig.Length)
        {
            int changedCells = 0;
            for (int i = 0; i < currentSig.Length; i++)
            {
                if (Math.Abs(currentSig[i] - prev[i]) > 6)
                    changedCells++;
            }
            diffPercent = (changedCells * 100.0) / currentSig.Length;
        }

        _lastSignature = currentSig;
        return ($"{hash:X16}", diffPercent);
    }

    private static (string Base64, string MimeType) EncodeBitmapFast(
        Bitmap bmp,
        string format,
        int quality,
        string? savePath)
    {
        using var ms = new MemoryStream(131072);
        string mimeType;

        if (format.Equals("png", StringComparison.OrdinalIgnoreCase) || s_jpegCodec == null)
        {
            bmp.Save(ms, ImageFormat.Png);
            mimeType = "image/png";
        }
        else
        {
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 30, 100));
            bmp.Save(ms, s_jpegCodec, encParams);
            mimeType = "image/jpeg";
        }

        byte[] bytes = ms.ToArray();
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            string targetPath = savePath;
            // Non-blocking background disk persist so IPC response returns immediately from RAM
            _ = Task.Run(() =>
            {
                try
                {
                    string? dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllBytes(targetPath, bytes);
                }
                catch
                {
                }
            });
        }

        return (Convert.ToBase64String(bytes), mimeType);
    }

    private static void CompositeDesktopWindows(Bitmap canvas, int viewX, int viewY, int viewW, int viewH)
    {
        var windowsTopToBottom = new List<(IntPtr Hwnd, Win32.RECT Rect)>();
        var sbClass = new System.Text.StringBuilder(128);

        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd) || Win32.IsWindowCloaked(hWnd))
                return true;

            int exStyle = Win32.GetWindowLong(hWnd, Win32.GWL_EXSTYLE);
            if ((exStyle & Win32.WS_EX_TRANSPARENT) != 0)
                return true;

            sbClass.Clear();
            Win32.GetClassName(hWnd, sbClass, sbClass.Capacity);
            string cls = sbClass.ToString();
            if (cls is "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow" or "CEF-OSC-WIDGET")
                return true;

            if (Win32.GetWindowRect(hWnd, out var r) && r.Width > 0 && r.Height > 0)
            {
                if (r.Right > viewX && r.Left < viewX + viewW && r.Bottom > viewY && r.Top < viewY + viewH)
                {
                    windowsTopToBottom.Add((hWnd, r));

                    // Z-Order Occlusion Culling: if a real opaque window covers the entire capture region
                    // (excluding the desktop wallpaper container Progman/WorkerW), stop enumerating behind it.
                    if (cls is not ("Progman" or "WorkerW") &&
                        r.Left <= viewX && r.Top <= viewY && r.Right >= viewX + viewW && r.Bottom >= viewY + viewH)
                    {
                        return false;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        using var gCanvas = Graphics.FromImage(canvas);

        // Fast-path: single full-viewport window renders directly into canvas HDC (zero temp Bitmap allocation)
        if (windowsTopToBottom.Count == 1)
        {
            var (onlyHwnd, onlyRect) = windowsTopToBottom[0];
            if (onlyRect.Left == viewX && onlyRect.Top == viewY && onlyRect.Width == viewW && onlyRect.Height == viewH)
            {
                IntPtr hdc = gCanvas.GetHdc();
                try
                {
                    Win32.PrintWindow(onlyHwnd, hdc, Win32.PW_RENDERFULLCONTENT);
                }
                finally
                {
                    gCanvas.ReleaseHdc(hdc);
                }
                return;
            }
        }

        gCanvas.Clear(Color.FromArgb(24, 26, 32));

        for (int i = windowsTopToBottom.Count - 1; i >= 0; i--)
        {
            var (hWnd, rect) = windowsTopToBottom[i];
            int w = Math.Min(rect.Width, 4096);
            int h = Math.Min(rect.Height, 4096);
            if (w <= 0 || h <= 0) continue;

            try
            {
                using var winBmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                using (var gWin = Graphics.FromImage(winBmp))
                {
                    IntPtr hdc = gWin.GetHdc();
                    try
                    {
                        Win32.PrintWindow(hWnd, hdc, Win32.PW_RENDERFULLCONTENT);
                    }
                    finally
                    {
                        gWin.ReleaseHdc(hdc);
                    }
                }
                gCanvas.DrawImageUnscaled(winBmp, rect.Left - viewX, rect.Top - viewY);
            }
            catch
            {
            }
        }
    }
}
