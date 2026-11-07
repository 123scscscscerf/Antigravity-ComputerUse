using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using AntigravityBridge.Core.Models;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core.Capture;

/// <summary>
/// Handles both active screen capture (multi-monitor, DPI-aware) and Silent-Mode background window capture.
/// Automatically falls back to DWM PrintWindow desktop compositing when running in sandboxed/headless sessions.
/// </summary>
public sealed class ScreenCaptureEngine
{
    public List<MonitorInfo> GetMonitors()
    {
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

    public CaptureResult CaptureScreen(int monitorIndex = 0, int? regionX = null, int? regionY = null, int? regionW = null, int? regionH = null)
    {
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

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        string captureMethod = "CopyFromScreen";

        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        catch (Win32Exception)
        {
            // Fallback: Composite visible windows in Z-order using PrintWindow(PW_RENDERFULLCONTENT)
            captureMethod = "CompositePrintWindow";
            CompositeDesktopWindows(bmp, x, y, width, height);
        }

        Win32.GetCursorPos(out var cursor);

        return new CaptureResult
        {
            ImageBase64 = BitmapToPngBase64(bmp),
            Width = width,
            Height = height,
            OffsetX = x,
            OffsetY = y,
            CaptureMethod = captureMethod,
            CursorX = cursor.X,
            CursorY = cursor.Y
        };
    }

    private static void CompositeDesktopWindows(Bitmap canvas, int viewX, int viewY, int viewW, int viewH)
    {
        var windowsTopToBottom = new List<(IntPtr Hwnd, Win32.RECT Rect)>();

        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd))
                return true;

            if (Win32.GetWindowRect(hWnd, out var r) && r.Width > 0 && r.Height > 0)
            {
                if (r.Right > viewX && r.Left < viewX + viewW && r.Bottom > viewY && r.Top < viewY + viewH)
                {
                    windowsTopToBottom.Add((hWnd, r));
                }
            }
            return true;
        }, IntPtr.Zero);

        using var gCanvas = Graphics.FromImage(canvas);
        gCanvas.Clear(Color.FromArgb(24, 26, 32));

        // Draw from bottom-most to top-most so Z-order is preserved
        for (int i = windowsTopToBottom.Count - 1; i >= 0; i--)
        {
            var (hWnd, rect) = windowsTopToBottom[i];
            int w = Math.Min(rect.Width, 4096);
            int h = Math.Min(rect.Height, 4096);
            if (w <= 0 || h <= 0) continue;

            try
            {
                using var winBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
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
                // Skip individual windows that cannot be rendered
            }
        }
    }

    /// <summary>
    /// Captures a specific window (HWND) in Silent-Mode without bringing it to the foreground.
    /// Uses PrintWindow with PW_RENDERFULLCONTENT (for DWM/hardware-accelerated windows) with BitBlt fallback.
    /// </summary>
    public CaptureResult CaptureWindowSilent(long hwndValue, bool clientOnly = false)
    {
        IntPtr hwnd = new(hwndValue);
        if (!Win32.IsWindow(hwnd))
            throw new ArgumentException($"Invalid window handle: 0x{hwndValue:X}");

        if (Win32.IsIconic(hwnd))
        {
            // Restore without activating if minimized so it has valid surface geometry
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
            Thread.Sleep(60);
        }

        if (!Win32.GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException($"Failed to get window rect for HWND 0x{hwndValue:X}");

        int width = rect.Width;
        int height = rect.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Window 0x{hwndValue:X} has zero area ({width}x{height}).");

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
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

        return new CaptureResult
        {
            ImageBase64 = BitmapToPngBase64(bmp),
            Width = width,
            Height = height,
            OffsetX = rect.Left,
            OffsetY = rect.Top,
            CaptureMethod = methodUsed,
            CursorX = cursor.X,
            CursorY = cursor.Y
        };
    }

    private static string BitmapToPngBase64(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}
