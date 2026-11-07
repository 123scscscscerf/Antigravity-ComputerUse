using System.Windows.Forms;
using AntigravityBridge.Core.Native;

namespace AntigravityBridge.Core.Input;

/// <summary>
/// Hardware-level global mouse and keyboard input engine using batched Win32 SendInput.
/// Delivers sub-millisecond batched Unicode text entry and instant DWM-normalized mouse actions.
/// </summary>
public sealed class GlobalInputEngine
{
    public bool FailSafeEnabled { get; set; } = true;

    private void EnsureFailSafe()
    {
        Win32.EnsureDefaultDesktop();
        if (!FailSafeEnabled) return;
        if (Win32.GetCursorPos(out var pt) && pt.X <= 2 && pt.Y <= 2)
        {
            throw new InvalidOperationException(
                "FAIL-SAFE TRIGGERED: Mouse cursor is at the top-left corner (0,0). Input aborted.");
        }
    }

    private static (int NormX, int NormY) NormalizeScreenCoords(int x, int y)
    {
        int screenW = Math.Max(2, Screen.PrimaryScreen?.Bounds.Width ?? 1920);
        int screenH = Math.Max(2, Screen.PrimaryScreen?.Bounds.Height ?? 1080);
        int normX = (int)Math.Round((double)x * 65535 / (screenW - 1));
        int normY = (int)Math.Round((double)y * 65535 / (screenH - 1));
        return (normX, normY);
    }

    private static void SendRawMouseMove(int x, int y)
    {
        var (normX, normY) = NormalizeScreenCoords(x, y);
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.InputUnion
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        Win32.SendInput(1, [input], Win32.INPUT.Size);
        Win32.SetCursorPos(x, y);
    }

    public object MoveMouse(int x, int y, int durationMs = 0)
    {
        EnsureFailSafe();

        if (durationMs <= 0)
        {
            SendRawMouseMove(x, y);
        }
        else
        {
            Win32.GetCursorPos(out var start);
            int steps = Math.Clamp(durationMs / 10, 3, 40);
            int sleepStep = Math.Max(1, durationMs / steps);

            for (int i = 1; i <= steps; i++)
            {
                EnsureFailSafe();
                double t = (double)i / steps;
                double ease = t * t * (3.0 - 2.0 * t);
                int curX = (int)Math.Round(start.X + (x - start.X) * ease);
                int curY = (int)Math.Round(start.Y + (y - start.Y) * ease);
                SendRawMouseMove(curX, curY);
                Thread.Sleep(sleepStep);
            }
        }

        Win32.GetCursorPos(out var finalPos);
        return new { x = finalPos.X, y = finalPos.Y };
    }

    public object MouseClick(int? x, int? y, string button = "left", int clicks = 1, int durationMs = 0)
    {
        EnsureFailSafe();

        if (x.HasValue && y.HasValue)
        {
            MoveMouse(x.Value, y.Value, durationMs);
            Thread.Sleep(5);
        }

        (uint downFlag, uint upFlag) = button.ToLowerInvariant() switch
        {
            "right" => (Win32.MOUSEEVENTF_RIGHTDOWN, Win32.MOUSEEVENTF_RIGHTUP),
            "middle" => (Win32.MOUSEEVENTF_MIDDLEDOWN, Win32.MOUSEEVENTF_MIDDLEUP),
            _ => (Win32.MOUSEEVENTF_LEFTDOWN, Win32.MOUSEEVENTF_LEFTUP)
        };

        int count = Math.Clamp(clicks, 1, 3);
        for (int i = 0; i < count; i++)
        {
            SendMouseFlags(downFlag);
            Thread.Sleep(8);
            SendMouseFlags(upFlag);
            if (i + 1 < count)
                Thread.Sleep(25);
        }

        Win32.GetCursorPos(out var pos);
        return new { action = "click", button, clicks = count, x = pos.X, y = pos.Y };
    }

    public object MouseDrag(int startX, int startY, int endX, int endY, string button = "left", int durationMs = 120)
    {
        EnsureFailSafe();

        (uint downFlag, uint upFlag) = button.ToLowerInvariant() switch
        {
            "right" => (Win32.MOUSEEVENTF_RIGHTDOWN, Win32.MOUSEEVENTF_RIGHTUP),
            _ => (Win32.MOUSEEVENTF_LEFTDOWN, Win32.MOUSEEVENTF_LEFTUP)
        };

        MoveMouse(startX, startY, 0);
        Thread.Sleep(15);
        SendMouseFlags(downFlag);
        Thread.Sleep(20);
        MoveMouse(endX, endY, Math.Max(30, durationMs));
        Thread.Sleep(20);
        SendMouseFlags(upFlag);

        return new { action = "drag", startX, startY, endX, endY, button };
    }

    public object MouseScroll(int delta, bool horizontal = false, int? x = null, int? y = null)
    {
        EnsureFailSafe();

        if (x.HasValue && y.HasValue)
        {
            MoveMouse(x.Value, y.Value, 0);
            Thread.Sleep(5);
        }

        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.InputUnion
            {
                mi = new Win32.MOUSEINPUT
                {
                    dwFlags = horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL,
                    mouseData = delta * 120
                }
            }
        };
        Win32.SendInput(1, [input], Win32.INPUT.Size);

