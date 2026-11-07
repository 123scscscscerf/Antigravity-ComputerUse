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
/// Supports persistent streaming connections as well as single-shot CLI execution.
/// </summary>
public sealed class NamedPipeRpcServer
{
    private readonly ScreenCaptureEngine _capture = new();
    private readonly GlobalInputEngine _input = new();
    private readonly SilentModeEngine _silent = new();

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

                    string responseJson = ProcessRawJsonRequest(line);
                    await writer.WriteLineAsync(responseJson);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Pipe closed by client
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AntigravityBridge.Core] Pipe client error: {ex.Message}");
            }
        }
    }

    public string ProcessRawJsonRequest(string jsonLine)
    {
        Win32.EnsureDefaultDesktop();
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
                engine = "AntigravityBridge.Core (.NET 8.0 Windows)",
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            },

            // --- Vision & Screen Capture ---
            "get_monitors" => _capture.GetMonitors(),

            "capture_screen" => _capture.CaptureScreen(
                monitorIndex: GetInt(p, "monitor_index") ?? 0,
                regionX: GetInt(p, "x"),
                regionY: GetInt(p, "y"),
                regionW: GetInt(p, "width"),
                regionH: GetInt(p, "height")),

            "capture_window_silent" => _capture.CaptureWindowSilent(
                hwndValue: GetRequiredLong(p, "hwnd"),
                clientOnly: GetBool(p, "client_only") ?? false),

            // --- Global Mouse & Keyboard Input ---
            "mouse_move" => _input.MoveMouse(
                x: GetRequiredInt(p, "x"),
                y: GetRequiredInt(p, "y"),
                durationMs: GetInt(p, "duration_ms") ?? 120),

            "mouse_click" => _input.MouseClick(
                x: GetInt(p, "x"),
                y: GetInt(p, "y"),
                button: GetString(p, "button") ?? "left",
                clicks: GetInt(p, "clicks") ?? 1,
                durationMs: GetInt(p, "duration_ms") ?? 80),

            "mouse_drag" => _input.MouseDrag(
                startX: GetRequiredInt(p, "start_x"),
                startY: GetRequiredInt(p, "start_y"),
                endX: GetRequiredInt(p, "end_x"),
                endY: GetRequiredInt(p, "end_y"),
                button: GetString(p, "button") ?? "left",
                durationMs: GetInt(p, "duration_ms") ?? 250),

            "mouse_scroll" => _input.MouseScroll(
                delta: GetRequiredInt(p, "delta"),
                horizontal: GetBool(p, "horizontal") ?? false,
                x: GetInt(p, "x"),
                y: GetInt(p, "y")),

            "keyboard_type" => _input.TypeText(
                text: GetRequiredString(p, "text"),
                delayMs: GetInt(p, "delay_ms") ?? 5,
                pressEnter: GetBool(p, "press_enter") ?? false),

            "keyboard_hotkey" => _input.SendHotkey(
                keys: GetRequiredString(p, "keys")),

            // --- Silent-Mode & Window Automation ---
            "list_windows" => _silent.ListWindows(
                visibleOnly: GetBool(p, "visible_only") ?? true,
                filter: GetString(p, "filter")),

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

            _ => throw new ArgumentException($"Unknown RPC method: '{method}'")
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
