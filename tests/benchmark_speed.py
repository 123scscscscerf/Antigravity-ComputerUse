"""Benchmark suite measuring latency (ms) of Antigravity Bridge v0.2 Turbo vs v0.1 & Claude Computer-Use baseline."""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from antigravity_mcp.server import AntigravityBridgeServer


def measure(label: str, fn) -> tuple[float, object]:
    t0 = time.perf_counter()
    res = fn()
    dt_ms = (time.perf_counter() - t0) * 1000.0
    print(f"  {label:<50} : {dt_ms:7.2f} ms")
    return dt_ms, res


def main() -> None:
    server = AntigravityBridgeServer()
    try:
        # Warmup .NET JIT & caches once
        server.core.call("ping")
        server.execute_tool("list_windows", {"visible_only": True})
        server.execute_tool("screen_capture", {"draw_grid": True, "grid_step": 200})

        print("=== Antigravity Bridge v0.2 Turbo Latency Benchmark (Warm JIT) ===")

        measure("1. Named Pipe RPC Ping", lambda: server.core.call("ping"))
        _, wins_raw = measure(
            "2. List Windows (Kernel32 + PID Cache)",
            lambda: server.execute_tool("list_windows", {"visible_only": True}),
        )
        wins = json.loads(wins_raw[0]["text"])

        _, cap_res = measure(
            "3. Full Screen Capture + Grid + Diff (1080p JPEG)",
            lambda: server.execute_tool("screen_capture", {"draw_grid": True, "grid_step": 200}),
        )
        meta = json.loads(cap_res[0]["text"])
        img_b64_kb = len(cap_res[1]["data"]) / 1024.0
        print(
            f"     -> Payload: {img_b64_kb:.1f} KB | Hash: {meta['frame_hash']} | "
            f"Diff: {meta['diff_percent']}% (changed={meta['screen_changed']})"
        )

        if wins:
            target_hwnd = wins[0]["hwnd"]
            measure(
                f"4. Bulk UIA CacheRequest Inspect ({wins[0]['process_name']})",
                lambda: server.execute_tool("inspect_window_ui", {"hwnd": target_hwnd}),
            )
            measure(
                f"5. Silent Window Capture + SoM ({wins[0]['process_name']})",
                lambda: server.execute_tool("window_capture_silent", {"hwnd": target_hwnd, "annotate_ui": True}),
            )

        # 6. Benchmark execute_batch (Multi-Step Macro + Final Verification Capture in 1 Turn)
        measure(
            "6. Turbo Batch (3 ops + 1080p Verification Capture)",
            lambda: server.execute_tool(
                "execute_batch",
                {
                    "steps": [
                        {"op": "list_windows", "visible_only": True},
                        {"op": "mouse_move", "x": 500, "y": 500, "duration_ms": 0},
                    ],
                    "capture_after": True,
                    "settle_ms": 5,
                },
            ),
        )

    finally:
        server.core.shutdown()


if __name__ == "__main__":
    main()