        return new { action = "scroll", delta, horizontal };
    }

    /// <summary>
    /// Batched hardware Unicode text entry via KEYEVENTF_UNICODE.
    /// When delayMs == 0 (default), dispatches the entire string in a single kernel SendInput call (<0.5ms for 200 chars!).
    /// </summary>
    public object TypeText(string text, int delayMs = 0, bool pressEnter = false)
    {
        EnsureFailSafe();

        if (delayMs <= 0)
        {
            var batch = new List<Win32.INPUT>(text.Length * 2 + 2);
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    batch.Add(CreateVkInput(0x0D, down: true));
                    batch.Add(CreateVkInput(0x0D, down: false));
                    continue;
                }

                batch.Add(new Win32.INPUT
                {
                    type = Win32.INPUT_KEYBOARD,
                    U = new Win32.InputUnion
                    {
                        ki = new Win32.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = Win32.KEYEVENTF_UNICODE
                        }
                    }
                });
                batch.Add(new Win32.INPUT
                {
                    type = Win32.INPUT_KEYBOARD,
                    U = new Win32.InputUnion
                    {
                        ki = new Win32.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP
                        }
                    }
                });
            }

            if (pressEnter)
            {
                batch.Add(CreateVkInput(0x0D, down: true));
                batch.Add(CreateVkInput(0x0D, down: false));
            }

            if (batch.Count > 0)
            {
                var arr = batch.ToArray();
                Win32.SendInput((uint)arr.Length, arr, Win32.INPUT.Size);
            }

            return new { action = "type_text", mode = "batched_kernel", length = text.Length, pressEnter };
        }

        // Timed character-by-character fallback if delayMs > 0
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                SendVirtualKey(0x0D);
                continue;
            }

            var down = new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                U = new Win32.InputUnion
                {
                    ki = new Win32.KEYBDINPUT { wVk = 0, wScan = c, dwFlags = Win32.KEYEVENTF_UNICODE }
                }
            };
            var up = new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                U = new Win32.InputUnion
                {
                    ki = new Win32.KEYBDINPUT { wVk = 0, wScan = c, dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP }
                }
            };

            Win32.SendInput(2, [down, up], Win32.INPUT.Size);
            Thread.Sleep(delayMs);
        }

        if (pressEnter)
        {
            Thread.Sleep(10);
            SendVirtualKey(0x0D);
        }

        return new { action = "type_text", mode = "sequential", length = text.Length, pressEnter };
    }

    public object SendHotkey(string keys)
    {
        EnsureFailSafe();

        var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vkCodes = parts.Select(ResolveVirtualKey).ToList();

        foreach (ushort vk in vkCodes)
        {
            SendVirtualKeyDown(vk);
            Thread.Sleep(8);
        }

        for (int i = vkCodes.Count - 1; i >= 0; i--)
        {
            SendVirtualKeyUp(vkCodes[i]);
            Thread.Sleep(5);
        }

        return new { action = "hotkey", keys };
    }

    private static Win32.INPUT CreateVkInput(ushort vk, bool down)
    {
        uint flags = IsExtendedKey(vk) ? Win32.KEYEVENTF_EXTENDEDKEY : 0u;
        if (!down)
            flags |= Win32.KEYEVENTF_KEYUP;

        return new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = flags
                }
            }
        };
    }

    private static void SendMouseFlags(uint flags)
    {
        Win32.GetCursorPos(out var pt);
        var (normX, normY) = NormalizeScreenCoords(pt.X, pt.Y);

        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.InputUnion
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    dwFlags = flags | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_MOVE
                }
            }
        };
        Win32.SendInput(1, [input], Win32.INPUT.Size);
    }

    private static void SendVirtualKey(ushort vk)
    {
        SendVirtualKeyDown(vk);
        Thread.Sleep(8);
        SendVirtualKeyUp(vk);
    }

    private static void SendVirtualKeyDown(ushort vk) =>
        Win32.SendInput(1, [CreateVkInput(vk, down: true)], Win32.INPUT.Size);

    private static void SendVirtualKeyUp(ushort vk) =>
        Win32.SendInput(1, [CreateVkInput(vk, down: false)], Win32.INPUT.Size);

    private static bool IsExtendedKey(ushort vk) =>
        vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C;

    public static ushort ResolveVirtualKey(string keyName)
    {
        string k = keyName.Trim().ToLowerInvariant();
        return k switch
        {
            "ctrl" or "control" or "lctrl" => 0x11,
            "alt" or "menu" or "lalt" => 0x12,
            "shift" or "lshift" => 0x10,
            "win" or "lwin" or "super" or "meta" => 0x5B,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "space" or "spacebar" => 0x20,
            "backspace" or "bs" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            _ when k.Length == 1 && char.IsLetterOrDigit(k[0]) => (ushort)char.ToUpperInvariant(k[0]),
            _ => throw new ArgumentException($"Unsupported key name: '{keyName}'")
        };
    }
}
