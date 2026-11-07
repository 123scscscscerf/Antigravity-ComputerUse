using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AntigravityBridge.Core.Capture;
using AntigravityBridge.Core.Input;
using AntigravityBridge.Core.Models;
using AntigravityBridge.Core.Native;
using AntigravityBridge.Core.Silent;

namespace AntigravityBridge.Core.Ipc;

/// <summary>
/// High-speed Named Pipe RPC server (\\.\pipe\AntigravityBridge) using newline-delimited JSON.
/// Executes all Win32/GDI/UIA operations on a dedicated desktop-bound STA thread so SetThreadDesktop("default")
/// succeeds before any HWND/GDI hook is created on the thread.
/// </summary>
public sealed class NamedPipeRpcServer
{
    private readonly ScreenCaptureEngine _capture = new();
    private readonly GlobalInputEngine _input = new();
    private readonly SilentModeEngine _silent = new();
    private readonly BlockingCollection<(string Json, TaskCompletionSource<string> Tcs)> _workQueue = new();
    private readonly Thread _desktopWorkerThread;

    public NamedPipeRpcServer()
    {
        _desktopWorkerThread = new Thread(DesktopWorkerLoop)
        {
            Name = "AntigravityDesktopWorker",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _desktopWorkerThread.Start();
    }

    private void DesktopWorkerLoop()
    {
        // Bind this dedicated thread to WinSta0\default BEFORE any GDI+ or UIA window is ever created on it!
        Win32.EnsureDefaultDesktop();
        try
        {
            Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        foreach (var (json, tcs) in _workQueue.GetConsumingEnumerable())
        {
            try
            {
                string res = ProcessRawJsonRequestInternal(json);
                tcs.TrySetResult(res);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }

    public async Task RunPipeServerAsync(string pipeName, CancellationToken ct)
    {
        Console.Error.WriteLine($"[AntigravityBridge.Core] Listening on Named Pipe: \\\\.\\pipe\\{pipeName}");

        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(ct);
                _ = HandleClientAsync(server, ct);
            }
            catch
            {
                await server.DisposeAsync();
                if (ct.IsCancellationRequested)
                    break;
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var reader = new StreamReader(stream, utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
                await using var writer = new StreamWriter(stream, utf8NoBom, bufferSize: 65536, leaveOpen: true)
                {
                    AutoFlush = true
                };

                while (stream.IsConnected && !ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    string responseJson = await EnqueueRequestAsync(line);
                    await writer.WriteLineAsync(responseJson);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AntigravityBridge.Core] Pipe client error: {ex.Message}");
            }
        }
    }

    public Task<string> EnqueueRequestAsync(string jsonLine)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workQueue.Add((jsonLine, tcs));
        return tcs.Task;
    }

    public string ProcessRawJsonRequest(string jsonLine) =>
        EnqueueRequestAsync(jsonLine).GetAwaiter().GetResult();

    private string ProcessRawJsonRequestInternal(string jsonLine)
    {
        string reqId = "unknown";
        try
        {
            var req = JsonSerializer.Deserialize<IpcRequest>(jsonLine);
            if (req == null)
                return JsonSerializer.Serialize(IpcResponse.Failure(reqId, "Null IPC request payload."));

            reqId = req.Id;
            object? result = Dispatch(req.Method, req.Params);
            return JsonSerializer.Serialize(IpcResponse.Success(reqId, result));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(IpcResponse.Failure(reqId, ex.Message));
        }
    }

    private object? Dispatch(string method, JsonElement? p)
    {
        return method switch
        {
            "ping" => new
            {
                status = "pong",
                engine = "AntigravityBridge.Core (.NET 8.0 Turbo)",
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            },

            // --- Vision & Screen Capture (Native C# GDI+ Overlays & JPEG/PNG) ---
            "get_monitors" => _capture.GetMonitors(),

            "capture_screen" => _capture.CaptureScreen(
                monitorIndex: GetInt(p, "monitor_index") ?? 0,
                regionX: GetInt(p, "x"),
                regionY: GetInt(p, "y"),
                regionW: GetInt(p, "width"),
                regionH: GetInt(p, "height"),
                drawGrid: GetBool(p, "draw_grid") ?? true,
                gridStep: GetInt(p, "grid_step") ?? 200,
                format: GetString(p, "format") ?? "jpeg",
                quality: GetInt(p, "quality") ?? 82,
                savePath: GetString(p, "save_path")),

            "capture_window_silent" => CaptureWindowWithOptionalSoM(p),

            // --- Global Mouse & Keyboard Input (Zero-Latency Batched SendInput) ---
            "mouse_move" => _input.MoveMouse(
                x: GetRequiredInt(p, "x"),
                y: GetRequiredInt(p, "y"),
                durationMs: GetInt(p, "duration_ms") ?? 0),

            "mouse_click" => _input.MouseClick(
                x: GetInt(p, "x"),
                y: GetInt(p, "y"),
                button: GetString(p, "button") ?? "left",
                clicks: GetInt(p, "clicks") ?? 1,
                durationMs: GetInt(p, "duration_ms") ?? 0),

            "mouse_drag" => _input.MouseDrag(
                startX: GetRequiredInt(p, "start_x"),
                startY: GetRequiredInt(p, "start_y"),
                endX: GetRequiredInt(p, "end_x"),
                endY: GetRequiredInt(p, "end_y"),
                button: GetString(p, "button") ?? "left",
                durationMs: GetInt(p, "duration_ms") ?? 100),

            "mouse_scroll" => _input.MouseScroll(
                delta: GetRequiredInt(p, "delta"),
                horizontal: GetBool(p, "horizontal") ?? false,
                x: GetInt(p, "x"),
                y: GetInt(p, "y")),

            "keyboard_type" => _input.TypeText(
                text: GetRequiredString(p, "text"),
                delayMs: GetInt(p, "delay_ms") ?? 0,
                pressEnter: GetBool(p, "press_enter") ?? false),

            "keyboard_hotkey" => _input.SendHotkey(
                keys: GetRequiredString(p, "keys")),

            // --- Silent-Mode & Window Automation ---
            "list_windows" => _silent.ListWindows(
                visibleOnly: GetBool(p, "visible_only") ?? true,
                filter: GetString(p, "filter")),

            "wait_window" => _silent.WaitForWindow(
                filter: GetRequiredString(p, "filter"),
                timeoutMs: GetInt(p, "timeout_ms") ?? 3000,
                focus: GetBool(p, "focus") ?? false),

            "launch_app" => _silent.LaunchApp(
                fileName: GetRequiredString(p, "file_name"),
                arguments: GetString(p, "arguments"),
                hidden: GetBool(p, "hidden") ?? false,
                waitFilter: GetString(p, "wait_filter"),
                timeoutMs: GetInt(p, "timeout_ms") ?? 3000),

            "manage_window" => _silent.ManageWindow(
                hwndValue: GetRequiredLong(p, "hwnd"),
                action: GetRequiredString(p, "action"),
                x: GetInt(p, "x"),
                y: GetInt(p, "y"),
                width: GetInt(p, "width"),
                height: GetInt(p, "height")),

            "silent_click" => _silent.SilentClick(
                hwndValue: GetRequiredLong(p, "hwnd"),
                relX: GetRequiredInt(p, "rel_x"),
                relY: GetRequiredInt(p, "rel_y"),
                button: GetString(p, "button") ?? "left",
                doubleClick: GetBool(p, "double_click") ?? false),

            "silent_type" => _silent.SilentType(
                hwndValue: GetRequiredLong(p, "hwnd"),
                text: GetString(p, "text"),
                key: GetString(p, "key"),
                pressEnter: GetBool(p, "press_enter") ?? false),

            "inspect_window_ui" => _silent.InspectWindowUi(
                hwndValue: GetRequiredLong(p, "hwnd"),
                maxElements: GetInt(p, "max_elements") ?? 80),

            "silent_automate_element" => _silent.SilentAutomateElement(
                hwndValue: GetRequiredLong(p, "hwnd"),
                name: GetString(p, "name"),
                automationId: GetString(p, "automation_id"),
                action: GetString(p, "action") ?? "invoke",
                value: GetString(p, "value")),

            // --- Turbo Batch Macro Execution (Multiple actions + verification in 1 turn!) ---
            "execute_batch" => ExecuteBatch(p),

            _ => throw new ArgumentException($"Unknown RPC method: '{method}'")
        };
    }

    private object CaptureWindowWithOptionalSoM(JsonElement? p)
    {
        long hwnd = GetRequiredLong(p, "hwnd");
        bool annotateUi = GetBool(p, "annotate_ui") ?? true;
        List<UiElementInfo>? uiElements = null;

        if (annotateUi)
        {
            uiElements = _silent.InspectWindowUi(hwnd, maxElements: GetInt(p, "max_elements") ?? 60);
        }

        var cap = _capture.CaptureWindowSilent(
            hwndValue: hwnd,
            clientOnly: GetBool(p, "client_only") ?? false,
            drawGrid: GetBool(p, "draw_grid") ?? true,
            gridStep: GetInt(p, "grid_step") ?? 150,
            format: GetString(p, "format") ?? "jpeg",
            quality: GetInt(p, "quality") ?? 82,
            savePath: GetString(p, "save_path"),
            uiElements: uiElements);

        return new
        {
            capture = cap,
            ui_elements = uiElements ?? []
        };
    }

    private object ExecuteBatch(JsonElement? p)
    {
        var sw = Stopwatch.StartNew();
        var stepResults = new List<object?>();
        long? lastResolvedHwnd = null;

        if (p is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("steps", out var stepsEl) &&
            stepsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in stepsEl.EnumerateArray())
            {
                string op = GetString(step, "op") ?? GetString(step, "method") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(op))
                    continue;

                if (op.Equals("sleep", StringComparison.OrdinalIgnoreCase))
                {
                    int ms = GetInt(step, "ms") ?? 50;
                    Thread.Sleep(Math.Clamp(ms, 1, 5000));
                    stepResults.Add(new { op = "sleep", ms });
                    continue;
                }

                // If step needs hwnd and doesn't specify one, automatically bind to lastResolvedHwnd from wait_window / launch_app
                JsonElement effectiveStep = step;
                if (lastResolvedHwnd.HasValue && GetLong(step, "hwnd") == null &&
                    op is "manage_window" or "silent_click" or "silent_type" or "silent_automate_element" or "inspect_window_ui")
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(step.GetRawText()) ?? [];
                    dict["hwnd"] = lastResolvedHwnd.Value;
                    effectiveStep = JsonSerializer.SerializeToElement(dict);
                }

                object? res = Dispatch(op, effectiveStep);
                if (res is WindowInfo winInfo)
                {
                    lastResolvedHwnd = winInfo.Hwnd;
                }
                stepResults.Add(new { op, result = res });
            }
        }

