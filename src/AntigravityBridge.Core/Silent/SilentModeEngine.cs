using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using AntigravityBridge.Core.Input;
using AntigravityBridge.Core.Models;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core.Silent;

/// <summary>
/// Silent-Mode Engine: interacts with background windows without stealing focus or moving the user's cursor.
/// Combines Win32 PostMessage/SendMessage with Windows UI Automation (UIA).
/// </summary>
public sealed class SilentModeEngine
{
    public List<WindowInfo> ListWindows(bool visibleOnly = true, string? filter = null)
    {
        var results = new List<WindowInfo>();
        IntPtr fg = Win32.GetForegroundWindow();

        Win32.EnumWindows((hWnd, _) =>
        {
            bool visible = Win32.IsWindowVisible(hWnd);
            if (visibleOnly && !visible)
                return true;

            int len = Win32.GetWindowTextLength(hWnd);
            if (len == 0 && visibleOnly)
                return true;

            var sbTitle = new StringBuilder(len + 1);
            Win32.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
            string title = sbTitle.ToString();

            if (visibleOnly && string.IsNullOrWhiteSpace(title))
                return true;

            var sbClass = new StringBuilder(256);
            Win32.GetClassName(hWnd, sbClass, sbClass.Capacity);
            string className = sbClass.ToString();

            Win32.GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = string.Empty;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch
            {
                // Ignore inaccessible system processes
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                bool matches = title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || procName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || className.Contains(filter, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                    return true;
            }

            Win32.GetWindowRect(hWnd, out var rect);

            results.Add(new WindowInfo
            {
                Hwnd = hWnd.ToInt64(),
                HwndHex = $"0x{hWnd.ToInt64():X}",
                Title = title,
                ClassName = className,
                ProcessId = pid,
                ProcessName = procName,
                IsVisible = visible,
                IsMinimized = Win32.IsIconic(hWnd),
                IsMaximized = Win32.IsZoomed(hWnd),
                IsForeground = hWnd == fg,
                X = rect.Left,
                Y = rect.Top,
                Width = rect.Width,
                Height = rect.Height
            });

            return true;
        }, IntPtr.Zero);

        return results;
    }

    public object ManageWindow(long hwndValue, string action, int? x = null, int? y = null, int? width = null, int? height = null)
    {
        IntPtr hwnd = new(hwndValue);
        if (!Win32.IsWindow(hwnd))
            throw new ArgumentException($"Window 0x{hwndValue:X} does not exist.");

        string act = action.ToLowerInvariant();
        switch (act)
        {
            case "focus" or "activate":
                if (Win32.IsIconic(hwnd))
                    Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(hwnd);
                break;
            case "minimize":
                Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);
                break;
            case "maximize":
                Win32.ShowWindow(hwnd, Win32.SW_SHOWMAXIMIZED);
                break;
            case "restore":
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
                break;
            case "show_no_activate":
                Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
                break;
            case "hide":
                Win32.ShowWindow(hwnd, Win32.SW_HIDE);
                break;
            case "close":
                Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
            case "move_resize":
                Win32.GetWindowRect(hwnd, out var cur);
                Win32.MoveWindow(
                    hwnd,
                    x ?? cur.Left,
                    y ?? cur.Top,
                    width ?? cur.Width,
                    height ?? cur.Height,
                    true);
                break;
            default:
                throw new ArgumentException($"Unsupported window action: '{action}'");
        }

        return new { hwnd = hwndValue, action = act, ok = true };
    }

    /// <summary>
    /// Sends a mouse click directly to a background window via Win32 PostMessage without moving the physical cursor.
    /// Automatically resolves deepest child HWND at the target relative coordinates.
    /// </summary>
    public object SilentClick(long hwndValue, int relX, int relY, string button = "left", bool doubleClick = false)
    {
        IntPtr rootHwnd = new(hwndValue);
        if (!Win32.IsWindow(rootHwnd))
            throw new ArgumentException($"Invalid HWND: 0x{hwndValue:X}");

        Win32.GetWindowRect(rootHwnd, out var rootRect);
        int screenX = rootRect.Left + relX;
        int screenY = rootRect.Top + relY;

        // Find child window at point if any
        IntPtr targetHwnd = FindDeepestChildAtScreenPoint(rootHwnd, screenX, screenY, out int localX, out int localY);
        IntPtr lParam = Win32.MakeLParam(localX, localY);

        (uint downMsg, uint upMsg, uint dblMsg, IntPtr mkFlag) = button.ToLowerInvariant() switch
        {
            "right" => (Win32.WM_RBUTTONDOWN, Win32.WM_RBUTTONUP, Win32.WM_RBUTTONDBLCLK, (IntPtr)Win32.MK_RBUTTON),
            "middle" => (Win32.WM_MBUTTONDOWN, Win32.WM_MBUTTONUP, Win32.WM_MBUTTONDOWN, (IntPtr)Win32.MK_MBUTTON),
            _ => (Win32.WM_LBUTTONDOWN, Win32.WM_LBUTTONUP, Win32.WM_LBUTTONDBLCLK, (IntPtr)Win32.MK_LBUTTON)
        };

