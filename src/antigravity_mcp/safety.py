"""Safety guard, audit logging, and process protection for Antigravity Bridge."""

from __future__ import annotations

import json
import time
from pathlib import Path
from typing import Any


class SafetyGuard:
    """Enforces rate limits, protected process policies, and structured audit logging."""

    PROTECTED_PROCESSES = frozenset({
        "csrss",
        "wininit",
        "winlogon",
        "lsass",
        "services",
        "smss",
    })

    def __init__(self, log_dir: Path | None = None, max_actions_per_minute: int = 120) -> None:
        self.max_actions_per_minute = max_actions_per_minute
        self._action_timestamps: list[float] = []
        self.log_dir = log_dir or (Path(__file__).resolve().parents[2] / "logs")
        self.log_dir.mkdir(parents=True, exist_ok=True)
        self.audit_file = self.log_dir / "audit.jsonl"

    def check_rate_limit(self, action_name: str) -> None:
        now = time.monotonic()
        cutoff = now - 60.0
        self._action_timestamps = [t for t in self._action_timestamps if t >= cutoff]
        if len(self._action_timestamps) >= self.max_actions_per_minute:
            raise RuntimeError(
                f"Safety rate limit exceeded ({self.max_actions_per_minute} actions/min) while calling '{action_name}'."
            )
        self._action_timestamps.append(now)

    def check_process_allowed(self, process_name: str | None) -> None:
        if not process_name:
            return
        normalized = process_name.lower().removesuffix(".exe")
        if normalized in self.PROTECTED_PROCESSES:
            raise PermissionError(
                f"Access denied: process '{process_name}' is a protected Windows system process."
            )

    def record_audit(self, tool_name: str, params: dict[str, Any], status: str = "ok") -> None:
        entry = {
            "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "tool": tool_name,
            "params": params,
            "status": status,
        }
        try:
            with self.audit_file.open("a", encoding="utf-8") as f:
                f.write(json.dumps(entry, ensure_ascii=False) + "\n")
        except OSError:
            pass