        CaptureResult? finalCapture = null;
        if (GetBool(p, "capture_after") ?? false)
        {
            int settleMs = GetInt(p, "settle_ms") ?? 40;
            if (settleMs > 0)
                Thread.Sleep(Math.Clamp(settleMs, 1, 1000));

            finalCapture = _capture.CaptureScreen(
                monitorIndex: GetInt(p, "monitor_index") ?? 0,
                drawGrid: GetBool(p, "draw_grid") ?? true,
                gridStep: GetInt(p, "grid_step") ?? 200,
                format: GetString(p, "format") ?? "jpeg",
                quality: GetInt(p, "quality") ?? 82,
                savePath: GetString(p, "save_path"));
        }

        sw.Stop();
        return new
        {
            elapsed_ms = sw.Elapsed.TotalMilliseconds,
            steps_executed = stepResults.Count,
            last_hwnd = lastResolvedHwnd,
            steps = stepResults,
            capture = finalCapture
        };
    }

    private static int? GetInt(JsonElement? p, string key)
    {
        if (p is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int v))
                return v;
        }
        return null;
    }

    private static int GetRequiredInt(JsonElement? p, string key) =>
        GetInt(p, key) ?? throw new ArgumentException($"Missing required integer parameter '{key}'.");

    private static long? GetLong(JsonElement? p, string key)
    {
        if (p is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt64(out long v))
                return v;
            if (val.ValueKind == JsonValueKind.String)
            {
                string s = val.GetString() ?? string.Empty;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
                    return hexVal;
                if (long.TryParse(s, out long decVal))
                    return decVal;
            }
        }
        return null;
    }

    private static long GetRequiredLong(JsonElement? p, string key) =>
        GetLong(p, key) ?? throw new ArgumentException($"Missing required handle/long parameter '{key}'.");

    private static bool? GetBool(JsonElement? p, string key)
    {
        if (p is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var val))
        {
            if (val.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return val.GetBoolean();
        }
        return null;
    }

    private static string? GetString(JsonElement? p, string key)
    {
        if (p is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.String)
                return val.GetString();
        }
        return null;
    }

    private static string GetRequiredString(JsonElement? p, string key) =>
        GetString(p, key) ?? throw new ArgumentException($"Missing required string parameter '{key}'.");
}
