"""Named Pipe IPC Client and lifecycle manager for the C# AntigravityBridge.Core host."""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import json
import subprocess
import time
import uuid
from pathlib import Path
from typing import Any

# Win32 Named Pipe / File constants via built-in ctypes.windll.kernel32
_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

_GENERIC_READ = 0x80000000
_GENERIC_WRITE = 0x40000000
_OPEN_EXISTING = 3
_INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value

_CreateFileW = _kernel32.CreateFileW
_CreateFileW.argtypes = [
    wintypes.LPCWSTR,
    wintypes.DWORD,
    wintypes.DWORD,
    wintypes.LPVOID,
    wintypes.DWORD,
    wintypes.DWORD,
    wintypes.HANDLE,
]
_CreateFileW.restype = wintypes.HANDLE

_WriteFile = _kernel32.WriteFile
_WriteFile.argtypes = [
    wintypes.HANDLE,
    wintypes.LPCVOID,
    wintypes.DWORD,
     ctypes.POINTER(wintypes.DWORD),
    wintypes.LPVOID,
]
_WriteFile.restype = wintypes.BOOL

_ReadFile = _kernel32.ReadFile
_ReadFile.argtypes = [
    wintypes.HANDLE,
    wintypes.LPVOID,
    wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD),
    wintypes.LPVOID,
]
_ReadFile.restype = wintypes.BOOL

_CloseHandle = _kernel32.CloseHandle
_CloseHandle.argtypes = [wintypes.HANDLE]
_CloseHandle.restype = wintypes.BOOL


class CoreBridgeClient:
    """Connects to AntigravityBridge.Core over a persistent Windows Named Pipe (\\\\.\\pipe\\AntigravityBridge).

    Uses direct Win32 Kernel32 pipe handles via ctypes for sub-millisecond RPC and automatic C# host spawning.
    """

    def __init__(self, pipe_name: str = "AntigravityBridge", core_exe_path: Path | None = None) -> None:
        self.pipe_name = pipe_name
        self.pipe_path = rf"\\.\pipe\{pipe_name}"
        self.core_exe_path = core_exe_path or self._discover_core_binary()
        self._proc: subprocess.Popen[bytes] | None = None
        self._handle: int | None = None

    @staticmethod
    def _discover_core_binary() -> Path:
        repo_root = Path(__file__).resolve().parents[2]
        candidates = [
            repo_root / "src" / "AntigravityBridge.Core" / "bin" / "Release" / "net8.0-windows" / "AntigravityBridge.Core.exe",
            repo_root / "src" / "AntigravityBridge.Core" / "bin" / "Debug" / "net8.0-windows" / "AntigravityBridge.Core.exe",
        ]
        for candidate in candidates:
            if candidate.exists():
                return candidate
        return candidates[0]

    def _try_open_pipe(self) -> int | None:
        handle = _CreateFileW(
            self.pipe_path,
            _GENERIC_READ | _GENERIC_WRITE,
            0,
            None,
            _OPEN_EXISTING,
            0,
            None,
        )
        if handle == _INVALID_HANDLE_VALUE or handle is None:
            return None
        return int(handle)

    def ensure_connected(self) -> int:
        if self._handle is not None:
            return self._handle

        handle = self._try_open_pipe()
        if handle is not None:
            self._handle = handle
            return handle

        if not self.core_exe_path.exists():
            raise FileNotFoundError(
                f"AntigravityBridge.Core binary not found at '{self.core_exe_path}'. "
                "Build it first with: dotnet build src/AntigravityBridge.Core -c Release"
            )

        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0x08000000)
        self._proc = subprocess.Popen(
            [str(self.core_exe_path), "--pipe-name", self.pipe_name],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            creationflags=creation_flags,
        )

        deadline = time.monotonic() + 6.0
        while time.monotonic() < deadline:
            handle = self._try_open_pipe()
            if handle is not None:
                self._handle = handle
                return handle
            time.sleep(0.04)

        raise TimeoutError(f"Timed out waiting for Named Pipe '{self.pipe_path}'.")

    def call(self, method: str, params: dict[str, Any] | None = None) -> Any:
        """Executes a synchronous JSON-RPC call over the persistent Windows Named Pipe (<1 ms overhead)."""
        handle = self.ensure_connected()

        req_id = uuid.uuid4().hex[:12]
        payload = {
            "id": req_id,
            "method": method,
            "params": params or {},
        }
        raw_request = (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")

        written = wintypes.DWORD(0)
        ok = _WriteFile(handle, raw_request, len(raw_request), ctypes.byref(written), None)
        if not ok:
            self._close_pipe()
            handle = self.ensure_connected()
            ok = _WriteFile(handle, raw_request, len(raw_request), ctypes.byref(written), None)
            if not ok:
                raise OSError(f"Failed to write to Named Pipe '{self.pipe_path}' (WinError {ctypes.get_last_error()}).")

        buf = ctypes.create_string_buffer(65536)
        chunks: list[bytes] = []
        while True:
            read_bytes = wintypes.DWORD(0)
            ok = _ReadFile(handle, buf, len(buf), ctypes.byref(read_bytes), None)
            if not ok or read_bytes.value == 0:
                break
            chunk = buf.raw[: read_bytes.value]
            chunks.append(chunk)
            if b"\n" in chunk:
                break

        raw_line = b"".join(chunks).split(b"\n", 1)[0].decode("utf-8")
        if not raw_line:
            self._close_pipe()
            raise RuntimeError(f"Empty response from C# Core on method '{method}'.")

        response = json.loads(raw_line)
        if not response.get("ok", False):
            raise RuntimeError(f"Core Error [{method}]: {response.get('error', 'Unknown error')}")

        return response.get("result")

    def _close_pipe(self) -> None:
        if self._handle is not None:
            _CloseHandle(self._handle)
            self._handle = None

    def shutdown(self) -> None:
        self._close_pipe()
        if self._proc and self._proc.poll() is None:
            self._proc.terminate()
            try:
                self._proc.wait(timeout=2.0)
            except subprocess.TimeoutExpired:
                self._proc.kill()
        self._proc = None
