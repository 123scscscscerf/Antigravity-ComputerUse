using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core.Overlay;

/// <summary>
/// Mandatory User Consent & Permission Guard for launching external programs and UAC/Privilege Elevation.
/// Renders a modern Cyan <-> Deep Blue shimmering consent modal on the interactive desktop.
/// </summary>
public sealed class PermissionGuard
{
    private readonly ConcurrentDictionary<string, bool> _sessionAllowList = new(StringComparer.OrdinalIgnoreCase);

    public bool RequireConsentForLaunch { get; set; } = true;
    public bool RequireConsentForElevation { get; set; } = true;

    public static bool IsElevationLikelyRequired(string fileName, string? arguments, bool explicitElevation)
    {
        if (explicitElevation)
            return true;

        string lowerFile = fileName.ToLowerInvariant();
        string lowerArgs = (arguments ?? string.Empty).ToLowerInvariant();

        return lowerFile.Contains("install")
            || lowerFile.Contains("setup")
            || lowerFile.Contains("update")
            || lowerFile.Contains("regedit")
            || lowerFile.Contains("msiexec")
            || lowerFile.Contains("diskpart")
            || lowerFile.Contains("bcdedit")
            || lowerFile.Contains("netsh")
            || lowerFile.Contains("sc.exe")
            || lowerArgs.Contains("runas")
            || lowerArgs.Contains("-verb runas");
    }

    public (bool Allowed, string Decision, bool Remembered) RequestUserPermission(
        string operationType,
        string targetProgram,
        string? arguments = null,
        string? reason = null,
        bool isElevation = false,
        int timeoutSeconds = 60)
    {
        string key = $"{operationType}:{Path.GetFileName(targetProgram)}:{isElevation}";
        if (_sessionAllowList.TryGetValue(key, out bool cachedAllowed) && cachedAllowed)
        {
            return (true, "allowed_by_session_cache", true);
        }

        if (!RequireConsentForLaunch && !isElevation)
            return (true, "policy_auto_allowed", false);

        if (!RequireConsentForElevation && isElevation)
            return (true, "policy_auto_allowed", false);

        (bool allowed, string decision, bool remember) result = (false, "denied", false);

        var dialogThread = new Thread(() =>
        {
            Win32.EnsureDefaultDesktop();
            try
            {
                Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch { }

            using var dlg = new ConsentModalForm(operationType, targetProgram, arguments, reason, isElevation, timeoutSeconds);
            Win32.ForceForegroundWindow(dlg.Handle);
            dlg.ShowDialog();
            result = (dlg.Allowed, dlg.Decision, dlg.RememberForSession);
        });

        dialogThread.SetApartmentState(ApartmentState.STA);
        dialogThread.Start();
        dialogThread.Join();

        if (result.allowed && result.remember)
        {
            _sessionAllowList[key] = true;
        }

        return result;
    }

    private sealed class ConsentModalForm : Form
    {
        private readonly string _operationType;
        private readonly string _targetProgram;
        private readonly string _arguments;
        private readonly string _reason;
        private readonly bool _isElevation;
        private int _remainingSeconds;
        private double _phase;

        private readonly System.Windows.Forms.Timer _shimmerTimer;
        private readonly System.Windows.Forms.Timer _countdownTimer;

        private Rectangle _allowBtnRect;
        private Rectangle _allowSessionBtnRect;
        private Rectangle _denyBtnRect;
        private int _hoveredBtn; // 0=none, 1=allow, 2=session, 3=deny

        public bool Allowed { get; private set; }
        public bool RememberForSession { get; private set; }
        public string Decision { get; private set; } = "denied";

        public ConsentModalForm(
            string operationType,
            string targetProgram,
            string? arguments,
            string? reason,
            bool isElevation,
            int timeoutSeconds)
        {
            _operationType = operationType;
            _targetProgram = targetProgram;
            _arguments = arguments ?? string.Empty;
            _reason = string.IsNullOrWhiteSpace(reason)
                ? (isElevation
                    ? "Агент запрашивает запуск с повышенными правами Администратора (UAC)."
                    : "Агент запрашивает разрешение на открытие внешней программы.")
                : reason;
            _isElevation = isElevation;
            _remainingSeconds = Math.Clamp(timeoutSeconds, 10, 300);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(560, 310);
            TopMost = true;
            ShowInTaskbar = true;
            Text = "Antigravity Security — Запрос разрешения";
            BackColor = Color.FromArgb(13, 19, 36);
            DoubleBuffered = true;

            _allowBtnRect = new Rectangle(28, 242, 165, 44);
            _allowSessionBtnRect = new Rectangle(205, 242, 195, 44);
            _denyBtnRect = new Rectangle(412, 242, 120, 44);

            _shimmerTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _shimmerTimer.Tick += (_, _) =>
            {
                _phase += 0.10;
                Invalidate();
            };
            _shimmerTimer.Start();

            _countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _countdownTimer.Tick += (_, _) =>
            {
                _remainingSeconds--;
                if (_remainingSeconds <= 0)
                {
                    Allowed = false;
                    Decision = "timeout_denied";
                    Close();
                }
                Invalidate();
            };
            _countdownTimer.Start();

            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    Approve(remember: false);
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    Deny();
                }
            };
        }

        private void Approve(bool remember)
        {
            Allowed = true;
            RememberForSession = remember;
            Decision = remember ? "user_allowed_session" : "user_allowed_once";
            Close();
        }

