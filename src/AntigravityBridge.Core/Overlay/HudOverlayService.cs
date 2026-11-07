using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core.Overlay;

/// <summary>
/// Claude Computer-Use style Shimmering Cyan <-> Deep Blue Screen Border & Action HUD Overlay.
/// Runs on a dedicated WinSta0\default UI thread as a 100% click-through, non-activating TopMost layered window.
/// </summary>
public sealed class HudOverlayService
{
    private Thread? _uiThread;
    private OverlayForm? _form;
    private readonly ManualResetEventSlim _ready = new(false);

    public bool IsEnabled { get; private set; } = true;

    public void Start()
    {
        if (_uiThread != null)
            return;

        _uiThread = new Thread(UiThreadMain)
        {
            Name = "AntigravityHudOverlayThread",
            IsBackground = true
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void UiThreadMain()
    {
        Win32.EnsureDefaultDesktop();
        try
        {
            Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _form = new OverlayForm();
        _ready.Set();
        Application.Run(_form);
    }

    public void NotifyAction(string statusText, int? pulseX = null, int? pulseY = null)
    {
        Start();
        if (_form is { IsDisposed: false, IsHandleCreated: true })
        {
            try
            {
                _form.BeginInvoke(() => _form.UpdateHudState(statusText, pulseX, pulseY));
            }
            catch { }
        }
    }

    public void SetOverlayVisible(bool visible, string? statusText = null)
    {
        Start();
        IsEnabled = visible;
        if (_form is { IsDisposed: false, IsHandleCreated: true })
        {
            try
            {
                _form.BeginInvoke(() =>
                {
                    _form.SetHudVisible(visible);
                    if (!string.IsNullOrWhiteSpace(statusText))
                        _form.UpdateHudState(statusText, null, null);
                });
            }
            catch { }
        }
    }

    private sealed class OverlayForm : Form
    {
        private readonly System.Windows.Forms.Timer _animTimer;
        private double _phase;
        private string _statusText = "Antigravity Computer-Use Active";
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private (int X, int Y, float Progress)? _clickPulse;
        private bool _hudVisible = true;

        private static readonly Color s_chromaKey = Color.FromArgb(1, 2, 3);
        private static readonly Font s_pillFont = new("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);

        public OverlayForm()
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = s_chromaKey;
            TransparencyKey = s_chromaKey;
            DoubleBuffered = true;

            _animTimer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 FPS smooth shimmer
            _animTimer.Tick += OnAnimationTick;
            _animTimer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT (100% click-through) | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000;
                return cp;
            }
        }

        public void SetHudVisible(bool visible)
        {
            _hudVisible = visible;
            Visible = visible;
            Invalidate();
        }

        public void UpdateHudState(string statusText, int? pulseX, int? pulseY)
        {
            _statusText = statusText;
            _lastActivityUtc = DateTime.UtcNow;
            if (pulseX.HasValue && pulseY.HasValue)
            {
                _clickPulse = (pulseX.Value, pulseY.Value, 0f);
            }
            if (!_hudVisible)
            {
                _hudVisible = true;
                Visible = true;
            }
            Invalidate();
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            if (!_hudVisible)
                return;

            // Smoothly advance phase for Cyan <-> Blue wave oscillation
            _phase += 0.085;
            if (_phase > Math.PI * 200)
                _phase = 0;

            if (_clickPulse.HasValue)
            {
                var p = _clickPulse.Value;
                float next = p.Progress + 0.12f;
                _clickPulse = next >= 1.0f ? null : (p.X, p.Y, next);
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_hudVisible)
                return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int w = Width;
            int h = Height;

            // Compute oscillating Cyan <-> Deep Royal Blue colors (Claude Computer-Use aesthetic)
            double t = (Math.Sin(_phase) + 1.0) * 0.5;          // 0..1..0
            double t2 = (Math.Sin(_phase + 1.8) + 1.0) * 0.5;   // phase-shifted secondary wave

            Color cyanBright = Color.FromArgb(0, 238, 255);     // Electric Cyan #00EEFF
            Color skyBlue = Color.FromArgb(56, 189, 248);       // Sky Cyan-Blue #38BDF8
            Color royalBlue = Color.FromArgb(37, 99, 235);      // Royal Blue #2563EB
            Color deepIndigo = Color.FromArgb(29, 58, 198);     // Deep Blue #1D3AC6

            Color c1 = LerpColor(cyanBright, royalBlue, t);
            Color c2 = LerpColor(deepIndigo, skyBlue, t2);
            Color c3 = LerpColor(skyBlue, deepIndigo, t);

            float angle = (float)((_phase * 28.0) % 360.0);
            var screenRect = new Rectangle(0, 0, w, h);

            using var gradBrush = new LinearGradientBrush(screenRect, c1, c2, angle);
            var blend = new ColorBlend(4)
            {
                Colors = [c1, c2, c3, c1],
                Positions = [0.0f, 0.35f, 0.70f, 1.0f]
            };
            gradBrush.InterpolationColors = blend;

            // 3-layer glowing border around the screen edges
            using (var outerPen = new Pen(gradBrush, 10f))
            {
                g.DrawRectangle(outerPen, 5, 5, w - 10, h - 10);
            }

            Color innerHighlight = LerpColor(Color.FromArgb(140, 255, 255), Color.FromArgb(80, 180, 255), t);
            using (var corePen = new Pen(innerHighlight, 2.5f))
            {
                g.DrawRectangle(corePen, 2, 2, w - 4, h - 4);
            }

            // Corner accent brackets
            DrawCornerAccents(g, w, h, cyanBright, royalBlue, t);

            // Top-center floating HUD Pill ("🌌 Antigravity Computer-Use • ...")
            DrawTopHudPill(g, w, c1, c2, t);

            // Click ripple animation if active
            if (_clickPulse.HasValue)
            {
                var (px, py, prog) = _clickPulse.Value;
                float radius = 8f + prog * 36f;
                using var pulsePen = new Pen(LerpColor(cyanBright, royalBlue, prog), Math.Max(1.5f, 4f * (1f - prog)));
                g.DrawEllipse(pulsePen, px - radius, py - radius, radius * 2, radius * 2);
            }
        }

        private static void DrawCornerAccents(Graphics g, int w, int h, Color cyan, Color blue, double t)
        {
            using var accentPen = new Pen(LerpColor(cyan, blue, 1.0 - t), 4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            const int len = 34;
            const int m = 8;

            // Top-Left
            g.DrawLine(accentPen, m, m, m + len, m);
            g.DrawLine(accentPen, m, m, m, m + len);
            // Top-Right
            g.DrawLine(accentPen, w - m - len, m, w - m, m);
            g.DrawLine(accentPen, w - m, m, w - m, m + len);
            // Bottom-Left
            g.DrawLine(accentPen, m, h - m, m + len, h - m);
            g.DrawLine(accentPen, m, h - m - len, m, h - m);
            // Bottom-Right
            g.DrawLine(accentPen, w - m - len, h - m, w - m, h - m);
            g.DrawLine(accentPen, w - m, h - m - len, w - m, h - m);
        }

        private void DrawTopHudPill(Graphics g, int screenW, Color c1, Color c2, double t)
        {
            string label = $"ANTIGRAVITY COMPUTER-USE  •  {_statusText}";
            if (label.Length > 68)
                label = label[..65] + "...";

            var textSize = g.MeasureString(label, s_pillFont);
            int pillW = (int)textSize.Width + 44;
            int pillH = 30;
            int pillX = (screenW - pillW) / 2;
            int pillY = 10;

            var pillRect = new Rectangle(pillX, pillY, pillW, pillH);
            using var path = CreateRoundedRectPath(pillRect, 14);
            using var bgBrush = new SolidBrush(Color.FromArgb(14, 22, 42));
            using var borderBrush = new LinearGradientBrush(pillRect, c1, c2, LinearGradientMode.Horizontal);
            using var borderPen = new Pen(borderBrush, 2.2f);

            g.FillPath(bgBrush, path);
            g.DrawPath(borderPen, path);

            // Pulsing Cyan-Blue indicator orb inside pill
            Color orbColor = LerpColor(Color.FromArgb(0, 245, 255), Color.FromArgb(59, 130, 246), t);
            using var orbBrush = new SolidBrush(orbColor);
            g.FillEllipse(orbBrush, pillX + 13, pillY + 9, 11, 11);

            using var textBrush = new SolidBrush(Color.FromArgb(235, 248, 255));
            g.DrawString(label, s_pillFont, textBrush, pillX + 30, pillY + 6);
        }

        private static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(r, g, bl);
        }
    }
}
