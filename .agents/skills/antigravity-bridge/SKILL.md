---
name: antigravity-bridge
description: Turbo Computer Vision, Mouse/Keyboard HID control, Silent-Mode background window automation, and single-turn Macro Batch execution for Windows via the Antigravity Bridge MCP server.
---

# Antigravity Computer-Use & Silent-Mode Bridge Skill (v0.2 Turbo)

Use the `antigravity-bridge` MCP tools whenever the user asks you to see the screen, control the mouse or keyboard, or automate external Windows applications in the background (**Silent-Mode**).

## ⚡ Speed Rule #1: Always Prefer `execute_batch` for Multi-Step Workflows

Unlike traditional Computer-Use agents (such as Claude Computer-Use) that waste 1 full LLM turn per click or keystroke, **Antigravity Bridge** supports atomic kernel-speed macro pipelines via `execute_batch`:

- Combine `launch_app` / `wait_window` / `mouse_click` / `keyboard_type` / `keyboard_hotkey` / `silent_click` / `silent_type` / `silent_automate_element` into a single `execute_batch(steps=[...], capture_after=True)` call!
- If a step runs `wait_window` (e.g., `{"op": "wait_window", "filter": "Notepad", "focus": true}`), its resolved `hwnd` is **automatically bound** to subsequent `silent_type`, `silent_click`, `silent_automate_element`, or `manage_window` steps in the same batch!

### Example: Launch Notepad & Type Cyrillic Text in 1 Single Turn (<150ms)
```json
{
  "steps": [
    {"op": "launch_app", "file_name": "notepad.exe", "wait_filter": "Notepad"},
    {"op": "silent_type", "text": "Привет из Antigravity Turbo!"}
  ],
  "capture_after": true
}
```

## Workflow Guidelines

1. **Prefer Silent-Mode for External Apps (`silent_*` / `window_capture_silent`)**
   - Call `list_windows` (<1.5ms) to find the target application's `hwnd`, or use `launch_app(..., wait_filter=...)`.
   - Use `window_capture_silent(hwnd=..., annotate_ui=True)` (~14ms) to see the window's content and its numbered `[1..N]` UI elements without interrupting the user or stealing focus.
   - Use `silent_automate_element(hwnd=..., name=..., action="invoke" | "set_value")` or `silent_click` / `silent_type` so the user's physical mouse and active window remain untouched.

2. **Full Screen Vision & Active Mouse/Keyboard Control**
   - Call `screen_capture(draw_grid=True)` (~49ms) to inspect the entire monitor with an `(X, Y)` coordinate grid, red cursor crosshair, and automatic `diff_percent` / `screen_changed` detection.
   - Use `mouse_action(action="click", x=..., y=...)` or `mouse_action(action="click", ui_element_id=...)` to click accurately.
   - Use `keyboard_action(text="...", press_enter=True)` for single-batch kernel `KEYEVENTF_UNICODE` text entry (<0.5ms for 200 chars!).

3. **Safety & Emergency Stop**
   - Moving the physical mouse cursor to the top-left corner `(0, 0)` triggers the hardware Fail-Safe in `AntigravityBridge.Core` and aborts active input immediately.
