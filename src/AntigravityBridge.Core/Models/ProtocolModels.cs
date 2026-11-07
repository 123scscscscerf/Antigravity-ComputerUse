using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntigravityBridge.Core.Models;

public sealed class IpcRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public sealed class IpcResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    public static IpcResponse Success(string id, object? result) =>
        new() { Id = id, Ok = true, Result = result };

    public static IpcResponse Failure(string id, string error) =>
        new() { Id = id, Ok = false, Error = error };
}

public sealed class MonitorInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public sealed class CaptureResult
{
    [JsonPropertyName("image_base64")]
    public string ImageBase64 { get; set; } = string.Empty;

    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "image/jpeg";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("offset_x")]
    public int OffsetX { get; set; }

    [JsonPropertyName("offset_y")]
    public int OffsetY { get; set; }

    [JsonPropertyName("capture_method")]
    public string CaptureMethod { get; set; } = string.Empty;

    [JsonPropertyName("cursor_x")]
    public int CursorX { get; set; }

    [JsonPropertyName("cursor_y")]
    public int CursorY { get; set; }

    [JsonPropertyName("frame_hash")]
    public string FrameHash { get; set; } = string.Empty;

    [JsonPropertyName("diff_percent")]
    public double DiffPercent { get; set; }

    [JsonPropertyName("screen_changed")]
    public bool ScreenChanged { get; set; }

    [JsonPropertyName("saved_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SavedPath { get; set; }
}

public sealed class WindowInfo
{
    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("hwnd_hex")]
    public string HwndHex { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("class_name")]
    public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("process_id")]
    public uint ProcessId { get; set; }

    [JsonPropertyName("process_name")]
    public string ProcessName { get; set; } = string.Empty;

    [JsonPropertyName("is_visible")]
    public bool IsVisible { get; set; }

    [JsonPropertyName("is_minimized")]
    public bool IsMinimized { get; set; }

    [JsonPropertyName("is_maximized")]
    public bool IsMaximized { get; set; }

    [JsonPropertyName("is_foreground")]
    public bool IsForeground { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public sealed class UiElementInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("automation_id")]
    public string AutomationId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("control_type")]
    public string ControlType { get; set; } = string.Empty;

    [JsonPropertyName("class_name")]
    public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("screen_x")]
    public int ScreenX { get; set; }

    [JsonPropertyName("screen_y")]
    public int ScreenY { get; set; }

    [JsonPropertyName("rel_x")]
    public int RelX { get; set; }

    [JsonPropertyName("rel_y")]
    public int RelY { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("center_screen_x")]
    public int CenterScreenX => ScreenX + Width / 2;

    [JsonPropertyName("center_screen_y")]
    public int CenterScreenY => ScreenY + Height / 2;

    [JsonPropertyName("center_rel_x")]
    public int CenterRelX => RelX + Width / 2;

    [JsonPropertyName("center_rel_y")]
    public int CenterRelY => RelY + Height / 2;

    [JsonPropertyName("supported_patterns")]
    public List<string> SupportedPatterns { get; set; } = [];

    [JsonPropertyName("current_value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentValue { get; set; }
}