        Win32.PostMessage(targetHwnd, Win32.WM_MOUSEMOVE, IntPtr.Zero, lParam);
        Win32.PostMessage(targetHwnd, downMsg, mkFlag, lParam);
        Thread.Sleep(20);
        Win32.PostMessage(targetHwnd, upMsg, IntPtr.Zero, lParam);

        if (doubleClick)
        {
            Thread.Sleep(30);
            Win32.PostMessage(targetHwnd, dblMsg, mkFlag, lParam);
            Thread.Sleep(20);
            Win32.PostMessage(targetHwnd, upMsg, IntPtr.Zero, lParam);
        }

        return new
        {
            action = "silent_click",
            root_hwnd = $"0x{hwndValue:X}",
            target_hwnd = $"0x{targetHwnd.ToInt64():X}",
            rel_x = relX,
            rel_y = relY,
            local_x = localX,
            local_y = localY,
            button,
            double_click = doubleClick
        };
    }

    /// <summary>
    /// Sends text or keystrokes to a background window via WM_CHAR / WM_KEYDOWN without changing window focus.
    /// </summary>
    public object SilentType(long hwndValue, string? text = null, string? key = null, bool pressEnter = false)
    {
        IntPtr hwnd = new(hwndValue);
        if (!Win32.IsWindow(hwnd))
            throw new ArgumentException($"Invalid HWND: 0x{hwndValue:X}");

        if (!string.IsNullOrEmpty(text))
        {
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    PostKey(hwnd, 0x0D);
                    continue;
                }
                Win32.PostMessage(hwnd, Win32.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                Thread.Sleep(5);
            }
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            ushort vk = GlobalInputEngine.ResolveVirtualKey(key);
            PostKey(hwnd, vk);
        }

        if (pressEnter)
        {
            PostKey(hwnd, 0x0D);
        }

