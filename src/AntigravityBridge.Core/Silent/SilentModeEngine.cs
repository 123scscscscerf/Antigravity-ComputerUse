using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using AntigravityBridge.Core.Input;
using AntigravityBridge.Core.Models;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core.Silent;

/// <summary>
/// Turbo Silent-Mode Engine: interacts with background windows without stealing focus or moving the user's cursor.
/// Uses bulk UIA CacheRequest (10x faster tree inspection) and QueryFullProcessImageName (<1ms window listing).
/// </summary>
public sealed class SilentModeEngine
{
    public List<WindowInfo> ListWindows(bool visibleOnly = true, string? filter = null)
    {
        Win32.EnsureDefaultDesktop();
        var results = new List<WindowInfo>();
        IntPtr fg = Win32.GetForegroundWindow();

        var sbTitle = new StringBuilder(512);
        var sbClass = new StringBuilder(256);

        Win32.EnumWindows((hWnd, _) =>
        {
            bool visible = Win32.IsWindowVisible(hWnd);
            if (visibleOnly && !visible)
                return true;

            int len = Win32.GetWindowTextLength(hWnd);
            if (len == 0 && visibleOnly)
                return true;

            sbTitle.Clear();
            if (len > 0)
                Win32.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
            string title = sbTitle.ToString();

            if (visibleOnly && string.IsNullOrWhiteSpace(title))
                return true;

            sbClass.Clear();
            Win32.GetClassName(hWnd, sbClass, sbClass.Capacity);
            string className = sbClass.ToString();

            Win32.GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = Win32.GetFastProcessName(pid);

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

    /// <summary>
    /// Waits for a window matching filter (title/process/class) to appear within timeoutMs.
    /// Eliminates blind sleeps and extra LLM roundtrips!
    /// </summary>
    public WindowInfo WaitForWindow(string filter, int timeoutMs = 3000, bool focus = false)
    {
        Win32.EnsureDefaultDesktop();
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds <= timeoutMs)
        {
            var matches = ListWindows(visibleOnly: true, filter: filter);
            if (matches.Count == 0)
                matches = ListWindows(visibleOnly: false, filter: filter).Where(w => w.Width > 10 && w.Height > 10).ToList();

            if (matches.Count > 0)
            {
                var win = matches[0];
                if (focus)
                    ManageWindow(win.Hwnd, "focus");
                return win;
            }
            Thread.Sleep(25);
        }

        throw new TimeoutException($"Window matching '{filter}' did not appear within {timeoutMs} ms.");
    }

    public object LaunchApp(string fileName, string? arguments = null, bool hidden = false, string? waitFilter = null, int timeoutMs = 3000)
    {
        Win32.EnsureDefaultDesktop();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = !hidden,
            CreateNoWindow = hidden,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch '{fileName}'.");

        WindowInfo? window = null;
        if (!string.IsNullOrWhiteSpace(waitFilter))
        {
            window = WaitForWindow(waitFilter, timeoutMs, focus: !hidden);
        }

        return new
        {
            ok = true,
            pid = proc.Id,
            file_name = fileName,
            window
        };
    }

    public object ManageWindow(long hwndValue, string action, int? x = null, int? y = null, int? width = null, int? height = null)
    {
        Win32.EnsureDefaultDesktop();
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

    public object SilentClick(long hwndValue, int relX, int relY, string button = "left", bool doubleClick = false)
    {
        Win32.EnsureDefaultDesktop();
        IntPtr rootHwnd = new(hwndValue);
        if (!Win32.IsWindow(rootHwnd))
            throw new ArgumentException($"Invalid HWND: 0x{hwndValue:X}");

        Win32.GetWindowRect(rootHwnd, out var rootRect);
        int screenX = rootRect.Left + relX;
        int screenY = rootRect.Top + relY;

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
        Thread.Sleep(5);
        Win32.PostMessage(targetHwnd, upMsg, IntPtr.Zero, lParam);

        // If target child control is a Win32 Button, also post BM_CLICK so NSIS/Win32 dialogs trigger BN_CLICKED even in background
        var sbCls = new StringBuilder(64);
        Win32.GetClassName(targetHwnd, sbCls, sbCls.Capacity);
        if (button.Equals("left", StringComparison.OrdinalIgnoreCase) &&
            sbCls.ToString().Contains("Button", StringComparison.OrdinalIgnoreCase))
        {
            Win32.PostMessage(targetHwnd, Win32.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }

        if (doubleClick)
        {
            Thread.Sleep(10);
            Win32.PostMessage(targetHwnd, dblMsg, mkFlag, lParam);
            Thread.Sleep(5);
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

    public object SilentType(long hwndValue, string? text = null, string? key = null, bool pressEnter = false)
    {
        Win32.EnsureDefaultDesktop();
        IntPtr rootHwnd = new(hwndValue);
        if (!Win32.IsWindow(rootHwnd))
            throw new ArgumentException($"Invalid HWND: 0x{hwndValue:X}");

        IntPtr targetHwnd = ResolveTextInputChild(rootHwnd);

        if (!string.IsNullOrEmpty(text))
        {
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    PostKey(targetHwnd, 0x0D);
                    continue;
                }
                Win32.PostMessage(targetHwnd, Win32.WM_CHAR, (IntPtr)c, IntPtr.Zero);
            }
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            ushort vk = GlobalInputEngine.ResolveVirtualKey(key);
            PostKey(targetHwnd, vk);
        }

        if (pressEnter)
        {
            PostKey(targetHwnd, 0x0D);
        }

        return new
        {
            action = "silent_type",
            root_hwnd = $"0x{hwndValue:X}",
            target_hwnd = $"0x{targetHwnd.ToInt64():X}",
            text_length = text?.Length ?? 0,
            key,
            press_enter = pressEnter
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long TimestampMs, List<UiElementInfo> Elements)> s_uiaCache = new();
    private static readonly Condition s_onScreenCondition =
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false);

    /// <summary>
    /// Bulk-cached Windows UI Automation (UIA) inspection.
    /// Uses server-side IsOffscreen=false filtering, CacheRequest bulk COM fetch, and a short 250ms TTL cache.
    /// </summary>
    public List<UiElementInfo> InspectWindowUi(long hwndValue, int maxElements = 80)
    {
        Win32.EnsureDefaultDesktop();
        IntPtr hwnd = new(hwndValue);
        if (!Win32.IsWindow(hwnd))
            throw new ArgumentException($"Invalid HWND: 0x{hwndValue:X}");

        long nowMs = Environment.TickCount64;
        if (s_uiaCache.TryGetValue(hwndValue, out var cachedEntry) && (nowMs - cachedEntry.TimestampMs) < 250)
        {
            return cachedEntry.Elements;
        }

        Win32.GetWindowRect(hwnd, out var winRect);

        var cacheRequest = new CacheRequest
        {
            TreeScope = TreeScope.Element | TreeScope.Descendants,
            AutomationElementMode = AutomationElementMode.Full
        };
        cacheRequest.Add(AutomationElement.NameProperty);
        cacheRequest.Add(AutomationElement.AutomationIdProperty);
        cacheRequest.Add(AutomationElement.ControlTypeProperty);
        cacheRequest.Add(AutomationElement.ClassNameProperty);
        cacheRequest.Add(AutomationElement.NativeWindowHandleProperty);
        cacheRequest.Add(AutomationElement.IsEnabledProperty);
        cacheRequest.Add(AutomationElement.BoundingRectangleProperty);

        AutomationElementCollection allElements;
        using (cacheRequest.Activate())
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return [];
            allElements = root.FindAll(TreeScope.Descendants, s_onScreenCondition);
        }

        var list = new List<UiElementInfo>();
        int nextId = 1;

        for (int i = 0; i < allElements.Count && list.Count < maxElements; i++)
        {
            try
            {
                var cached = allElements[i].Cached;
                var rect = cached.BoundingRectangle;
                if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height))
                    continue;
                if (rect.Width < 4 || rect.Height < 4)
                    continue;

                string controlType = cached.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "Custom";
                string name = cached.Name ?? string.Empty;
                string autoId = cached.AutomationId ?? string.Empty;

                bool isInteractiveType = controlType is "Button" or "Edit" or "CheckBox" or "RadioButton"
                    or "ComboBox" or "MenuItem" or "Hyperlink" or "TabItem" or "ListItem" or "Document" or "SplitButton";

                if (!isInteractiveType && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(autoId))
                    continue;

                var patterns = new List<string>(2);
                if (controlType is "Button" or "MenuItem" or "Hyperlink" or "SplitButton")
                    patterns.Add("Invoke");
                else if (controlType is "Edit" or "Document" or "ComboBox")
                    patterns.Add("Value");
                else if (controlType is "CheckBox" or "RadioButton")
                    patterns.Add("Toggle");
                else if (controlType is "TabItem" or "ListItem")
                    patterns.Add("SelectionItem");

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
                    ClassName = cached.ClassName ?? string.Empty,
                    Hwnd = cached.NativeWindowHandle,
                    IsEnabled = cached.IsEnabled,
                    ScreenX = sx,
                    ScreenY = sy,
                    RelX = sx - winRect.Left,
                    RelY = sy - winRect.Top,
                    Width = w,
                    Height = h,
                    SupportedPatterns = patterns
                });
            }
            catch
            {
            }
        }

