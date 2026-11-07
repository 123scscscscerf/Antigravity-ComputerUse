---
name: antigravity-bridge
description: Computer Vision, Mouse/Keyboard HID control, and Silent-Mode background window automation for Windows via the Antigravity Bridge MCP server.
---

# Antigravity Computer-Use & Silent-Mode Bridge Skill

Use the `antigravity-bridge` MCP tools whenever the user asks you to see the screen, control the mouse or keyboard, or automate external Windows applications in the background (**Silent-Mode**).

## Workflow Guidelines

1. **Prefer Silent-Mode for External Apps (`silent_*` / `window_capture_silent`)**
   - Always call `list_windows` first to find the target application's `hwnd`.
   - Use `window_capture_silent(hwnd=..., annotate_ui=True)` to see the window's content and its numbered `[1..N]` UI elements without interrupting the user or stealing focus.
   - Use `silent_automate_element(hwnd=..., name=..., action="invoke" | "set_value")` or `silent_click(hwnd=..., rel_x=..., rel_y=...)` / `silent_type(hwnd=..., text=...)` so the user's physical mouse and active window remain untouched.

2. **Full Screen Vision & Active Mouse/Keyboard Control**
   - Call `screen_capture(draw_grid=True)` to inspect the entire monitor with an `(X, Y)` coordinate grid and red cursor crosshair.
   - Use `mouse_action(action="click", x=..., y=...)` or `mouse_action(action="click", ui_element_id=...)` to click accurately.
   - Use `keyboard_action(text="...", press_enter=True)` for Unicode/Cyrillic text entry, or `keyboard_action(hotkey="ctrl+s")` for shortcuts.

3. **Safety & Emergency Stop**
   - Moving the physical mouse cursor to the top-left corner `(0, 0)` triggers the hardware Fail-Safe in `AntigravityBridge.Core` and aborts active input immediately.