        return new
        {
            action = "silent_type",
            hwnd = $"0x{hwndValue:X}",
            text_length = text?.Length ?? 0,
            key,
            press_enter = pressEnter
        };
    }

    /// <summary>
    /// Inspects the Windows UI Automation (UIA) tree of a target window so Antigravity can see exact buttons, inputs,
    /// and controls with their coordinates and supported silent automation patterns.
    /// </summary>
    public List<UiElementInfo> InspectWindowUi(long hwndValue, int maxElements = 80)
    {
        IntPtr hwnd = new(hwndValue);
        if (!Win32.IsWindow(hwnd))
            throw new ArgumentException($"Invalid HWND: 0x{hwndValue:X}");

        Win32.GetWindowRect(hwnd, out var winRect);
        var root = AutomationElement.FromHandle(hwnd);
        if (root == null)
            return [];

        var allElements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var list = new List<UiElementInfo>();
        int nextId = 1;

        for (int i = 0; i < allElements.Count && list.Count < maxElements; i++)
        {
            try
            {
                var el = allElements[i];
                var current = el.Current;
                if (current.IsOffscreen)
                    continue;

                var rect = current.BoundingRectangle;
                if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height))
                    continue;
                if (rect.Width < 4 || rect.Height < 4)
                    continue;

                string controlType = current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "Custom";
                string name = current.Name ?? string.Empty;
                string autoId = current.AutomationId ?? string.Empty;

                var patterns = new List<string>();
                string? currentValue = null;

                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                    patterns.Add("Invoke");
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object? valPatObj) && valPatObj is ValuePattern vp)
                {
                    patterns.Add("Value");
                    currentValue = vp.Current.Value;
                }
                if (el.TryGetCurrentPattern(TogglePattern.Pattern, out _))
                    patterns.Add("Toggle");
                if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
                    patterns.Add("SelectionItem");
                if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
                    patterns.Add("ExpandCollapse");

                // Keep elements that have patterns, a name/automationId, or are interactive control types
                bool isInteractiveType = controlType is "Button" or "Edit" or "CheckBox" or "RadioButton"
                    or "ComboBox" or "MenuItem" or "Hyperlink" or "TabItem" or "ListItem" or "Document";

                if (patterns.Count == 0 && !isInteractiveType && string.IsNullOrWhiteSpace(name))
                    continue;

                int sx = (int)Math.Round(rect.Left);
                int sy = (int)Math.Round(rect.Top);
                int w = (int)Math.Round(rect.Width);
                int h = (int)Math.Round(rect.Height);

                list.Add(new UiElementInfo
                {
                    Id = nextId++,
                    AutomationId = autoId,
                    Name = name.Length > 120 ? name[..120] : name,
                    ControlType = controlType,
                    ClassName = current.ClassName ?? string.Empty,
                    Hwnd = current.NativeWindowHandle,
                    IsEnabled = current.IsEnabled,
                    ScreenX = sx,
                    ScreenY = sy,
                    RelX = sx - winRect.Left,
                    RelY = sy - winRect.Top,
                    Width = w,
                    Height = h,
                    SupportedPatterns = patterns,
                    CurrentValue = currentValue
                });
            }
            catch
            {
                // Skip transient UIA elements that disappeared mid-traversal
            }
        }

        return list;
    }

    /// <summary>
    /// Invokes a UI element (Button, CheckBox, MenuItem, Input) in Silent-Mode via Windows UI Automation patterns
    /// (InvokePattern, TogglePattern, ValuePattern, SelectionItemPattern) without moving the mouse cursor!
    /// </summary>
    public object SilentAutomateElement(
        long hwndValue,
        string? name = null,
        string? automationId = null,
        string action = "invoke",
        string? value = null)
    {
        IntPtr hwnd = new(hwndValue);
        if (!Win32.IsWindow(hwnd))
            throw new ArgumentException($"Invalid HWND: 0x{hwndValue:X}");

        var root = AutomationElement.FromHandle(hwnd)
            ?? throw new InvalidOperationException("Failed to attach UI Automation to window.");

        Condition cond = Condition.TrueCondition;
        if (!string.IsNullOrWhiteSpace(automationId) && !string.IsNullOrWhiteSpace(name))
        {
            cond = new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
                new PropertyCondition(AutomationElement.NameProperty, name));
        }
        else if (!string.IsNullOrWhiteSpace(automationId))
        {
            cond = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            cond = new PropertyCondition(AutomationElement.NameProperty, name);
        }
        else
        {
            throw new ArgumentException("Specify either 'name' or 'automation_id' to target a UI element.");
        }

        var target = root.FindFirst(TreeScope.Descendants, cond);
        if (target == null && !string.IsNullOrWhiteSpace(name))
        {
            // Fallback: case-insensitive substring match on Name
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (int i = 0; i < all.Count; i++)
            {
                try
                {
                    string elName = all[i].Current.Name ?? string.Empty;
                    if (elName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        target = all[i];
                        break;
                    }
                }
                catch { }
            }
        }

        if (target == null)
            throw new InvalidOperationException($"UI Element not found (name='{name}', automationId='{automationId}').");

        string act = action.ToLowerInvariant();
        switch (act)
        {
            case "invoke" or "click":
                if (target.TryGetCurrentPattern(InvokePattern.Pattern, out object? invObj) && invObj is InvokePattern inv)
                {
                    inv.Invoke();
                    return new { ok = true, pattern = "InvokePattern", name = target.Current.Name };
                }
                if (target.TryGetCurrentPattern(TogglePattern.Pattern, out object? togObj) && togObj is TogglePattern tog)
                {
                    tog.Toggle();
                    return new { ok = true, pattern = "TogglePattern", name = target.Current.Name };
                }
                if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selObj) && selObj is SelectionItemPattern sel)
                {
                    sel.Select();
                    return new { ok = true, pattern = "SelectionItemPattern", name = target.Current.Name };
                }
                // Fallback to SilentClick on center of element
                var rect = target.Current.BoundingRectangle;
                Win32.GetWindowRect(hwnd, out var winRect);
                int cx = (int)(rect.Left + rect.Width / 2) - winRect.Left;
                int cy = (int)(rect.Top + rect.Height / 2) - winRect.Top;
                return SilentClick(hwndValue, cx, cy);

            case "set_value":
                if (target.TryGetCurrentPattern(ValuePattern.Pattern, out object? valObj) && valObj is ValuePattern vp)
                {
                    vp.SetValue(value ?? string.Empty);
                    return new { ok = true, pattern = "ValuePattern.SetValue", name = target.Current.Name, value };
                }
                if (target.Current.NativeWindowHandle != 0)
                {
                    Win32.SendMessage(new IntPtr(target.Current.NativeWindowHandle), Win32.WM_SETTEXT, IntPtr.Zero, value ?? string.Empty);
                    return new { ok = true, pattern = "WM_SETTEXT", name = target.Current.Name, value };
                }
                throw new InvalidOperationException("Target element does not support ValuePattern or WM_SETTEXT.");

            default:
                throw new ArgumentException($"Unsupported UIA action: '{action}'");
        }
    }

    private static void PostKey(IntPtr hwnd, ushort vk)
    {
        Win32.PostMessage(hwnd, Win32.WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
        Thread.Sleep(15);
        Win32.PostMessage(hwnd, Win32.WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
    }

    private static IntPtr FindDeepestChildAtScreenPoint(IntPtr rootHwnd, int screenX, int screenY, out int localX, out int localY)
    {
        IntPtr best = rootHwnd;
        int bestArea = int.MaxValue;

        Win32.EnumChildWindows(rootHwnd, (childHwnd, _) =>
        {
            if (!Win32.IsWindowVisible(childHwnd))
                return true;

            if (Win32.GetWindowRect(childHwnd, out var r))
            {
                if (screenX >= r.Left && screenX < r.Right && screenY >= r.Top && screenY < r.Bottom)
                {
                    int area = Math.Max(1, r.Width * r.Height);
                    if (area <= bestArea)
                    {
                        bestArea = area;
                        best = childHwnd;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        Win32.GetWindowRect(best, out var targetRect);
        localX = screenX - targetRect.Left;
        localY = screenY - targetRect.Top;
        return best;
    }
}