        s_uiaCache[hwndValue] = (nowMs, list);
        return list;
    }

    public object SilentAutomateElement(
        long hwndValue,
        string? name = null,
        string? automationId = null,
        string action = "invoke",
        string? value = null)
    {
        Win32.EnsureDefaultDesktop();
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
                    try
                    {
                        inv.Invoke();
                        return new { ok = true, pattern = "InvokePattern", name = target.Current.Name };
                    }
                    catch
                    {
                        // Fallback to BM_CLICK / SilentClick when Win32 dialog button rejects UIA Invoke in background
                    }
                }
                if (target.TryGetCurrentPattern(TogglePattern.Pattern, out object? togObj) && togObj is TogglePattern tog)
                {
                    try
                    {
                        tog.Toggle();
                        return new { ok = true, pattern = "TogglePattern", name = target.Current.Name };
                    }
                    catch { }
                }
                if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selObj) && selObj is SelectionItemPattern sel)
                {
                    try
                    {
                        sel.Select();
                        return new { ok = true, pattern = "SelectionItemPattern", name = target.Current.Name };
                    }
                    catch { }
                }
                if (target.Current.NativeWindowHandle != 0)
                {
                    IntPtr childHwnd = new(target.Current.NativeWindowHandle);
                    Win32.PostMessage(childHwnd, Win32.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    return new { ok = true, pattern = "BM_CLICK", name = target.Current.Name, hwnd = $"0x{childHwnd.ToInt64():X}" };
                }
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
        Thread.Sleep(5);
        Win32.PostMessage(hwnd, Win32.WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
    }

    private static IntPtr ResolveTextInputChild(IntPtr rootHwnd)
    {
        IntPtr bestChild = IntPtr.Zero;
        Win32.EnumChildWindows(rootHwnd, (childHwnd, _) =>
        {
            if (!Win32.IsWindowVisible(childHwnd))
                return true;

            var sbClass = new StringBuilder(128);
            Win32.GetClassName(childHwnd, sbClass, sbClass.Capacity);
            string cls = sbClass.ToString();

            if (cls.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("Scintilla", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("TextBox", StringComparison.OrdinalIgnoreCase))
            {
                bestChild = childHwnd;
                return false;
            }

            if (bestChild == IntPtr.Zero)
                bestChild = childHwnd;

            return true;
        }, IntPtr.Zero);

        return bestChild != IntPtr.Zero ? bestChild : rootHwnd;
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
