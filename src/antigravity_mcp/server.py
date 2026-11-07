"""Google Antigravity MCP Server (Computer Vision, HID Input, Silent-Mode Automation & Turbo Batch Engine).

Implements the Model Context Protocol (MCP) over stdio with zero-copy pass-through from the C# .NET 8 Native Core.
"""

from __future__ import annotations

import json
import sys
import time
from typing import Any

from .core_client import CoreBridgeClient
from .safety import SafetyGuard
from .vision import VisionProcessor


class AntigravityBridgeServer:
    """MCP Server bridging Google Antigravity to the C# .NET 8 Native Windows Core."""

    def __init__(self) -> None:
        self.core = CoreBridgeClient()
        self.vision = VisionProcessor()
        self.safety = SafetyGuard()
        self._last_ui_elements: dict[int, dict[str, Any]] = {}

    def get_mcp_tools_schema(self) -> list[dict[str, Any]]:
        return [
            {
                "name": "screen_capture",
                "description": (
                    "Captures a screenshot of the screen or region so Antigravity can see the display. "
                    "Uses hardware-accelerated C# GDI+ grid overlay, cursor crosshair, and perceptual diffing (<25ms)."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "monitor_index": {"type": "integer", "default": 0, "description": "Monitor index (0 = primary)"},
                        "draw_grid": {"type": "boolean", "default": True, "description": "Overlay (X, Y) coordinate grid"},
                        "grid_step": {"type": "integer", "default": 200, "description": "Grid spacing in pixels"},
                        "format": {"type": "string", "enum": ["jpeg", "png"], "default": "jpeg", "description": "Fast JPEG (default) or lossless PNG"},
                        "quality": {"type": "integer", "default": 82, "description": "JPEG quality (30..100)"},
                        "x": {"type": "integer", "description": "Optional crop region left X"},
                        "y": {"type": "integer", "description": "Optional crop region top Y"},
                        "width": {"type": "integer", "description": "Optional crop region width"},
                        "height": {"type": "integer", "description": "Optional crop region height"},
                    },
                },
            },
            {
                "name": "window_capture_silent",
                "description": (
                    "SILENT-MODE: Captures a screenshot of a specific window (HWND) in the background "
                    "using Win32 PrintWindow (PW_RENDERFULLCONTENT) WITHOUT stealing focus or bringing it to front. "
                    "Annotates interactive UI controls with [1..N] Set-of-Marks badges."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "hwnd": {"type": ["integer", "string"], "description": "Window handle (decimal or hex like '0x1A2B')"},
                        "annotate_ui": {"type": "boolean", "default": True, "description": "Draw [ID] badges on interactive UI elements"},
                        "draw_grid": {"type": "boolean", "default": True, "description": "Overlay window-relative coordinate grid"},
                        "format": {"type": "string", "enum": ["jpeg", "png"], "default": "jpeg"},
                    },
                    "required": ["hwnd"],
                },
            },
            {
                "name": "execute_batch",
                "description": (
                    "TURBO PIPELINE (5x-10x faster than single-step Computer Use): Executes an ordered sequence of "
                    "actions (mouse_click, mouse_move, keyboard_type, keyboard_hotkey, wait_window, launch_app, "
                    "manage_window, silent_click, silent_type, silent_automate_element, sleep) in a SINGLE atomic call "
                    "inside the C# kernel and optionally returns a verification screenshot at the end."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "steps": {
                            "type": "array",
                            "description": "Ordered list of operations, e.g. [{'op': 'launch_app', 'file_name': 'notepad', 'wait_filter': 'Notepad'}, {'op': 'silent_type', 'text': 'Hello!'}]",
                            "items": {"type": "object"},
                        },
                        "capture_after": {"type": "boolean", "default": True, "description": "Return a verification screenshot after batch completes"},
                        "settle_ms": {"type": "integer", "default": 40, "description": "Wait time in ms before final screenshot"},
                        "draw_grid": {"type": "boolean", "default": True},
                    },
                    "required": ["steps"],
                },
            },
            {
                "name": "launch_app",
                "description": "Launches an application or command and optionally waits for its window to appear (returning its HWND immediately).",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "file_name": {"type": "string", "description": "Executable name or path (e.g. 'notepad.exe', 'calc.exe')"},
                        "arguments": {"type": "string", "description": "Optional CLI arguments"},
                        "hidden": {"type": "boolean", "default": False, "description": "Launch hidden in background"},
                        "wait_filter": {"type": "string", "description": "Optional window title/process substring to wait for"},
                        "timeout_ms": {"type": "integer", "default": 3000},
                    },
                    "required": ["file_name"],
                },
            },
            {
                "name": "list_windows",
                "description": "Lists open windows (<2ms) with their HWND, title, process name, coordinates, and state.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "visible_only": {"type": "boolean", "default": True},
                        "filter": {"type": "string", "description": "Filter by window title, process name, or class"},
                    },
                },
            },
            {
                "name": "inspect_window_ui",
                "description": (
                    "SILENT-MODE: Bulk-reads the Windows UI Automation (UIA) accessibility tree of a target window (HWND) in <15ms. "
                    "Returns buttons, inputs, checkboxes, their [id], Name, AutomationId, coordinates, and supported patterns."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "hwnd": {"type": ["integer", "string"], "description": "Target window handle"},
                        "max_elements": {"type": "integer", "default": 80},
                    },
                    "required": ["hwnd"],
                },
            },
            {
                "name": "mouse_action",
                "description": (
                    "Performs global mouse actions (click, double_click, right_click, move, drag, scroll). "
                    "Can target exact screen coordinates (x, y) OR a UI element badge [ui_element_id] from inspect_window_ui."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "action": {
                            "type": "string",
                            "enum": ["click", "double_click", "right_click", "middle_click", "move", "drag", "scroll"],
                            "default": "click",
                        },
                        "x": {"type": "integer", "description": "Screen X coordinate"},
                        "y": {"type": "integer", "description": "Screen Y coordinate"},
                        "ui_element_id": {"type": "integer", "description": "Optional [id] from inspect_window_ui"},
                        "end_x": {"type": "integer", "description": "Destination X for drag"},
                        "end_y": {"type": "integer", "description": "Destination Y for drag"},
                        "scroll_delta": {"type": "integer", "description": "Wheel ticks (+up / -down)"},
                        "duration_ms": {"type": "integer", "default": 0, "description": "0 = instant (<2ms), >0 = smooth interpolation"},
                    },
                    "required": ["action"],
                },
            },
            {
                "name": "keyboard_action",
                "description": (
                    "Performs global keyboard actions: types full Unicode/Cyrillic text in a single batched kernel SendInput call (<1ms) "
                    "or presses hotkey combinations (e.g. 'ctrl+c', 'alt+tab', 'win+r', 'enter')."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "text": {"type": "string", "description": "Text to type (supports Cyrillic, English, symbols)"},
                        "hotkey": {"type": "string", "description": "Key or combination like 'ctrl+shift+esc', 'enter', 'alt+f4'"},
                        "press_enter": {"type": "boolean", "default": False, "description": "Press Enter after typing text"},
                        "delay_ms": {"type": "integer", "default": 0, "description": "0 = single-batch kernel injection (<1ms)"},
                    },
                },
            },
            {
                "name": "silent_click",
                "description": (
                    "SILENT-MODE: Sends a mouse click directly to a background window (HWND) via Win32 PostMessage "
                    "WITHOUT moving the user's physical mouse cursor or stealing window focus."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "hwnd": {"type": ["integer", "string"], "description": "Target window handle"},
                        "rel_x": {"type": "integer", "description": "X coordinate relative to the window's top-left corner"},
                        "rel_y": {"type": "integer", "description": "Y coordinate relative to the window's top-left corner"},
                        "button": {"type": "string", "enum": ["left", "right", "middle"], "default": "left"},
                        "double_click": {"type": "boolean", "default": False},
                    },
                    "required": ["hwnd", "rel_x", "rel_y"],
                },
            },
            {
                "name": "silent_type",
                "description": (
                    "SILENT-MODE: Sends text (WM_CHAR) or keystrokes (WM_KEYDOWN) directly into a background window "
                    "(automatically resolving child Edit/RichEdit controls) without changing window focus."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "hwnd": {"type": ["integer", "string"], "description": "Target window handle"},
                        "text": {"type": "string", "description": "Text to send in background"},
                        "key": {"type": "string", "description": "Optional special key like 'enter', 'tab', 'escape'"},
                        "press_enter": {"type": "boolean", "default": False},
                    },
                    "required": ["hwnd"],
                },
            },
            {
                "name": "silent_automate_element",
                "description": (
                    "SILENT-MODE: Invokes a button, toggles a checkbox, or sets an input field value in a background window "
                    "using Windows UI Automation (InvokePattern / ValuePattern / TogglePattern) with zero mouse interference."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "hwnd": {"type": ["integer", "string"], "description": "Target window handle"},
                        "name": {"type": "string", "description": "Element Name (or substring)"},
                        "automation_id": {"type": "string", "description": "Element AutomationId"},
                        "action": {"type": "string", "enum": ["invoke", "set_value"], "default": "invoke"},
                        "value": {"type": "string", "description": "Value to set when action='set_value'"},
                    },
                    "required": ["hwnd"],
                },
            },
            {
                "name": "manage_window",
                "description": "Manages window state: focus, minimize, maximize, restore, show_no_activate, hide, close, move_resize.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "hwnd": {"type": ["integer", "string"], "description": "Target window handle"},
                        "action": {
                            "type": "string",
                            "enum": ["focus", "minimize", "maximize", "restore", "show_no_activate", "hide", "close", "move_resize"],
                        },
                        "x": {"type": "integer"},
                        "y": {"type": "integer"},
                        "width": {"type": "integer"},
                        "height": {"type": "integer"},
                    },
                    "required": ["hwnd", "action"],
                },
            },
        ]

    def execute_tool(self, name: str, args: dict[str, Any]) -> list[dict[str, Any]]:
        """Executes an MCP tool call and returns MCP content blocks (TextContent / ImageContent)."""
        self.safety.check_rate_limit(name)
        self.safety.record_audit(name, args)

        if name == "screen_capture":
            fmt = args.get("format", "jpeg").lower()
            ext = "png" if fmt == "png" else "jpg"
            save_path = str(self.vision.captures_dir / f"screen_{int(time.time() * 1000)}.{ext}")
            call_args = {**args, "save_path": save_path}
            raw = self.core.call("capture_screen", call_args)

            meta = {
                "width": raw["width"],
                "height": raw["height"],
                "offset_x": raw["offset_x"],
                "offset_y": raw["offset_y"],
                "cursor": {"x": raw["cursor_x"], "y": raw["cursor_y"]},
                "frame_hash": raw.get("frame_hash"),
                "diff_percent": raw.get("diff_percent"),
                "screen_changed": raw.get("screen_changed"),
                "saved_path": raw.get("saved_path") or save_path,
            }
            return [
                {"type": "text", "text": json.dumps(meta, ensure_ascii=False, indent=2)},
                {"type": "image", "data": raw["image_base64"], "mimeType": raw.get("mime_type", "image/jpeg")},
            ]

        if name == "window_capture_silent":
            hwnd = args["hwnd"]
            fmt = args.get("format", "jpeg").lower()
            ext = "png" if fmt == "png" else "jpg"
            save_path = str(self.vision.captures_dir / f"window_{int(time.time() * 1000)}.{ext}")

            res = self.core.call(
                "capture_window_silent",
                {
                    "hwnd": hwnd,
                    "annotate_ui": args.get("annotate_ui", True),
                    "draw_grid": args.get("draw_grid", True),
                    "format": fmt,
                    "save_path": save_path,
                },
            )
            raw = res["capture"]
            ui_elements = res.get("ui_elements") or []
            self._last_ui_elements = {el["id"]: el for el in ui_elements}

            meta = {
                "hwnd": hwnd,
                "capture_method": raw["capture_method"],
                "width": raw["width"],
                "height": raw["height"],
                "window_screen_offset": {"x": raw["offset_x"], "y": raw["offset_y"]},
                "frame_hash": raw.get("frame_hash"),
                "diff_percent": raw.get("diff_percent"),
                "screen_changed": raw.get("screen_changed"),
                "saved_path": raw.get("saved_path") or save_path,
                "annotated_ui_elements_count": len(ui_elements),
                "ui_elements": ui_elements[:25],
            }
            return [
                {"type": "text", "text": json.dumps(meta, ensure_ascii=False, indent=2)},
                {"type": "image", "data": raw["image_base64"], "mimeType": raw.get("mime_type", "image/jpeg")},
            ]

        if name == "execute_batch":
            save_path = str(self.vision.captures_dir / f"batch_{int(time.time() * 1000)}.jpg")
            call_args = {**args, "save_path": save_path}
            res = self.core.call("execute_batch", call_args)
            capture = res.pop("capture", None)
            blocks: list[dict[str, Any]] = [
                {"type": "text", "text": json.dumps(res, ensure_ascii=False, indent=2)}
            ]
            if capture:
                blocks.append(
                    {
                        "type": "image",
                        "data": capture["image_base64"],
                        "mimeType": capture.get("mime_type", "image/jpeg"),
                    }
                )
            return blocks

        if name in ("list_windows", "launch_app"):
            res = self.core.call(name, args)
            return [{"type": "text", "text": json.dumps(res, ensure_ascii=False, indent=2)}]

        if name == "inspect_window_ui":
            res = self.core.call("inspect_window_ui", args)
            self._last_ui_elements = {el["id"]: el for el in res}
            return [{"type": "text", "text": json.dumps(res, ensure_ascii=False, indent=2)}]

        if name == "mouse_action":
            act = args.get("action", "click")
            x = args.get("x")
            y = args.get("y")
            el_id = args.get("ui_element_id")
            if el_id is not None and el_id in self._last_ui_elements:
                el = self._last_ui_elements[el_id]
                x = el["center_screen_x"]
                y = el["center_screen_y"]

            duration = args.get("duration_ms", 0)
            if act == "move":
                res = self.core.call("mouse_move", {"x": x, "y": y, "duration_ms": duration})
            elif act in ("click", "double_click", "right_click", "middle_click"):
                button = "right" if act == "right_click" else ("middle" if act == "middle_click" else "left")
                clicks = 2 if act == "double_click" else 1
                res = self.core.call(
                    "mouse_click",
                    {"x": x, "y": y, "button": button, "clicks": clicks, "duration_ms": duration},
                )
            elif act == "drag":
                res = self.core.call(
                    "mouse_drag",
                    {
                        "start_x": x,
                        "start_y": y,
                        "end_x": args["end_x"],
                        "end_y": args["end_y"],
                        "duration_ms": max(30, duration),
                    },
                )
            elif act == "scroll":
                res = self.core.call(
                    "mouse_scroll",
                    {"delta": args.get("scroll_delta", -3), "x": x, "y": y},
                )
            else:
                raise ValueError(f"Unsupported mouse action: {act}")
            return [{"type": "text", "text": json.dumps(res, ensure_ascii=False)}]

        if name == "keyboard_action":
            results = []
            if args.get("text"):
                results.append(
                    self.core.call(
                        "keyboard_type",
                        {
                            "text": args["text"],
                            "delay_ms": args.get("delay_ms", 0),
                            "press_enter": args.get("press_enter", False),
                        },
                    )
                )
            if args.get("hotkey"):
                results.append(self.core.call("keyboard_hotkey", {"keys": args["hotkey"]}))
            return [{"type": "text", "text": json.dumps(results, ensure_ascii=False)}]

        if name in ("silent_click", "silent_type", "silent_automate_element", "manage_window"):
            res = self.core.call(name, args)
            return [{"type": "text", "text": json.dumps(res, ensure_ascii=False, indent=2)}]

        raise ValueError(f"Unknown tool: {name}")

    def run_stdio_loop(self) -> None:
        """Runs standard MCP JSON-RPC 2.0 loop over stdin/stdout."""
        for raw_line in sys.stdin:
            line = raw_line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue

            req_id = msg.get("id")
            method = msg.get("method", "")
            params = msg.get("params") or {}

            if req_id is None:
                continue

            try:
                if method == "initialize":
                    result = {
                        "protocolVersion": "2024-11-05",
                        "capabilities": {"tools": {"listChanged": False}},
                        "serverInfo": {"name": "antigravity-bridge", "version": "0.2.0-turbo"},
                    }
                elif method == "tools/list":
                    result = {"tools": self.get_mcp_tools_schema()}
                elif method == "tools/call":
                    tool_name = params.get("name", "")
                    tool_args = params.get("arguments") or {}
                    content = self.execute_tool(tool_name, tool_args)
                    result = {"content": content, "isError": False}
                elif method == "ping":
                    result = {}
                else:
                    raise ValueError(f"Unsupported MCP method: {method}")

                resp = {"jsonrpc": "2.0", "id": req_id, "result": result}
            except Exception as exc:
                resp = {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "result": {
                        "content": [{"type": "text", "text": f"Error: {exc}"}],
                        "isError": True,
                    },
                }

            sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
            sys.stdout.flush()


def main() -> None:
    server = AntigravityBridgeServer()
    try:
        server.run_stdio_loop()
    finally:
        server.core.shutdown()


if __name__ == "__main__":
    main()
