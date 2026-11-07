"""End-to-end integration test for Antigravity Bridge (Python MCP <-> Named Pipe <-> C# .NET 8 Core)."""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from antigravity_mcp.server import AntigravityBridgeServer


def run_smoke_tests() -> None:
    server = AntigravityBridgeServer()
    try:
        # 1. Ping C# Core over Named Pipe
        ping_res = server.core.call("ping")
        assert ping_res["status"] == "pong", f"Unexpected ping response: {ping_res}"
        print(f"[PASS] Named Pipe IPC Ping -> {ping_res['engine']}")

        # 2. Enumerate Monitors
        monitors = server.core.call("get_monitors")
        assert isinstance(monitors, list) and len(monitors) >= 1
        print(f"[PASS] Monitors detected: {len(monitors)} ({monitors[0]['width']}x{monitors[0]['height']})")

        # 3. Screen Capture + Vision Grid Pipeline
        capture_blocks = server.execute_tool("screen_capture", {"draw_grid": True, "grid_step": 250})
        meta = json.loads(capture_blocks[0]["text"])
        assert meta["width"] > 0 and meta["height"] > 0
        assert len(capture_blocks[1]["data"]) > 100
        print(f"[PASS] Screen Capture + Vision Grid -> {meta['width']}x{meta['height']} saved to {meta['saved_path']}")

        # 4. List Open Windows (Silent-Mode discovery)
        win_blocks = server.execute_tool("list_windows", {"visible_only": True})
        windows = json.loads(win_blocks[0]["text"])
        if not windows:
            # In non-interactive / sandboxed test runners, query all session windows
            win_blocks = server.execute_tool("list_windows", {"visible_only": False})
            windows = json.loads(win_blocks[0]["text"])

        assert isinstance(windows, list) and len(windows) >= 1
        sized_windows = [w for w in windows if w["width"] > 0 and w["height"] > 0]
        sample_win = sized_windows[0] if sized_windows else windows[0]
        print(
            f"[PASS] Window Enumeration -> Found {len(windows)} windows "
            f"(sample: '{sample_win['title'] or sample_win['class_name']}' [{sample_win['hwnd_hex']}])"
        )

        # 5. Silent Window Capture + UI Automation Inspection
        if sample_win["width"] > 0 and sample_win["height"] > 0:
            silent_cap = server.execute_tool(
                "window_capture_silent",
                {"hwnd": sample_win["hwnd"], "annotate_ui": True, "draw_grid": True},
            )
            silent_meta = json.loads(silent_cap[0]["text"])
            print(
                f"[PASS] Silent Window Capture ({silent_meta['capture_method']}) -> "
                f"{silent_meta['width']}x{silent_meta['height']}, "
                f"UI Elements Annotated: {silent_meta['annotated_ui_elements_count']}"
            )
        else:
            ui_blocks = server.execute_tool("inspect_window_ui", {"hwnd": sample_win["hwnd"]})
            ui_elements = json.loads(ui_blocks[0]["text"])
            print(f"[PASS] Silent UI Automation Inspection -> {len(ui_elements)} elements inspected")

    finally:
        server.core.shutdown()


if __name__ == "__main__":
    run_smoke_tests()