        private void Deny()
        {
            Allowed = false;
            RememberForSession = false;
            Decision = "user_denied";
            Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int prev = _hoveredBtn;
            if (_allowBtnRect.Contains(e.Location)) _hoveredBtn = 1;
            else if (_allowSessionBtnRect.Contains(e.Location)) _hoveredBtn = 2;
            else if (_denyBtnRect.Contains(e.Location)) _hoveredBtn = 3;
            else _hoveredBtn = 0;

            Cursor = _hoveredBtn != 0 ? Cursors.Hand : Cursors.Default;
            if (prev != _hoveredBtn)
                Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_allowBtnRect.Contains(e.Location))
                Approve(remember: false);
            else if (_allowSessionBtnRect.Contains(e.Location))
                Approve(remember: true);
            else if (_denyBtnRect.Contains(e.Location))
                Deny();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            double t = (Math.Sin(_phase) + 1.0) * 0.5;
            Color cyan = Color.FromArgb(0, 238, 255);
            Color royalBlue = Color.FromArgb(37, 99, 235);
            Color c1 = LerpColor(cyan, royalBlue, t);
            Color c2 = LerpColor(royalBlue, cyan, t);

            var fullRect = new Rectangle(0, 0, Width, Height);
            using var borderGrad = new LinearGradientBrush(fullRect, c1, c2, (float)((_phase * 35) % 360));
            using var borderPen = new Pen(borderGrad, 4f);
            g.DrawRectangle(borderPen, 2, 2, Width - 4, Height - 4);

            // Top header bar
            var headerRect = new Rectangle(4, 4, Width - 8, 54);
            using var headerBrush = new LinearGradientBrush(
                headerRect,
                Color.FromArgb(20, 38, 76),
                Color.FromArgb(14, 23, 46),
                LinearGradientMode.Horizontal);
            g.FillRectangle(headerBrush, headerRect);

            using var titleFont = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            using var badgeFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var labelFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            using var monoFont = new Font("Consolas", 9.5f, FontStyle.Bold);

            using var cyanBrush = new SolidBrush(c1);
            g.FillEllipse(cyanBrush, 22, 24, 12, 12);

            string headerTitle = _isElevation
                ? "🛡️ ANTIGRAVITY — ЗАПРОС ПОВЫШЕНИЯ ПРАВ (ADMIN / UAC)"
                : "🚀 ANTIGRAVITY — ЗАПРОС НА ОТКРЫТИЕ ПРОГРАММЫ";
            g.DrawString(headerTitle, titleFont, Brushes.White, 42, 18);

            // Details Card
            var cardRect = new Rectangle(28, 74, Width - 56, 148);
            using var cardBg = new SolidBrush(Color.FromArgb(19, 29, 54));
            using var cardBorder = new Pen(Color.FromArgb(45, 85, 160), 1f);
            g.FillRectangle(cardBg, cardRect);
            g.DrawRectangle(cardBorder, cardRect);

            using var mutedBrush = new SolidBrush(Color.FromArgb(148, 178, 218));
            using var brightBrush = new SolidBrush(Color.FromArgb(230, 244, 255));
            using var accentTextBrush = new SolidBrush(Color.FromArgb(56, 225, 255));

            g.DrawString("Действие:", badgeFont, mutedBrush, 44, 88);
            g.DrawString(
                _isElevation ? "Повышение привилегий (Run as Administrator / Установка)" : $"Запуск программы ({_operationType})",
                badgeFont,
                accentTextBrush,
                135,
                88);

            g.DrawString("Программа:", badgeFont, mutedBrush, 44, 116);
            string cmdDisplay = string.IsNullOrWhiteSpace(_arguments)
                ? _targetProgram
                : $"{_targetProgram} {_arguments}";
            if (cmdDisplay.Length > 52)
                cmdDisplay = "..." + cmdDisplay[^49..];
            g.DrawString(cmdDisplay, monoFont, brightBrush, 135, 116);

            g.DrawString("Причина:", badgeFont, mutedBrush, 44, 146);
            var reasonRect = new RectangleF(44, 168, Width - 88, 44);
            g.DrawString(_reason, labelFont, brightBrush, reasonRect);

            // Buttons
            DrawButton(g, _allowBtnRect, $"✓ Разрешить ({_remainingSeconds}с)", c1, c2, primary: true, hovered: _hoveredBtn == 1);
            DrawButton(g, _allowSessionBtnRect, "✓ Разрешить на сессию", Color.FromArgb(30, 64, 130), Color.FromArgb(25, 50, 105), primary: false, hovered: _hoveredBtn == 2);
            DrawButton(g, _denyBtnRect, "✕ Запретить", Color.FromArgb(120, 30, 45), Color.FromArgb(90, 22, 35), primary: false, hovered: _hoveredBtn == 3);
        }

        private static void DrawButton(
            Graphics g,
            Rectangle rect,
            string text,
            Color c1,
            Color c2,
            bool primary,
            bool hovered)
        {
            using var brush = new LinearGradientBrush(
                rect,
                hovered ? ControlPaint.Light(c1, 0.2f) : c1,
                hovered ? ControlPaint.Light(c2, 0.2f) : c2,
                LinearGradientMode.Horizontal);
            using var pen = new Pen(primary ? Color.FromArgb(160, 245, 255) : Color.FromArgb(80, 130, 200), hovered ? 2f : 1f);
            g.FillRectangle(brush, rect);
            g.DrawRectangle(pen, rect);

            using var font = new Font("Segoe UI", 9.2f, FontStyle.Bold);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, rect, sf);
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }
}
